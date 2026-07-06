// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.DataMatrix;

// Padding, interleaving and block assembly are authored from ISO/IEC 16022 §5.2.1/§5.3.2; zint
// and zxing-cpp are used only as decode/cross-check oracles in this package's tests, never as source.

/// <summary>
/// Orchestrates full Data Matrix ECC 200 encoding: high-level compaction
/// (<see cref="DataMatrixHighLevelEncoder"/>), symbol-size selection
/// (<see cref="DataMatrixSymbolSizes"/>), padding, per-block Reed-Solomon error correction
/// (<see cref="ReedSolomonBinary"/> over <see cref="GaloisField.Gf256"/>) with interleaving,
/// placement (<see cref="DataMatrixPlacement"/>), and finder/timing pattern assembly.
/// </summary>
internal static class DataMatrixEncoder
{
    internal static BarcodeMatrix Encode(DataMatrixBarcode barcode)
    {
        var content = barcode.Bytes is { } bytes
            ? DataMatrixHighLevelEncoder.EncodeBytes(bytes, barcode.Gs1)
            : DataMatrixHighLevelEncoder.EncodeText(barcode.Text!, barcode.Gs1);

        var size = DataMatrixSymbolSizes.Resolve(content.Count, barcode.Shape);

        var dataCodewords = new int[size.DataCodewords];
        content.CopyTo(dataCodewords);
        PadRemaining(dataCodewords, content.Count);

        var interleaved = InterleaveWithErrorCorrection(dataCodewords, size);
        var mapping = DataMatrixPlacement.Place(interleaved, size.MappingRows, size.MappingColumns);

        return AssembleSymbol(mapping, size);
    }

    /// <summary>
    /// Fills unused data-region capacity with the pad codeword (129) followed by the 253-state
    /// randomizing algorithm (ISO/IEC 16022:2024 §5.2.1): for the pad codeword at absolute 1-based
    /// data-codeword position <paramref name="contentLength"/> and onward, <c>R = ((149*P) mod
    /// 253) + 1</c> and the codeword is <c>(129 + R) mod 254</c> — except the very first pad
    /// codeword, which is always the literal, unrandomized value 129.
    /// </summary>
    private static void PadRemaining(int[] dataCodewords, int contentLength)
    {
        if (contentLength >= dataCodewords.Length) return;

        dataCodewords[contentLength] = DataMatrixHighLevelEncoder.PadCodeword;
        for (var i = contentLength + 1; i < dataCodewords.Length; i++)
        {
            var position = i + 1; // 1-based absolute position in the data-codeword stream
            var randomizer = ((149 * position) % 253) + 1;
            var temp = DataMatrixHighLevelEncoder.PadCodeword + randomizer;
            dataCodewords[i] = temp <= 254 ? temp : temp - 254;
        }
    }

    /// <summary>
    /// Splits <paramref name="dataCodewords"/> across <see cref="DataMatrixSize.Blocks"/>
    /// interleaved Reed-Solomon blocks per ISO/IEC 16022:2024 §5.3.2/Annex A: data codeword
    /// <c>i</c> (0-based, in the original stream order) belongs to block <c>i mod
    /// DataMatrixSize.Blocks</c> — round-robin, not contiguous chunks — so block <c>b</c>'s
    /// <c>j</c>-th codeword is <c>dataCodewords[b + j * Blocks]</c> (shorter blocks, per
    /// <see cref="DataMatrixSize.DataCodewordsInBlock"/>, simply stop one round-robin cycle early).
    /// Each block's Reed-Solomon remainder is computed independently over just its own codewords.
    /// The assembled stream places the data codewords in their original, unmodified sequence —
    /// round-robin assignment and round-robin readout are inverse permutations, so this is exactly
    /// what putting them "back" after block assignment amounts to — followed by the error
    /// codewords interleaved round-robin across blocks: error codeword 0 of every block, then
    /// error codeword 1 of every block, and so on (every block contributes the same count, since
    /// <see cref="DataMatrixSize.ErrorCodewordsPerBlock"/> is uniform). With exactly one block
    /// (every size except 52x52 and the nine larger squares) this degenerates to plain
    /// data-then-error-correction concatenation, byte-identical to treating the whole stream as a
    /// single Reed-Solomon block.
    /// </summary>
    private static int[] InterleaveWithErrorCorrection(int[] dataCodewords, DataMatrixSize size)
    {
        var reedSolomon = new ReedSolomonBinary(GaloisField.Gf256, firstRoot: 1);
        var blocks = size.Blocks;
        var ecPerBlock = size.ErrorCodewordsPerBlock;

        var blockError = new int[blocks][];
        for (var block = 0; block < blocks; block++)
        {
            var length = size.DataCodewordsInBlock(block);
            var chunk = new int[length];
            for (var j = 0; j < length; j++)
                chunk[j] = dataCodewords[block + (j * blocks)];
            blockError[block] = reedSolomon.ComputeRemainder(chunk, ecPerBlock);
        }

        var result = new int[size.DataCodewords + size.ErrorCodewords];
        Array.Copy(dataCodewords, result, size.DataCodewords);

        var index = size.DataCodewords;
        for (var row = 0; row < ecPerBlock; row++)
            for (var block = 0; block < blocks; block++)
                result[index++] = blockError[block][row];

        return result;
    }

    /// <summary>
    /// Assembles the full symbol matrix from the placed mapping matrix: slices each data region's
    /// interior back out and surrounds it with its own finder pattern — a solid "L" (left column
    /// and bottom row) plus alternating "timing" pattern (top row and right column, dark at every
    /// even offset from its own starting corner) — per ISO/IEC 16022:2024 §6.2.1.
    /// </summary>
    private static BarcodeMatrix AssembleSymbol(bool[,] mapping, DataMatrixSize size)
    {
        var matrix = new BarcodeMatrix(size.SymbolColumns, size.SymbolRows);

        for (var regionRow = 0; regionRow < size.RegionRows; regionRow++)
        {
            for (var regionColumn = 0; regionColumn < size.RegionColumns; regionColumn++)
            {
                var top = regionRow * (size.RegionInteriorRows + 2);
                var left = regionColumn * (size.RegionInteriorColumns + 2);
                DrawRegionFinder(matrix, top, left, size.RegionInteriorRows, size.RegionInteriorColumns);

                var mapTop = regionRow * size.RegionInteriorRows;
                var mapLeft = regionColumn * size.RegionInteriorColumns;
                for (var r = 0; r < size.RegionInteriorRows; r++)
                    for (var c = 0; c < size.RegionInteriorColumns; c++)
                        matrix.SetDark(left + 1 + c, top + 1 + r, mapping[mapTop + r, mapLeft + c]);
            }
        }

        return matrix;
    }

    private static void DrawRegionFinder(BarcodeMatrix matrix, int top, int left, int interiorRows, int interiorColumns)
    {
        var bottom = top + interiorRows + 1;
        var right = left + interiorColumns + 1;

        for (var r = top; r <= bottom; r++) matrix.SetDark(left, r, true); // solid left column
        for (var c = left; c <= right; c++) matrix.SetDark(c, bottom, true); // solid bottom row

        // Alternating timing pattern, each counted independently from its own starting corner: the
        // top row (including the top-right corner it shares with the right column) is dark at
        // even offsets from the corner adjacent to the solid left column; the right column is dark
        // at odd offsets from that same shared corner.
        for (var offset = 1; offset <= interiorColumns + 1; offset++)
            matrix.SetDark(left + offset, top, offset % 2 == 0);

        for (var offset = 1; offset <= interiorRows; offset++)
            matrix.SetDark(right, top + offset, offset % 2 != 0);
    }
}
