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
///
/// <para>
/// Latin-1 characters (128-255) are carried with FNC4: a lone one is reached with a single
/// FNC4, which extends only the character right after it; a run of two or more latches FNC4
/// with a doubled FNC4, extending every character until a doubled FNC4 switches it off again
/// (ISO/IEC 15417). Code Set A and B reuse the same symbol values for "switch to A" (101) and
/// "switch to B" (100) as their own FNC4 code, since telling a decoder already in Code A to
/// switch to Code A would otherwise be a no-op. Which basic subset a Latin-1 character needs
/// still comes from its low 128 equivalent (char - 128): 0x80-0x9F carries a control-range low
/// value, so it needs Code Set A the same as its unshifted 0x00-0x1F counterpart would.
/// </para>
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

    /// <summary>Validates that <paramref name="content"/> is Latin-1 (0-255), returning it unchanged.</summary>
    /// <exception cref="ArgumentException">A character falls outside 0-255.</exception>
    internal static string Validate(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var c in content)
            if (c > 255)
                throw new ArgumentException($"Code 128 content must be Latin-1 (0-255); found U+{(int)c:X4}.", nameof(content));
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

        var dataModuleCount = 0.0;
        foreach (var run in runs) dataModuleCount += run;

        // Code 128 has no digit grouping to preserve (unlike EAN/UPC), so the whole content is a
        // single HRI line centred beneath the bars. A plain Code 128 symbol shows its content
        // verbatim; a GS1-128 symbol shows the parenthesized application-identifier form (GS1
        // General Specifications, human-readable interpretation of an element string), e.g.
        // "(01)09501101020917(17)261231".
        var hriLabel = BuildHriLabel(barcode);

        return new Encoded1D
        {
            Runs = runs,
            QuietZoneLeft = 10,
            QuietZoneRight = 10,
            HriGroups = hriLabel.Length == 0 ? [] : [new HriGroup(hriLabel, HriAnchor.Below, 0, dataModuleCount)],
        };
    }

    /// <summary>
    /// The human-readable text drawn beneath the bars. For GS1-128, that is the parenthesized
    /// application-identifier interpretation of the content. Content the caller flagged GS1 but
    /// that is not a well-formed element string still encodes into valid bars, so it falls back
    /// to the raw content with its FNC1 (U+001D) separators removed rather than failing to render.
    /// </summary>
    private static string BuildHriLabel(Code128Barcode barcode)
    {
        if (!barcode.Gs1) return barcode.Content;

        try
        {
            return Gs1ElementString.Parse(barcode.Content).Hri;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return barcode.Content.Replace("", string.Empty);
        }
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
        if (barcode.Gs1)
            foreach (var c in barcode.Content)
                if (c > 127)
                    throw new ArgumentException(
                        $"GS1-128 content cannot use FNC4 / extended Latin-1; found U+{(int)c:X4}. " +
                        "The GS1 General Specifications reserve FNC4 for plain Code 128 and do not " +
                        "permit it in a GS1-128 symbol.", nameof(barcode));

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

        // Whether FNC4 is currently latched (every character gets +128 until a doubled FNC4
        // switches it off), tracked across the whole item stream regardless of which Char branch
        // below is servicing the current item.
        var fnc4Latched = false;

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
                        // Code Set C has no FNC4 concept, so a latch left over from a preceding
                        // Latin-1 run has to switch off before the plunge into Code C.
                        UnlatchFnc4(symbols, currentMode, ref fnc4Latched);
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

                    var lowChar = LowEquivalent(item.CharValue);

                    if (IsCompatible(lowChar, currentMode))
                    {
                        EmitFnc4(items, i, currentMode, symbols, ref fnc4Latched);
                        symbols.Add(MapValue(lowChar, currentMode));
                        i++;
                        break;
                    }

                    var runEnd = i;
                    while (runEnd < items.Count && items[runEnd].Kind == ItemKind.Char
                                                 && !IsCompatible(LowEquivalent(items[runEnd].CharValue), currentMode))
                        runEnd++;

                    var otherMode = currentMode == Mode.A ? Mode.B : Mode.A;

                    // A lone Latin-1 character cannot take the cheap Shift below: Shift's target
                    // is the single symbol right after it, and FNC4 has no symbol value of its
                    // own to occupy that slot with (it reuses "switch to A"/"switch to B", both
                    // already-reserved function codes, not a data value Shift could read through
                    // otherMode's table). Confirmed against zxing-cpp: emitting FNC4 as if it were
                    // Shift's target symbol decoded the character with no Latin-1 bit set at all.
                    // A genuine switch sidesteps this, since FNC4 then keys off the new register
                    // rather than sharing a slot with Shift.
                    if (runEnd - i == 1 && item.CharValue <= 127)
                    {
                        symbols.Add(98); // Shift: affects only the next symbol
                        symbols.Add(MapValue(lowChar, otherMode));
                        i++;
                    }
                    else
                    {
                        symbols.Add(otherMode == Mode.B ? 100 : 101); // Code B / Code A

                        // A genuine switch moves the register to otherMode for every character in
                        // this run, so FNC4 inside the loop below is keyed to otherMode.
                        for (var k = i; k < runEnd; k++)
                        {
                            EmitFnc4(items, k, otherMode, symbols, ref fnc4Latched);
                            symbols.Add(MapValue(LowEquivalent(items[k].CharValue), otherMode));
                        }

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
        // Reduced every step so the accumulator cannot overflow, whatever the content length.
        var sum = startValue % 103;
        for (var i = 0; i < symbols.Count; i++) sum = (sum + (symbols[i] * ((i % 103) + 1))) % 103;
        return sum;
    }

    private static void AppendSymbolWidths(List<double> runs, int symbolValue)
    {
        foreach (var w in Code128Tables.GetWidths(symbolValue)) runs.Add(w);
    }

    /// <summary>A Latin-1 character's low 128 equivalent (char - 128), or the character itself if it is already 0-127.</summary>
    private static char LowEquivalent(char c) => c > 127 ? (char)(c - 128) : c;

    /// <summary>
    /// Emits the FNC4 codes item <paramref name="index"/> needs before its own value symbol, if
    /// any: nothing for a 0-127 character (beyond switching an active latch back off), a single
    /// FNC4 for a Latin-1 character with a 0-127 neighbour on both sides, or a doubled FNC4 to
    /// latch one that starts a run of two or more. <paramref name="registerMode"/> is the A/B
    /// register in effect when this code is emitted: <c>currentMode</c> itself for a directly
    /// compatible or Shifted character (a Shift does not move the register), or the target subset
    /// for a character reached through a genuine Code A/Code B switch.
    /// </summary>
    private static void EmitFnc4(List<Item> items, int index, Mode registerMode, List<int> symbols, ref bool fnc4Latched)
    {
        var item = items[index];
        if (item.CharValue <= 127)
        {
            UnlatchFnc4(symbols, registerMode, ref fnc4Latched);
            return;
        }

        if (fnc4Latched) return; // already latched by an earlier character in this run

        var nextIsHigh = index + 1 < items.Count
            && items[index + 1].Kind == ItemKind.Char
            && items[index + 1].CharValue > 127;

        var fnc4Value = registerMode == Mode.A ? 101 : 100;
        symbols.Add(fnc4Value);
        if (nextIsHigh)
        {
            symbols.Add(fnc4Value); // doubled: latches until the next doubled FNC4
            fnc4Latched = true;
        }
    }

    /// <summary>Switches an active FNC4 latch back off with a doubled FNC4, if one is active.</summary>
    private static void UnlatchFnc4(List<int> symbols, Mode registerMode, ref bool fnc4Latched)
    {
        if (!fnc4Latched) return;

        var fnc4Value = registerMode == Mode.A ? 101 : 100;
        symbols.Add(fnc4Value);
        symbols.Add(fnc4Value);
        fnc4Latched = false;
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
    /// (only digits, uppercase and common punctuation) defaults to Code Set A. A Latin-1
    /// character is judged by its low 128 equivalent, the same value FNC4 will reconstruct it
    /// from, so e.g. 0x80 (low equivalent 0x00) counts as a Code Set A control character here.
    /// </summary>
    private static bool DetermineUseSubsetB(List<Item> items)
    {
        foreach (var item in items)
        {
            if (item.Kind != ItemKind.Char) continue;
            var low = LowEquivalent(item.CharValue);
            if (low <= 31) return false;
            if (low >= 96) return true;
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
