// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Qr;

/// <summary>
/// Assembles a QR/Micro QR data codeword sequence: the optional ECI header, each segment's mode
/// indicator, character count indicator and data (ISO/IEC 18004 §7.4), a terminator shortened to
/// fit, byte alignment, and the alternating pad codewords 0xEC/0x11 (§7.4.10). Micro QR versions
/// M1 and M3 end in a 4-bit half codeword instead of a full byte, padded with <c>0000</c>.
/// </summary>
internal static class QrBitStreamBuilder
{
    /// <summary>The single-codeword ECI designator for UTF-8 (assignment number 26): <c>0bbbbbbb</c> (ISO/IEC 18004 Table 4).</summary>
    private const int Utf8EciDesignator = 0b0_0011010;

    /// <summary>Writes the ECI mode indicator and the UTF-8 (26) designator codeword.</summary>
    internal static void WriteUtf8EciHeader(BitWriter writer)
    {
        writer.WriteBits(QrTables.EciModeIndicator, QrTables.ModeIndicatorBits);
        writer.WriteBits(Utf8EciDesignator, 8);
    }

    /// <summary>Writes every segment's mode indicator, character count indicator and data.</summary>
    /// <param name="writer">The bit stream being assembled.</param>
    /// <param name="content">The original string the segments index into.</param>
    /// <param name="segments">The segments produced by <see cref="QrSegmenter"/>.</param>
    /// <param name="modeIndicator">Returns the (value, bit width) of the mode indicator for a mode.</param>
    /// <param name="countBits">Returns the character count indicator's bit width for a mode.</param>
    /// <param name="byteEncoding">The encoding used for byte-mode segments.</param>
    internal static void WriteSegments(
        BitWriter writer,
        string content,
        IReadOnlyList<QrSegment> segments,
        Func<QrSegmentMode, (int Value, int Bits)> modeIndicator,
        Func<QrSegmentMode, int> countBits,
        Encoding byteEncoding)
    {
        foreach (var segment in segments)
        {
            var (value, bits) = modeIndicator(segment.Mode);
            if (bits > 0) writer.WriteBits(value, bits);
            writer.WriteBits(segment.RuneCount, countBits(segment.Mode));

            switch (segment.Mode)
            {
                case QrSegmentMode.Numeric:
                    WriteNumeric(writer, content, segment);
                    break;
                case QrSegmentMode.Alphanumeric:
                    WriteAlphanumeric(writer, content, segment);
                    break;
                case QrSegmentMode.Byte:
                    WriteByte(writer, content, segment, byteEncoding);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(segments));
            }
        }
    }

    private static void WriteNumeric(BitWriter writer, string content, QrSegment segment)
    {
        var text = content.AsSpan(segment.CharStart, segment.CharLength);
        var i = 0;
        while (i < text.Length)
        {
            var remaining = text.Length - i;
            var groupLength = Math.Min(3, remaining);
            var group = 0;
            for (var j = 0; j < groupLength; j++) group = (group * 10) + (text[i + j] - '0');
            writer.WriteBits(group, groupLength switch { 3 => 10, 2 => 7, _ => 4 });
            i += groupLength;
        }
    }

    private static void WriteAlphanumeric(BitWriter writer, string content, QrSegment segment)
    {
        var text = content.AsSpan(segment.CharStart, segment.CharLength);
        var i = 0;
        while (i < text.Length)
        {
            var first = QrTables.AlphanumericValue(text[i]);
            if (i + 1 < text.Length)
            {
                var second = QrTables.AlphanumericValue(text[i + 1]);
                writer.WriteBits((first * 45) + second, 11);
                i += 2;
            }
            else
            {
                writer.WriteBits(first, 6);
                i += 1;
            }
        }
    }

    private static void WriteByte(BitWriter writer, string content, QrSegment segment, Encoding byteEncoding)
    {
        var text = content.AsSpan(segment.CharStart, segment.CharLength);
        Span<byte> bytes = stackalloc byte[byteEncoding.GetByteCount(text)];
        byteEncoding.GetBytes(text, bytes);
        foreach (var b in bytes) writer.WriteBits(b, 8);
    }

    /// <summary>
    /// Appends a terminator (shortened to fit whatever data capacity remains), pads to the next
    /// codeword boundary, and fills the rest of <paramref name="dataCodewordCount"/> codewords by
    /// alternating the pad codewords 0xEC and 0x11.
    /// </summary>
    /// <param name="writer">The bit stream after all segments have been written.</param>
    /// <param name="dataCodewordCount">The total number of data codewords the symbol/version/level combination carries.</param>
    /// <param name="terminatorBits">The full (unshortened) terminator width.</param>
    /// <param name="lastCodewordIsHalfWidth">
    /// When <c>true</c> (Micro QR versions M1 and M3), the final codeword is 4 bits wide and padded
    /// with <c>0000</c> rather than an alternating pad codeword.
    /// </param>
    /// <exception cref="FormatException">The content already exceeds the symbol's data capacity before the terminator is even added.</exception>
    internal static byte[] Finish(BitWriter writer, int dataCodewordCount, int terminatorBits, bool lastCodewordIsHalfWidth)
    {
        var finalWidth = lastCodewordIsHalfWidth ? 4 : 8;
        var fullByteCodewordCount = dataCodewordCount - (lastCodewordIsHalfWidth ? 1 : 0);
        var fullBytesTargetBits = fullByteCodewordCount * 8;
        var targetBits = fullBytesTargetBits + (lastCodewordIsHalfWidth ? finalWidth : 0);

        if (writer.BitCount > targetBits)
            throw new FormatException($"Content requires {writer.BitCount} bits, exceeding the {targetBits}-bit data capacity.");

        var terminator = Math.Clamp(targetBits - writer.BitCount, 0, terminatorBits);
        writer.WriteBits(0, terminator);

        if (writer.BitCount < fullBytesTargetBits)
        {
            var toByteBoundary = (8 - (writer.BitCount % 8)) % 8;
            writer.WriteBits(0, toByteBoundary);

            var padToggle = 0;
            while (writer.BitCount < fullBytesTargetBits)
            {
                writer.WriteBits(QrTables.PadCodewords[padToggle % 2], 8);
                padToggle++;
            }
        }

        if (writer.BitCount < targetBits)
            writer.WriteBits(0, targetBits - writer.BitCount);

        var buffer = writer.ToArray();
        var result = new byte[dataCodewordCount];
        Array.Copy(buffer, result, fullByteCodewordCount);
        if (lastCodewordIsHalfWidth) result[dataCodewordCount - 1] = (byte)(buffer[fullByteCodewordCount] >> 4);
        return result;
    }
}
