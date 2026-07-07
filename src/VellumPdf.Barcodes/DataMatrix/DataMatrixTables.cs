// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.DataMatrix;

/// <summary>
/// The C40 and Text compaction value tables (ISO/IEC 16022:2024 §5.2.5 "C40 encodation" and
/// §5.2.6 "Text encodation", Table 2). Both modes share the same structure — a basic set, three
/// shift sets, and Upper Shift for byte values 128-255 — and differ only in which case each
/// covers: C40's basic set is upper-case letters (its shift-3 set is lower-case); Text's basic set
/// is lower-case (its shift-3 set is upper-case). Every other value (space, digits, the three
/// shift codes themselves, and the shift-1/shift-2 sets) is identical between the two modes.
/// </summary>
internal static class DataMatrixTables
{
    /// <summary>Value 0 in the basic set: switches the next single value to the Shift-1 set (control characters 0-31, direct 1:1).</summary>
    internal const int Shift1 = 0;

    /// <summary>Value 1 in the basic set: switches the next single value to the Shift-2 set (a fixed punctuation subset, plus FNC1 and Upper Shift).</summary>
    internal const int Shift2 = 1;

    /// <summary>Value 2 in the basic set: switches the next single value to the Shift-3 set (the opposite-case letters, plus a handful of remaining punctuation).</summary>
    internal const int Shift3 = 2;

    /// <summary>Shift-2 value 27: FNC1 within a C40/Text run (ISO/IEC 16022 Table 2). Mirrors the ASCII-mode codeword 232 the caller sees when GS1 mode is on.</summary>
    internal const int Fnc1InShift2 = 27;

    /// <summary>Shift-2 value 30: Upper Shift, escaping the next character into the 128-255 range by adding 128 back after decoding it as an ordinary (non-extended) value.</summary>
    internal const int UpperShiftInShift2 = 30;

    // Shift-2: a specific 27-symbol punctuation subset (values 0-26), identical for C40 and Text.
    private static readonly Dictionary<char, int> Shift2Values = new()
    {
        ['!'] = 0,
        ['"'] = 1,
        ['#'] = 2,
        ['$'] = 3,
        ['%'] = 4,
        ['&'] = 5,
        ['\''] = 6,
        ['('] = 7,
        [')'] = 8,
        ['*'] = 9,
        ['+'] = 10,
        [','] = 11,
        ['-'] = 12,
        ['.'] = 13,
        ['/'] = 14,
        [':'] = 15,
        [';'] = 16,
        ['<'] = 17,
        ['='] = 18,
        ['>'] = 19,
        ['?'] = 20,
        ['@'] = 21,
        ['['] = 22,
        ['\\'] = 23,
        [']'] = 24,
        ['^'] = 25,
        ['_'] = 26,
    };

    // Shift-3's non-letter members (value 0 and 27-31), identical for C40 and Text; letters
    // (values 1-26) are the opposite case of whichever basic set is active, handled separately.
    private static readonly Dictionary<char, int> Shift3SpecialValues = new()
    {
        ['`'] = 0,
        ['{'] = 27,
        ['|'] = 28,
        ['}'] = 29,
        ['~'] = 30,
        [(char)127] = 31,
    };

    /// <summary>
    /// Appends the C40 (<paramref name="isC40"/> true) or Text (false) value(s) needed to encode
    /// one byte to <paramref name="values"/>: 1 value for the shared basic set (space, digit, or
    /// the matching-case letter), 2 for a Shift-1/2/3 value, or 4 (a Shift-2 Upper Shift pair
    /// followed by 1-2 more) for a byte 128-255 via §5.2.5.3/§5.2.6.3's Upper Shift mechanism. A
    /// literal GS (U+001D) becomes the Shift-2 FNC1 value instead of its Shift-1 control-code
    /// value, mirroring the GS1 convention <c>Code128Encoder</c> already applies for Code 128.
    /// </summary>
    internal static void AppendValues(List<int> values, byte b, bool isC40)
    {
        if (b == 0x1D)
        {
            values.Add(Shift2);
            values.Add(Fnc1InShift2);
            return;
        }

        if (b >= 128)
        {
            values.Add(Shift2);
            values.Add(UpperShiftInShift2);
            AppendValues(values, (byte)(b - 128), isC40);
            return;
        }

        var c = (char)b;
        if (c == ' ')
        {
            values.Add(3);
            return;
        }

        if (c is >= '0' and <= '9')
        {
            values.Add(4 + (c - '0'));
            return;
        }

        var basicLetterBase = isC40 ? 'A' : 'a';
        if (IsCase(c, isC40))
        {
            values.Add(14 + (c - basicLetterBase));
            return;
        }

        if (b <= 31)
        {
            values.Add(Shift1);
            values.Add(b);
            return;
        }

        if (Shift2Values.TryGetValue(c, out var shift2Value))
        {
            values.Add(Shift2);
            values.Add(shift2Value);
            return;
        }

        var oppositeLetterBase = isC40 ? 'a' : 'A';
        if (IsCase(c, !isC40))
        {
            values.Add(Shift3);
            values.Add(1 + (c - oppositeLetterBase));
            return;
        }

        if (Shift3SpecialValues.TryGetValue(c, out var shift3Value))
        {
            values.Add(Shift3);
            values.Add(shift3Value);
            return;
        }

        // Unreachable: every byte 0-127 is one of space, digit, upper-case letter, lower-case
        // letter, a Shift-1 control code, a Shift-2 punctuation mark, or a Shift-3 punctuation
        // mark, and bytes 128-255 recurse above after subtracting 128.
        throw new InvalidOperationException($"Byte {b} is not representable in C40/Text encodation.");
    }

    private static bool IsCase(char c, bool upper) => upper ? c is >= 'A' and <= 'Z' : c is >= 'a' and <= 'z';
}
