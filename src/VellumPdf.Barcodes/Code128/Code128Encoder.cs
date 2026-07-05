// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Code128;

/// <summary>
/// Encodes Code 128 (ISO/IEC 15417) and GS1-128, choosing subsets A/B/C by the GS1 General
/// Specifications heuristics (mirrored on Wikipedia's Code 128 article): Code Set C absorbs
/// runs of 4+ digits at the start/end of the data, 6+ in the middle, or the whole data when it
/// is entirely 2 or 4+ digits; a single character needing the other basic subset is reached
/// with a Shift rather than a full switch; U+001D (GS) always becomes FNC1.
/// </summary>
internal static class Code128Encoder
{
    private enum Mode { A, B, C }

    private enum ItemKind { Char, CPair, Fnc1 }

    private sealed class Item
    {
        public required ItemKind Kind { get; init; }
        public char CharValue { get; init; }
        public int Digit1 { get; init; }
        public int Digit2 { get; init; }

        public static Item Char(char c) => new() { Kind = ItemKind.Char, CharValue = c };
        public static Item CPair(int d1, int d2) => new() { Kind = ItemKind.CPair, Digit1 = d1, Digit2 = d2 };
        public static Item Fnc1() => new() { Kind = ItemKind.Fnc1 };
    }

    /// <summary>Validates that <paramref name="content"/> is ASCII (0-127), returning it unchanged.</summary>
    /// <exception cref="ArgumentException">A character falls outside 0-127.</exception>
    internal static string Validate(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var c in content)
            if (c > 127)
                throw new ArgumentException($"Code 128 content must be ASCII (0-127); found U+{(int)c:X4}.", nameof(content));
        return content;
    }

    /// <summary>Encodes a <see cref="Code128Barcode"/> to module runs.</summary>
    internal static Encoded1D Encode(Code128Barcode barcode)
    {
        var (startValue, dataSymbols, check) = EncodeSymbols(barcode);

        var runs = new List<double>();
        AppendSymbolWidths(runs, startValue);
        foreach (var symbol in dataSymbols) AppendSymbolWidths(runs, symbol);
        AppendSymbolWidths(runs, check);
        foreach (var w in Code128Tables.StopWidths) runs.Add(w);

        return new Encoded1D
        {
            Runs = runs,
            QuietZoneLeft = 10,
            QuietZoneRight = 10,
        };
    }

    /// <summary>
    /// Encodes a <see cref="Code128Barcode"/> to its symbol sequence — the start code, data
    /// symbol values (subset switches, shifts, FNC1 and Code C pairs included) and the check
    /// character — before conversion to module widths. Exposed separately so the subset
    /// heuristics and check-character arithmetic can be verified directly against published
    /// worked examples.
    /// </summary>
    internal static (int StartValue, IReadOnlyList<int> DataSymbols, int Check) EncodeSymbols(Code128Barcode barcode)
    {
        var items = Tokenize(barcode.Content);
        if (barcode.Gs1) items.Insert(0, Item.Fnc1());

        var useSubsetB = DetermineUseSubsetB(items);
        var homeMode = useSubsetB ? Mode.B : Mode.A;

        var firstDataIndex = 0;
        while (firstDataIndex < items.Count && items[firstDataIndex].Kind == ItemKind.Fnc1) firstDataIndex++;

        Mode currentMode;
        int startValue;
        if (firstDataIndex < items.Count && items[firstDataIndex].Kind == ItemKind.CPair)
        {
            startValue = 105; // Start C
            currentMode = Mode.C;
        }
        else
        {
            startValue = homeMode == Mode.B ? 104 : 103; // Start B / Start A
            currentMode = homeMode;
        }

        var symbols = new List<int>();
        var i = 0;
        while (i < items.Count)
        {
            var item = items[i];
            switch (item.Kind)
            {
                case ItemKind.Fnc1:
                    symbols.Add(102);
                    i++;
                    break;

                case ItemKind.CPair:
                    if (currentMode != Mode.C)
                    {
                        symbols.Add(99); // Code C
                        currentMode = Mode.C;
                    }

                    symbols.Add((item.Digit1 * 10) + item.Digit2);
                    i++;
                    break;

                case ItemKind.Char:
                    if (currentMode == Mode.C)
                    {
                        symbols.Add(homeMode == Mode.B ? 100 : 101); // Code B / Code A
                        currentMode = homeMode;
                    }

                    if (IsCompatible(item.CharValue, currentMode))
                    {
                        symbols.Add(MapValue(item.CharValue, currentMode));
                        i++;
                        break;
                    }

                    var runEnd = i;
                    while (runEnd < items.Count && items[runEnd].Kind == ItemKind.Char
                                                 && !IsCompatible(items[runEnd].CharValue, currentMode))
                        runEnd++;

                    var otherMode = currentMode == Mode.A ? Mode.B : Mode.A;
                    if (runEnd - i == 1)
                    {
                        symbols.Add(98); // Shift: affects only the next symbol
                        symbols.Add(MapValue(item.CharValue, otherMode));
                        i++;
                    }
                    else
                    {
                        symbols.Add(otherMode == Mode.B ? 100 : 101); // Code B / Code A
                        for (var k = i; k < runEnd; k++)
                            symbols.Add(MapValue(items[k].CharValue, otherMode));
                        i = runEnd;

                        if (i < items.Count)
                        {
                            symbols.Add(homeMode == Mode.B ? 100 : 101); // back to the home mode
                            currentMode = homeMode;
                        }
                        else
                        {
                            currentMode = otherMode;
                        }
                    }

                    break;
            }
        }

        var check = ComputeCheckCharacter(startValue, symbols);
        return (startValue, symbols, check);
    }

    /// <summary>
    /// The weighted modulo-103 check character: the start symbol's value, unweighted, plus each
    /// data symbol's value times its 1-based position (Wikipedia's worked example for "PJJ123C").
    /// </summary>
    private static int ComputeCheckCharacter(int startValue, List<int> symbols)
    {
        var sum = startValue;
        for (var i = 0; i < symbols.Count; i++) sum += symbols[i] * (i + 1);
        return sum % 103;
    }

    private static void AppendSymbolWidths(List<double> runs, int symbolValue)
    {
        foreach (var w in Code128Tables.GetWidths(symbolValue)) runs.Add(w);
    }

    private static bool IsCompatible(char c, Mode mode) => mode switch
    {
        Mode.A => c <= 95,   // control (0-31) + common (32-95)
        Mode.B => c >= 32,   // common (32-95) + lowercase/extras (96-127)
        _ => false,
    };

    private static int MapValue(char c, Mode mode) =>
        mode == Mode.A && c <= 31 ? c + 64 : c - 32;

    /// <summary>
    /// The first character exclusive to one basic subset (a control character for A, or a
    /// lowercase/96-127 character for B) picks the home subset; a data stream with neither
    /// (only digits, uppercase and common punctuation) defaults to Code Set A.
    /// </summary>
    private static bool DetermineUseSubsetB(List<Item> items)
    {
        foreach (var item in items)
        {
            if (item.Kind != ItemKind.Char) continue;
            if (item.CharValue <= 31) return false;
            if (item.CharValue >= 96) return true;
        }

        return false;
    }

    private static List<Item> Tokenize(string content)
    {
        var items = new List<Item>();
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c == '\u001D')
            {
                items.Add(Item.Fnc1());
                i++;
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < content.Length && char.IsAsciiDigit(content[i])) i++;
                AddDigitRunItems(items, content, start, i - start, isAtStart: start == 0, isAtEnd: i == content.Length);
                continue;
            }

            items.Add(Item.Char(c));
            i++;
        }

        return items;
    }

    /// <summary>
    /// Applies the GS1 Code Set C eligibility rules to one maximal digit run: 4+ digits at the
    /// start or end of the data, 6+ in the middle, or (when the run is the entire data) 2 or 4+
    /// digits. An odd-length eligible run leaves one leftover digit: at the front when the run
    /// is at the end of the data (so no switch back out of Code C is needed afterwards), or at
    /// the back otherwise.
    /// </summary>
    private static void AddDigitRunItems(List<Item> items, string content, int start, int length, bool isAtStart, bool isAtEnd)
    {
        var isEntireContent = isAtStart && isAtEnd;
        var eligible = isEntireContent
            ? length == 2 || length >= 4
            : isAtStart || isAtEnd ? length >= 4 : length >= 6;

        if (!eligible)
        {
            for (var k = 0; k < length; k++) items.Add(Item.Char(content[start + k]));
            return;
        }

        var evenLength = length - (length % 2);
        var leftover = length - evenLength;

        int pairsStart;
        if (isAtEnd)
        {
            for (var k = 0; k < leftover; k++) items.Add(Item.Char(content[start + k]));
            pairsStart = start + leftover;
        }
        else
        {
            pairsStart = start;
        }

        for (var k = 0; k < evenLength; k += 2)
            items.Add(Item.CPair(content[pairsStart + k] - '0', content[pairsStart + k + 1] - '0'));

        if (!isAtEnd)
            for (var k = 0; k < leftover; k++)
                items.Add(Item.Char(content[pairsStart + evenLength + k]));
    }
}
