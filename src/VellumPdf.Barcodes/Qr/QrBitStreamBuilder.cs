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

    /// <summary>
    /// Writes the FNC1-in-first-position mode indicator (ISO/IEC 18004 §7.4.8.2), marking the
    /// symbol as GS1-formatted. Callers write this after <see cref="WriteUtf8EciHeader"/> (when
    /// both apply) and before the first data segment, per the clause's ordering rule.
    /// </summary>
    internal static void WriteFnc1FirstPosition(BitWriter writer) =>
        writer.WriteBits(QrTables.Fnc1FirstPositionModeIndicator, QrTables.ModeIndicatorBits);

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

            // ISO/IEC 18004 §7.4.4: the byte-mode character count indicator holds the number of
            // 8-bit code words, not the number of characters — these diverge for any multi-byte
            // UTF-8 sequence, so RuneCount (correct for numeric/alphanumeric) cannot be reused here.
            var count = segment.Mode == QrSegmentMode.Byte
                ? byteEncoding.GetByteCount(content.AsSpan(segment.CharStart, segment.CharLength))
                : segment.RuneCount;
            writer.WriteBits(count, countBits(segment.Mode));

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
                case QrSegmentMode.Kanji:
                    WriteKanji(writer, content, segment);
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
    /// Writes a Kanji-mode segment (§7.4.6): each rune's Shift-JIS X 0208 code, less its block's
    /// base value, has its two bytes repacked into a single 13-bit codeword (<c>msb * 0xC0 + lsb</c>)
    /// rather than being written as the raw 16-bit Shift-JIS value.
    /// </summary>
    private static void WriteKanji(BitWriter writer, string content, QrSegment segment)
    {
        var text = content.AsSpan(segment.CharStart, segment.CharLength);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!ShiftJisTable.TryGetShiftJis(rune.Value, out var shiftJis))
                throw new InvalidOperationException(
                    $"Rune '{rune}' was placed in a Kanji segment but has no Shift-JIS mapping; this is a QrSegmenter eligibility bug.");

            var blockOffset = shiftJis <= 0x9FFC ? 0x8140 : 0xC140;
            var d = shiftJis - blockOffset;
            var value13 = (((d >> 8) & 0xFF) * 0xC0) + (d & 0xFF);
            writer.WriteBits(value13, 13);
        }
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
    /// with <c>0000</c> rather than an alternating pad codeword. The returned byte keeps those 4
    /// bits in their natural, byte-aligned position (the high nibble, as <see cref="BitWriter.ToArray"/>
    /// already leaves them) rather than shifting them down to a compact 0-15 value: Reed-Solomon
    /// treats every codeword as a GF(256) element by its raw byte value, and a decoder reconstructs
    /// this half codeword the same byte-aligned way (4 real bits, then 4 zero bits), so shifting it
    /// here would feed error-correction generation a different number to the one a decoder checks
    /// against, corrupting the codeword's syndrome even though every module is drawn "correctly".
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
        if (lastCodewordIsHalfWidth) result[dataCodewordCount - 1] = buffer[fullByteCodewordCount];
        return result;
    }
}
