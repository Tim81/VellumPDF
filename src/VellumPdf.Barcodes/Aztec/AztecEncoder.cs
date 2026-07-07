// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Aztec;

// Bit-stuffing, size selection and the mode message are authored from ISO/IEC 24778 clauses 6, 7.2
// and 7.3.3; zint and zxing-cpp are used only as decode/cross-check oracles in this package's
// tests, never as source.

/// <summary>
/// Orchestrates full Aztec Code encoding: high-level bit-stream encoding
/// (<see cref="AztecHighLevelEncoder"/>), bit-stuffed codeword formation, symbol-size selection
/// (<see cref="AztecSymbolInfo"/>) from the requested <see cref="AztecCode.ErrorCorrectionPercent"/>
/// and <see cref="AztecCode.Format"/>, Reed-Solomon error correction (<see cref="ReedSolomonBinary"/>)
/// over the size's own Galois field, the error-corrected mode message, and placement
/// (<see cref="AztecPlacement"/>).
/// </summary>
internal static class AztecEncoder
{
    /// <summary>The mode message's own Reed-Solomon field (ISO/IEC 24778 clause 7.2.4), independent of the data message's field.</summary>
    private static readonly ReedSolomonBinary ModeMessageReedSolomon = new(GaloisField.Gf16, firstRoot: 1);

    internal static BarcodeMatrix Encode(AztecCode barcode)
    {
        var content = barcode.Bytes ?? ToLatin1Bytes(barcode.Text!);
        var rawBits = AztecHighLevelEncoder.Encode(content);

        var (size, dataCodewords) = SelectSize(rawBits, barcode.Format, barcode.ErrorCorrectionPercent);

        var ecCount = size.CodewordCount - dataCodewords.Length;
        var errorCorrection = new ReedSolomonBinary(size.Field, firstRoot: 1).ComputeRemainder(dataCodewords, ecCount);

        var allCodewords = new int[size.CodewordCount];
        dataCodewords.CopyTo(allCodewords, 0);
        errorCorrection.CopyTo(allCodewords, dataCodewords.Length);

        var modeMessageBits = BuildModeMessage(size, dataCodewords.Length);

        return AztecPlacement.Build(size, modeMessageBits, allCodewords);
    }

    private static byte[] ToLatin1Bytes(string content)
    {
        var bytes = new byte[content.Length];
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c > 0xFF)
                throw new FormatException($"\"{content}\" contains a character outside ISO/IEC 8859-1 (Latin-1) at index {i}; Aztec Code text content can only encode single bytes (use the byte[] constructor for arbitrary binary data).");
            bytes[i] = (byte)c;
        }

        return bytes;
    }

    /// <summary>
    /// Finds the smallest symbol (honouring <paramref name="format"/>) whose data-codeword capacity
    /// — after reserving <paramref name="ecPercent"/>% of its total capacity plus 3 codewords for
    /// error correction (ISO/IEC 24778 clause 4.1.e) — holds <paramref name="messageBits"/> once bit-stuffed
    /// to that size's codeword width, returning the size and its bit-stuffed data codewords.
    /// </summary>
    /// <exception cref="FormatException">The content does not fit any candidate size at the requested <paramref name="format"/>.</exception>
    internal static (AztecSymbolSize Size, int[] DataCodewords) SelectSize(List<bool> messageBits, AztecFormat format, int ecPercent)
    {
        var stuffedByWordBits = new Dictionary<int, int[]>();

        foreach (var size in Candidates(format))
        {
            if (!stuffedByWordBits.TryGetValue(size.WordBits, out var stuffed))
            {
                stuffed = StuffCodewords(messageBits, size.WordBits);
                stuffedByWordBits[size.WordBits] = stuffed;
            }

            var neededEc = (int)Math.Ceiling(size.CodewordCount * ecPercent / 100.0) + 3;
            var maxData = size.CodewordCount - neededEc;

            // The mode message's own dataword-count field is 6 bits (compact) or 11 bits (full-range
            // — ISO/IEC 24778 clause 7.2.3), capping the data codeword count it can express regardless
            // of how much capacity the symbol geometrically has.
            var modeMessageCap = size.IsCompact ? 64 : 2048;

            if (stuffed.Length <= maxData && stuffed.Length <= modeMessageCap)
                return (size, stuffed);
        }

        throw new FormatException(
            $"Content needs more data-codeword capacity than the largest {DescribeFormat(format)} Aztec Code symbol provides at {ecPercent}% error correction.");
    }

    private static IEnumerable<AztecSymbolSize> Candidates(AztecFormat format) => format switch
    {
        AztecFormat.Compact => AztecSymbolInfo.Compact,
        AztecFormat.FullRange => AztecSymbolInfo.FullRange,
        _ => AztecSymbolInfo.Compact.Concat(AztecSymbolInfo.FullRange),
    };

    private static string DescribeFormat(AztecFormat format) => format switch
    {
        AztecFormat.Compact => "compact",
        AztecFormat.FullRange => "full-range",
        _ => "compact or full-range",
    };

    /// <summary>
    /// Builds the error-corrected mode message (ISO/IEC 24778 clause 7.2): the dataword value —
    /// <c>(layers - 1)</c> in the top 2 (compact) or 5 (full-range) bits, <c>(dataCodewordCount - 1)</c>
    /// in the remaining 6 or 11 bits — split into 4-bit words, followed by Reed-Solomon check words
    /// over GF(16) (5 check words for compact, 6 for full-range), as a flat bit list, most
    /// significant bit first.
    /// </summary>
    internal static List<bool> BuildModeMessage(AztecSymbolSize size, int dataCodewordCount)
    {
        var layersMinus1 = size.Layers - 1;
        var codewordsMinus1 = dataCodewordCount - 1;

        int[] dataWords;
        int checkWordCount;
        if (size.IsCompact)
        {
            var value = (layersMinus1 << 6) | codewordsMinus1; // 2 + 6 = 8 bits = 2 nibbles
            dataWords = [(value >> 4) & 0xF, value & 0xF];
            checkWordCount = 5;
        }
        else
        {
            var value = (layersMinus1 << 11) | codewordsMinus1; // 5 + 11 = 16 bits = 4 nibbles
            dataWords = [(value >> 12) & 0xF, (value >> 8) & 0xF, (value >> 4) & 0xF, value & 0xF];
            checkWordCount = 6;
        }

        var checkWords = ModeMessageReedSolomon.ComputeRemainder(dataWords, checkWordCount);

        var bits = new List<bool>((dataWords.Length + checkWords.Length) * 4);
        foreach (var word in dataWords) AppendNibble(bits, word);
        foreach (var word in checkWords) AppendNibble(bits, word);
        return bits;
    }

    private static void AppendNibble(List<bool> bits, int value)
    {
        for (var b = 3; b >= 0; b--)
            bits.Add(((value >> b) & 1) != 0);
    }

    /// <summary>
    /// Splits <paramref name="bits"/> into <paramref name="wordBits"/>-wide data codewords,
    /// bit-stuffing as it goes so that no codeword is all-0 or all-1 (ISO/IEC 24778 clause 7.3.3;
    /// a data codeword of either value is treated as illegal, since it is indistinguishable from an
    /// erasure): whenever the next <c>wordBits - 1</c> bits of the stream are all the same value,
    /// that run becomes a codeword completed with the *complementary* bit rather than the actual
    /// next stream bit, and only <c>wordBits - 1</c> bits are consumed from the stream — the
    /// complementary bit is synthesized, not read. Otherwise the next <c>wordBits</c> bits are
    /// consumed as-is (safe: since they are not all identical, they cannot be all-0 or all-1
    /// regardless of the last bit's value). The final, necessarily partial codeword is padded with
    /// 1-bits; if that padding happens to complete an all-1 codeword, its last bit is flipped to 0.
    /// </summary>
    internal static int[] StuffCodewords(List<bool> bits, int wordBits)
    {
        var codewords = new List<int>();
        var pos = 0;

        while (pos < bits.Count)
        {
            var available = bits.Count - pos;
            if (available >= wordBits)
            {
                var first = bits[pos];
                var allSame = true;
                for (var k = 1; k < wordBits - 1; k++)
                {
                    if (bits[pos + k] != first) { allSame = false; break; }
                }

                if (allSame)
                {
                    var value = 0;
                    for (var k = 0; k < wordBits - 1; k++) value = (value << 1) | (bits[pos + k] ? 1 : 0);
                    value = (value << 1) | (first ? 0 : 1);
                    codewords.Add(value);
                    pos += wordBits - 1;
                }
                else
                {
                    var value = 0;
                    for (var k = 0; k < wordBits; k++) value = (value << 1) | (bits[pos + k] ? 1 : 0);
                    codewords.Add(value);
                    pos += wordBits;
                }
            }
            else
            {
                var value = 0;
                for (var k = 0; k < wordBits; k++)
                {
                    var bit = k < available ? bits[pos + k] : true;
                    value = (value << 1) | (bit ? 1 : 0);
                }

                if (value == (1 << wordBits) - 1) value &= ~1;
                codewords.Add(value);
                pos = bits.Count;
            }
        }

        return [.. codewords];
    }
}
