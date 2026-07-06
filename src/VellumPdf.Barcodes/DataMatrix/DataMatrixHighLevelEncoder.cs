// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.DataMatrix;

/// <summary>
/// Data Matrix high-level encoding (ISO/IEC 16022:2024 §5.2): compacts a byte stream into data
/// codewords (0-255), switching automatically between ASCII, C40, Text and Base 256 for the
/// smallest-effort encoding of each run. X12 (§5.2.7) and EDIFACT (§5.2.8) are out of scope for
/// this release — every ASCII-representable byte is always reachable through ASCII, C40 or Text,
/// so this restriction only costs a little density on X12/EDIFACT-favoured content, never
/// correctness. Mirrors <c>Pdf417HighLevelEncoder</c>'s segment-then-encode structure.
/// </summary>
internal static class DataMatrixHighLevelEncoder
{
    /// <summary>ASCII-mode padding codeword (§5.2.1: fills unused data-region capacity after the encoded content).</summary>
    internal const int PadCodeword = 129;

    private const int LatchToC40 = 230;
    private const int LatchToBase256 = 231;

    /// <summary>GS1 FNC1 in the first codeword position (§5.6.1: the ECC 200 GS1 marker), and — mirroring <c>Code128Encoder</c>'s convention — wherever a literal GS (U+001D) appears in ASCII-mode content.</summary>
    internal const int Fnc1Codeword = 232;

    private const int ExtendedAsciiShiftCodeword = 235;
    private const int LatchToText = 239;
    private const int UnlatchCodeword = 254;

    /// <summary>
    /// The minimum run length of basic-set-only (digit/space/matching-case-letter) characters at
    /// which C40/Text (latch codeword, 2 codewords per 3 values, and this encoder's always-emitted
    /// unlatch codeword — see <see cref="EncodeC40OrTextRun"/>'s remarks) costs strictly less than
    /// plain ASCII: <c>2 + 2*ceil(L/3) &lt; L</c> first holds at <c>L = 9</c>.
    /// </summary>
    private const int CompactionMinRun = 9;

    /// <summary>The minimum run length of bytes 128-255 at which Base 256's latch-plus-length-field overhead pays for itself over ASCII's per-byte Upper Shift escape.</summary>
    private const int Base256MinRun = 3;

    /// <summary>Encodes <paramref name="content"/> (must be representable in ISO/IEC 8859-1) as Data Matrix data codewords, switching automatically between ASCII, C40, Text and Base 256.</summary>
    /// <exception cref="FormatException"><paramref name="content"/> contains a character outside ISO/IEC 8859-1 (Latin-1).</exception>
    internal static List<int> EncodeText(string content, bool gs1)
    {
        var bytes = ToLatin1Bytes(content);
        var output = new List<int>();
        if (gs1) output.Add(Fnc1Codeword);

        foreach (var segment in SegmentContent(bytes))
        {
            var slice = bytes.AsSpan(segment.Start, segment.Length);
            switch (segment.Kind)
            {
                case SegmentKind.C40:
                    EncodeC40OrTextRun(output, slice, isC40: true);
                    break;
                case SegmentKind.Text:
                    EncodeC40OrTextRun(output, slice, isC40: false);
                    break;
                case SegmentKind.Base256:
                    EncodeBase256Run(output, slice);
                    break;
                default:
                    EncodeAsciiRun(output, slice);
                    break;
            }
        }

        return output;
    }

    /// <summary>Encodes raw bytes as a single Base 256 run (§5.2.9), bypassing mode selection entirely — mirrors <c>Pdf417HighLevelEncoder.EncodeBytes</c>.</summary>
    internal static List<int> EncodeBytes(byte[] content, bool gs1)
    {
        var output = new List<int>();
        if (gs1) output.Add(Fnc1Codeword);
        EncodeBase256Run(output, content);
        return output;
    }

    private static byte[] ToLatin1Bytes(string content)
    {
        var bytes = new byte[content.Length];
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c > 0xFF)
                throw new FormatException($"\"{content}\" contains a character outside ISO/IEC 8859-1 (Latin-1) at index {i}; Data Matrix can only encode single bytes.");
            bytes[i] = (byte)c;
        }

        return bytes;
    }

    // ----- ASCII (§5.2.3-§5.2.4) -----

    /// <summary>
    /// Encodes a run of bytes in ASCII mode: a byte 0-127 becomes codeword <c>value + 1</c>, two
    /// consecutive digits are compacted into one codeword (130 + their two-digit value, §5.2.4),
    /// a byte 128-255 becomes the Upper Shift codeword (235) followed by <c>value - 127</c>
    /// (§5.2.3), and a literal GS (U+001D) becomes the FNC1 codeword directly rather than its
    /// literal ASCII value.
    /// </summary>
    private static void EncodeAsciiRun(List<int> output, ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length)
        {
            if (data[i] == 0x1D)
            {
                output.Add(Fnc1Codeword);
                i++;
                continue;
            }

            if (IsDigit(data[i]) && i + 1 < data.Length && IsDigit(data[i + 1]))
            {
                var pairValue = ((data[i] - (byte)'0') * 10) + (data[i + 1] - (byte)'0');
                output.Add(130 + pairValue);
                i += 2;
                continue;
            }

            if (data[i] >= 128)
            {
                output.Add(ExtendedAsciiShiftCodeword);
                output.Add(data[i] - 127);
                i++;
                continue;
            }

            output.Add(data[i] + 1);
            i++;
        }
    }

    // ----- C40 / Text (§5.2.5-§5.2.6) -----

    /// <summary>
    /// Encodes a run of bytes in C40 (<paramref name="isC40"/> true) or Text mode: a latch
    /// codeword, then the run's values (<see cref="DataMatrixTables.AppendValues"/>) packed 3 per
    /// codeword pair (<c>value = c1*1600 + c2*40 + c3 + 1</c>, high byte first), then an unlatch
    /// back to ASCII.
    /// </summary>
    /// <remarks>
    /// A run whose value count is not a multiple of 3 is padded with value 0 to complete the final
    /// pair (§5.2.5.2/§5.2.6.2 permit this for the last 1-2 positions), and the run is always
    /// followed by an explicit unlatch codeword (254). ISO/IEC 16022 allows omitting that unlatch
    /// when the run ends up filling the symbol's data-region capacity exactly, saving one
    /// codeword; this encoder always emits it for simplicity, which can only ever cost stepping up
    /// to the next symbol size in that one exact-fit edge case, never an incorrect symbol.
    /// </remarks>
    private static void EncodeC40OrTextRun(List<int> output, ReadOnlySpan<byte> data, bool isC40)
    {
        output.Add(isC40 ? LatchToC40 : LatchToText);

        var values = new List<int>((data.Length * 2 / 3) + 2);
        foreach (var b in data) DataMatrixTables.AppendValues(values, b, isC40);
        while (values.Count % 3 != 0) values.Add(0);

        for (var i = 0; i < values.Count; i += 3)
        {
            var packed = (values[i] * 1600) + (values[i + 1] * 40) + values[i + 2] + 1;
            output.Add(packed / 256);
            output.Add(packed % 256);
        }

        output.Add(UnlatchCodeword);
    }

    // ----- Base 256 (§5.2.9) -----

    /// <summary>
    /// Encodes a run of bytes in Base 256 mode: a latch codeword, a 1- or 2-byte length field
    /// (fewer than 250 bytes: the length itself; 250 or more: <c>250 + (length / 250)</c> then
    /// <c>length % 250</c>), then the bytes verbatim — every one of which (length field included)
    /// is randomized by the 255-state algorithm (§5.2.9.2): for the codeword at absolute 1-based
    /// data-codeword position <c>P</c>, <c>R = ((149*P) mod 255) + 1</c> and the transmitted value
    /// is <c>(raw + R) mod 256</c>.
    /// </summary>
    private static void EncodeBase256Run(List<int> output, ReadOnlySpan<byte> data)
    {
        output.Add(LatchToBase256);

        var raw = new List<int>(data.Length + 2);
        if (data.Length < 250)
        {
            raw.Add(data.Length);
        }
        else
        {
            raw.Add((data.Length / 250) + 249);
            raw.Add(data.Length % 250);
        }

        foreach (var b in data) raw.Add(b);

        var codewordsBeforeThisRun = output.Count;
        for (var i = 0; i < raw.Count; i++)
        {
            var position = codewordsBeforeThisRun + i + 1; // 1-based absolute position in the data-codeword stream
            var randomizer = ((149 * position) % 255) + 1;
            output.Add((raw[i] + randomizer) % 256);
        }
    }

    // ----- Mode segmentation -----

    private enum SegmentKind { Ascii, C40, Text, Base256 }

    private readonly record struct Segment(SegmentKind Kind, int Start, int Length);

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static bool IsUpper(byte b) => b is >= (byte)'A' and <= (byte)'Z';

    private static bool IsLower(byte b) => b is >= (byte)'a' and <= (byte)'z';

    private static bool IsSpace(byte b) => b == (byte)' ';

    private static bool IsHighByte(byte b) => b >= 128;

    /// <summary>
    /// Segments <paramref name="data"/> into runs that are each encoded as one mode: a run of at
    /// least <see cref="CompactionMinRun"/> characters drawn entirely from digits, spaces and
    /// upper-case letters (with at least one upper-case letter, or a pure digit/space run is
    /// cheaper left as ASCII digit pairs) becomes C40; the lower-case mirror becomes Text; a run
    /// of at least <see cref="Base256MinRun"/> bytes 128-255 becomes Base 256; everything else
    /// (including short mixed runs, and digit/space-only runs) becomes ASCII.
    /// </summary>
    private static List<Segment> SegmentContent(ReadOnlySpan<byte> data)
    {
        var segments = new List<Segment>();
        var i = 0;
        var asciiStart = 0;

        while (i < data.Length)
        {
            var c40Length = BasicRunLength(data, i, isC40: true, out var hasDistinguishingLetter);
            if (hasDistinguishingLetter && c40Length >= CompactionMinRun)
            {
                FlushAscii(segments, asciiStart, i);
                segments.Add(new Segment(SegmentKind.C40, i, c40Length));
                i += c40Length;
                asciiStart = i;
                continue;
            }

            var textLength = BasicRunLength(data, i, isC40: false, out hasDistinguishingLetter);
            if (hasDistinguishingLetter && textLength >= CompactionMinRun)
            {
                FlushAscii(segments, asciiStart, i);
                segments.Add(new Segment(SegmentKind.Text, i, textLength));
                i += textLength;
                asciiStart = i;
                continue;
            }

            var highByteLength = HighByteRunLength(data, i);
            if (highByteLength >= Base256MinRun)
            {
                FlushAscii(segments, asciiStart, i);
                segments.Add(new Segment(SegmentKind.Base256, i, highByteLength));
                i += highByteLength;
                asciiStart = i;
                continue;
            }

            i++;
        }

        FlushAscii(segments, asciiStart, data.Length);
        return segments;
    }

    private static void FlushAscii(List<Segment> segments, int start, int end)
    {
        if (end > start) segments.Add(new Segment(SegmentKind.Ascii, start, end - start));
    }

    /// <summary>The length of the run starting at <paramref name="start"/> drawn from digits, spaces and the case (<paramref name="isC40"/>: upper, else lower) matching letters, plus whether it contains at least one such letter.</summary>
    private static int BasicRunLength(ReadOnlySpan<byte> data, int start, bool isC40, out bool hasDistinguishingLetter)
    {
        hasDistinguishingLetter = false;
        var j = start;
        while (j < data.Length)
        {
            var b = data[j];
            var isLetter = isC40 ? IsUpper(b) : IsLower(b);
            if (!IsDigit(b) && !IsSpace(b) && !isLetter) break;
            if (isLetter) hasDistinguishingLetter = true;
            j++;
        }

        return j - start;
    }

    private static int HighByteRunLength(ReadOnlySpan<byte> data, int start)
    {
        var j = start;
        while (j < data.Length && IsHighByte(data[j])) j++;
        return j - start;
    }
}
