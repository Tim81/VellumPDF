// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Numerics;

namespace VellumPdf.Barcodes.Pdf417;

/// <summary>
/// PDF417 high-level encoding (ISO/IEC 15438 section 2.2.4): compacts text into data codewords
/// (0-899) using Text, Byte and Numeric Compaction, switching between them automatically for a
/// string via the ISO/IEC 15438 Annex P heuristics (a long run of digits is cheaper in Numeric
/// Compaction; a short run of otherwise-text characters between two byte runs is cheaper left as
/// bytes than paying for a mode switch). None of this covers Macro PDF417 (multi-symbol
/// splitting), which this package does not support.
/// </summary>
internal static class Pdf417HighLevelEncoder
{
    private const int LatchToText = 900;
    private const int LatchToByte = 901;
    private const int LatchToByteMultipleOfSix = 924;
    private const int LatchToNumeric = 902;

    /// <summary>The digit-run length at which Numeric Compaction is cheaper than folding the digits into a surrounding Text Compaction run (ISO/IEC 15438 Annex P).</summary>
    private const int NumericThreshold = 13;

    /// <summary>The minimum length of a Text-compactable run, once a byte run has been found on at least one side, worth paying a mode-switch codeword for rather than encoding it as bytes too.</summary>
    private const int TextRunMergeThreshold = 5;

    /// <summary>Encodes <paramref name="content"/> (must be representable in ISO/IEC 8859-1) as PDF417 data codewords, automatically switching between Text, Byte and Numeric Compaction.</summary>
    /// <exception cref="FormatException"><paramref name="content"/> contains a character outside ISO/IEC 8859-1 (Latin-1).</exception>
    internal static List<int> EncodeText(string content)
    {
        var bytes = ToLatin1Bytes(content);
        var output = new List<int>();
        var segments = SegmentContent(bytes);

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var slice = bytes.AsSpan(segment.Start, segment.Length);
            switch (segment.Kind)
            {
                case SegmentKind.Text:
                    if (i > 0) output.Add(LatchToText);
                    EncodeTextChars(output, slice);
                    break;
                case SegmentKind.Numeric:
                    output.Add(LatchToNumeric);
                    EncodeNumericDigits(output, slice);
                    break;
                default:
                    output.Add(slice.Length % 6 == 0 ? LatchToByteMultipleOfSix : LatchToByte);
                    EncodeByteBytes(output, slice);
                    break;
            }
        }

        return output;
    }

    /// <summary>Encodes raw bytes as a single Byte Compaction run (the Latch codeword plus the base-900 conversion), bypassing mode selection entirely.</summary>
    internal static List<int> EncodeBytes(byte[] content)
    {
        var output = new List<int> { content.Length % 6 == 0 ? LatchToByteMultipleOfSix : LatchToByte };
        EncodeByteBytes(output, content);
        return output;
    }

    private static byte[] ToLatin1Bytes(string content)
    {
        var bytes = new byte[content.Length];
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c > 0xFF)
                throw new FormatException($"\"{content}\" contains a character outside ISO/IEC 8859-1 (Latin-1) at index {i}; PDF417 can only encode single bytes.");
            bytes[i] = (byte)c;
        }

        return bytes;
    }

    // ----- Byte Compaction (ISO/IEC 15438 section 2.2.4.5) -----

    /// <summary>Converts groups of 6 bytes to 5 base-900 codewords each (48-bit range, so <c>ulong</c> suffices); a final partial group under 6 bytes is carried one byte per codeword.</summary>
    private static void EncodeByteBytes(List<int> output, ReadOnlySpan<byte> data)
    {
        var fullGroups = data.Length / 6;
        for (var g = 0; g < fullGroups; g++)
        {
            var sum = 0UL;
            for (var k = 0; k < 6; k++) sum = (sum << 8) | data[(g * 6) + k];

            // d[0] is the least significant base-900 digit (codeword 0 in ISO/IEC 15438's own
            // naming); the codewords are transmitted most significant first.
            var d = new int[5];
            for (var k = 0; k < 5; k++)
            {
                d[k] = (int)(sum % 900);
                sum /= 900;
            }

            for (var k = 4; k >= 0; k--) output.Add(d[k]);
        }

        for (var i = fullGroups * 6; i < data.Length; i++) output.Add(data[i]);
    }

    // ----- Numeric Compaction (ISO/IEC 15438 section 2.2.4.6) -----

    /// <summary>Converts groups of up to 44 digits (prefixed with a leading 1, per the spec) to base-900 codewords via <see cref="BigInteger"/>, most significant first.</summary>
    private static void EncodeNumericDigits(List<int> output, ReadOnlySpan<byte> digits)
    {
        for (var offset = 0; offset < digits.Length; offset += 44)
        {
            var groupLength = Math.Min(44, digits.Length - offset);
            var chars = new char[groupLength + 1];
            chars[0] = '1';
            for (var i = 0; i < groupLength; i++) chars[i + 1] = (char)digits[offset + i];

            var value = BigInteger.Parse(chars, NumberStyles.None, CultureInfo.InvariantCulture);
            var codewords = new List<int>();
            while (value > 0)
            {
                codewords.Add((int)(value % 900));
                value /= 900;
            }

            codewords.Reverse();
            output.AddRange(codewords);
        }
    }

    // ----- Text Compaction (ISO/IEC 15438 section 2.2.4.4) -----

    private enum TextSubmode { Alpha, Lower, Mixed, Punctuation }

    // Sub-mode latch/shift codeword values (Table 3). A raw value's meaning depends on which
    // sub-mode it is emitted from: 27 is Lower-Latch from Alpha/Mixed but Alpha-Shift from Lower;
    // 28 is Mixed-Latch from Alpha/Lower but Alpha-Latch from Mixed; 25 (Punctuation-Latch) exists
    // only from Mixed; 29 (Punctuation-Shift) is available from Alpha, Lower and Mixed, and is
    // also Alpha-Latch from Punctuation (Punctuation's only way out).
    private const int SwitchLl = 27;
    private const int SwitchMlOrAl = 28;
    private const int SwitchPl = 25;
    private const int SwitchAsOrPsOrAl = 29;

    private static readonly Dictionary<char, int> AlphaValues = BuildLetterMap('A', ' ');
    private static readonly Dictionary<char, int> LowerValues = BuildLetterMap('a', ' ');

    private static readonly Dictionary<char, int> MixedValues = new()
    {
        ['0'] = 0,
        ['1'] = 1,
        ['2'] = 2,
        ['3'] = 3,
        ['4'] = 4,
        ['5'] = 5,
        ['6'] = 6,
        ['7'] = 7,
        ['8'] = 8,
        ['9'] = 9,
        ['&'] = 10,
        ['\r'] = 11,
        ['\t'] = 12,
        [','] = 13,
        [':'] = 14,
        ['#'] = 15,
        ['-'] = 16,
        ['.'] = 17,
        ['$'] = 18,
        ['/'] = 19,
        ['+'] = 20,
        ['%'] = 21,
        ['*'] = 22,
        ['='] = 23,
        ['^'] = 24,
        [' '] = 26,
    };

    private static readonly Dictionary<char, int> PunctuationValues = new()
    {
        [';'] = 0,
        ['<'] = 1,
        ['>'] = 2,
        ['@'] = 3,
        ['['] = 4,
        ['\\'] = 5,
        [']'] = 6,
        ['_'] = 7,
        ['`'] = 8,
        ['~'] = 9,
        ['!'] = 10,
        ['\r'] = 11,
        ['\t'] = 12,
        [','] = 13,
        [':'] = 14,
        ['\n'] = 15,
        ['-'] = 16,
        ['.'] = 17,
        ['$'] = 18,
        ['/'] = 19,
        ['"'] = 20,
        ['|'] = 21,
        ['*'] = 22,
        ['('] = 23,
        [')'] = 24,
        ['?'] = 25,
        ['{'] = 26,
        ['}'] = 27,
        ['\''] = 28,
    };

    private static readonly HashSet<char> MixedOnlyChars = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '&', '#', '+', '%', '=', '^'];
    private static readonly HashSet<char> SharedMixedPunctuationChars = ['\r', '\t', ',', ':', '-', '.', '$', '/', '*'];

    private static Dictionary<char, int> BuildLetterMap(char first, char space)
    {
        var map = new Dictionary<char, int>(27);
        for (var i = 0; i < 26; i++) map[(char)(first + i)] = i;
        map[space] = 26;
        return map;
    }

    private static Dictionary<char, int> ValuesFor(TextSubmode submode) => submode switch
    {
        TextSubmode.Alpha => AlphaValues,
        TextSubmode.Lower => LowerValues,
        TextSubmode.Mixed => MixedValues,
        TextSubmode.Punctuation => PunctuationValues,
        _ => throw new ArgumentOutOfRangeException(nameof(submode)),
    };

    /// <summary>
    /// Packs a run of text-compactable bytes into base-30 value pairs (two per codeword), latching
    /// or shifting between the Alpha/Lower/Mixed/Punctuation sub-modes as needed. Always starts in
    /// the Alpha sub-mode, per ISO/IEC 15438 ("a latch from any mode to the Text Compaction mode is
    /// a latch to the Alpha sub-mode"). A one-character deviation from the current sub-mode uses a
    /// temporary Shift when one exists for that transition (only Lower-to-Alpha and Alpha/Lower/
    /// Mixed-to-Punctuation have one); a longer run latches instead. This is a correct but not
    /// necessarily length-optimal application of the switching rules — it looks only one character
    /// ahead to decide between a shift and a latch.
    /// </summary>
    private static void EncodeTextChars(List<int> output, ReadOnlySpan<byte> data)
    {
        var current = TextSubmode.Alpha;
        var values = new List<int>(data.Length + 1);

        for (var i = 0; i < data.Length; i++)
        {
            var c = (char)data[i];
            if (ValuesFor(current).TryGetValue(c, out var direct))
            {
                values.Add(direct);
                continue;
            }

            var next = i + 1 < data.Length ? (char?)data[i + 1] : null;
            var target = ChooseTarget(c, next);
            var isolated = next is not { } n || ValuesFor(current).ContainsKey(n);
            EmitTransition(values, ref current, target, isolated);

            values.Add(ValuesFor(target)[c]);
        }

        if (values.Count % 2 != 0) values.Add(SwitchAsOrPsOrAl); // harmless filler; there is no following codeword to reinterpret it

        for (var i = 0; i < values.Count; i += 2)
            output.Add((values[i] * 30) + values[i + 1]);
    }

    private static TextSubmode ChooseTarget(char c, char? next)
    {
        if (c == ' ') return TextSubmode.Alpha; // only reachable from Punctuation, which has no space of its own
        if (c is >= 'A' and <= 'Z') return TextSubmode.Alpha;
        if (c is >= 'a' and <= 'z') return TextSubmode.Lower;
        if (MixedOnlyChars.Contains(c)) return TextSubmode.Mixed;
        if (SharedMixedPunctuationChars.Contains(c))
            return next is { } n && MixedOnlyChars.Contains(n) ? TextSubmode.Mixed : TextSubmode.Punctuation;
        return TextSubmode.Punctuation;
    }

    private static void EmitTransition(List<int> values, ref TextSubmode current, TextSubmode target, bool isolated)
    {
        switch (current, target)
        {
            case (TextSubmode.Alpha, TextSubmode.Lower):
                values.Add(SwitchLl);
                current = TextSubmode.Lower;
                break;
            case (TextSubmode.Alpha, TextSubmode.Mixed):
                values.Add(SwitchMlOrAl);
                current = TextSubmode.Mixed;
                break;
            case (TextSubmode.Alpha, TextSubmode.Punctuation):
                if (isolated)
                {
                    values.Add(SwitchAsOrPsOrAl); // Punctuation-Shift
                }
                else
                {
                    values.Add(SwitchMlOrAl);
                    current = TextSubmode.Mixed;
                    values.Add(SwitchPl);
                    current = TextSubmode.Punctuation;
                }

                break;
            case (TextSubmode.Lower, TextSubmode.Alpha):
                if (isolated)
                {
                    values.Add(SwitchAsOrPsOrAl); // Alpha-Shift
                }
                else
                {
                    values.Add(SwitchMlOrAl);
                    current = TextSubmode.Mixed;
                    values.Add(SwitchMlOrAl); // Alpha-Latch, from Mixed
                    current = TextSubmode.Alpha;
                }

                break;
            case (TextSubmode.Lower, TextSubmode.Mixed):
                values.Add(SwitchMlOrAl);
                current = TextSubmode.Mixed;
                break;
            case (TextSubmode.Lower, TextSubmode.Punctuation):
                if (isolated)
                {
                    values.Add(SwitchAsOrPsOrAl); // Punctuation-Shift
                }
                else
                {
                    values.Add(SwitchMlOrAl);
                    current = TextSubmode.Mixed;
                    values.Add(SwitchPl);
                    current = TextSubmode.Punctuation;
                }

                break;
            case (TextSubmode.Mixed, TextSubmode.Alpha):
                values.Add(SwitchMlOrAl); // Alpha-Latch
                current = TextSubmode.Alpha;
                break;
            case (TextSubmode.Mixed, TextSubmode.Lower):
                values.Add(SwitchLl);
                current = TextSubmode.Lower;
                break;
            case (TextSubmode.Mixed, TextSubmode.Punctuation):
                if (isolated)
                {
                    values.Add(SwitchAsOrPsOrAl); // Punctuation-Shift
                }
                else
                {
                    values.Add(SwitchPl);
                    current = TextSubmode.Punctuation;
                }

                break;
            case (TextSubmode.Punctuation, TextSubmode.Alpha):
                values.Add(SwitchAsOrPsOrAl); // Alpha-Latch, Punctuation's only way out
                current = TextSubmode.Alpha;
                break;
            case (TextSubmode.Punctuation, TextSubmode.Lower):
                values.Add(SwitchAsOrPsOrAl);
                current = TextSubmode.Alpha;
                values.Add(SwitchLl);
                current = TextSubmode.Lower;
                break;
            case (TextSubmode.Punctuation, TextSubmode.Mixed):
                values.Add(SwitchAsOrPsOrAl);
                current = TextSubmode.Alpha;
                values.Add(SwitchMlOrAl);
                current = TextSubmode.Mixed;
                break;
            default:
                throw new InvalidOperationException("Unreachable text sub-mode transition.");
        }
    }

    // ----- Mode segmentation (ISO/IEC 15438 Annex P) -----

    private enum SegmentKind { Text, Byte, Numeric }

    private readonly record struct Segment(SegmentKind Kind, int Start, int Length);

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static bool IsTextCompactable(byte b) => b is 9 or 10 or 13 or (>= 32 and <= 126);

    private static List<Segment> SegmentContent(ReadOnlySpan<byte> data)
    {
        var segments = new List<Segment>();
        var i = 0;
        while (i < data.Length)
        {
            if (TryTakeLongDigitRun(data, i, out var digitEnd))
            {
                segments.Add(new Segment(SegmentKind.Numeric, i, digitEnd - i));
                i = digitEnd;
                continue;
            }

            if (IsTextCompactable(data[i]))
            {
                var j = i;
                while (j < data.Length)
                {
                    if (IsDigit(data[j]) && TryTakeLongDigitRun(data, j, out _)) break;
                    if (!IsTextCompactable(data[j])) break;
                    j++;
                }

                segments.Add(new Segment(SegmentKind.Text, i, j - i));
                i = j;
            }
            else
            {
                var j = i;
                while (j < data.Length && !IsTextCompactable(data[j])) j++;
                segments.Add(new Segment(SegmentKind.Byte, i, j - i));
                i = j;
            }
        }

        return MergeShortTextRuns(segments);
    }

    private static bool TryTakeLongDigitRun(ReadOnlySpan<byte> data, int start, out int end)
    {
        var j = start;
        while (j < data.Length && IsDigit(data[j])) j++;
        end = j;
        return j - start >= NumericThreshold;
    }

    /// <summary>
    /// A short Text run next to a Byte run costs more (a mode-switch codeword either way) than
    /// just folding it into the Byte run, so runs shorter than <see cref="TextRunMergeThreshold"/>
    /// characters are absorbed into an adjacent Byte run when one exists.
    /// </summary>
    private static List<Segment> MergeShortTextRuns(List<Segment> segments)
    {
        var merged = new List<Segment>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment.Kind == SegmentKind.Text && segment.Length < TextRunMergeThreshold && merged.Count > 0 && merged[^1].Kind == SegmentKind.Byte)
            {
                merged[^1] = merged[^1] with { Length = merged[^1].Length + segment.Length };
                continue;
            }

            merged.Add(segment);
        }

        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Kind == SegmentKind.Text && merged[i].Length < TextRunMergeThreshold && i + 1 < merged.Count && merged[i + 1].Kind == SegmentKind.Byte)
            {
                merged[i] = new Segment(SegmentKind.Byte, merged[i].Start, merged[i].Length + merged[i + 1].Length);
                merged.RemoveAt(i + 1);
            }
        }

        var final = new List<Segment>(merged.Count);
        foreach (var segment in merged)
        {
            if (final.Count > 0 && final[^1].Kind == segment.Kind)
                final[^1] = final[^1] with { Length = final[^1].Length + segment.Length };
            else
                final.Add(segment);
        }

        return final;
    }
}
