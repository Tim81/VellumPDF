// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Qr;

/// <summary>
/// Splits data codewords into their error-correction blocks, generates each block's Reed-Solomon
/// codewords, and interleaves the result into the final codeword sequence (ISO/IEC 18004 §7.5.2,
/// §7.6): all blocks' data codewords column by column (shorter blocks simply drop out once
/// exhausted), followed by all blocks' error-correction codewords column by column.
/// </summary>
internal static class QrBlockInterleaver
{
    /// <summary>Interleaves <paramref name="dataCodewords"/> (already sized to <see cref="QrEcBlockInfo.TotalDataCodewords"/>) per <paramref name="info"/>.</summary>
    internal static byte[] Interleave(ReadOnlySpan<byte> dataCodewords, QrEcBlockInfo info)
    {
        var blocks = new byte[info.TotalBlocks][];
        var ecBlocks = new byte[info.TotalBlocks][];
        var offset = 0;
        var index = 0;

        for (var i = 0; i < info.Group1Blocks; i++, index++)
        {
            blocks[index] = dataCodewords.Slice(offset, info.Group1DataCodewords).ToArray();
            ecBlocks[index] = ReedSolomonGf256.ComputeRemainder(blocks[index], info.EcCodewordsPerBlock);
            offset += info.Group1DataCodewords;
        }

        for (var i = 0; i < info.Group2Blocks; i++, index++)
        {
            blocks[index] = dataCodewords.Slice(offset, info.Group2DataCodewords).ToArray();
            ecBlocks[index] = ReedSolomonGf256.ComputeRemainder(blocks[index], info.EcCodewordsPerBlock);
            offset += info.Group2DataCodewords;
        }

        var result = new byte[info.TotalCodewords];
        var position = 0;
        var maxDataLength = Math.Max(info.Group1DataCodewords, info.Group2DataCodewords);
        for (var column = 0; column < maxDataLength; column++)
            foreach (var block in blocks)
                if (column < block.Length) result[position++] = block[column];

        for (var column = 0; column < info.EcCodewordsPerBlock; column++)
            foreach (var ecBlock in ecBlocks)
                result[position++] = ecBlock[column];

        return result;
    }

    /// <summary>
    /// Micro QR always uses a single block (no interleaving): computes and appends its
    /// error-correction codewords directly.
    /// </summary>
    internal static byte[] AppendSingleBlockEc(ReadOnlySpan<byte> dataCodewords, int ecCodewordCount)
    {
        var ec = ReedSolomonGf256.ComputeRemainder(dataCodewords, ecCodewordCount);
        var result = new byte[dataCodewords.Length + ec.Length];
        dataCodewords.CopyTo(result);
        ec.CopyTo(result.AsSpan(dataCodewords.Length));
        return result;
    }
}
