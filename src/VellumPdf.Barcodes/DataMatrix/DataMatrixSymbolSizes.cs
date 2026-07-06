// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.DataMatrix;

/// <summary>
/// One ECC 200 symbol size's attributes (ISO/IEC 16022:2024 clause 5.4, "Encoding procedure
/// overview" and the size table it references): the overall symbol dimensions, how many data
/// regions tile it, one data region's interior (data-bearing, excluding its own one-module-wide
/// finder perimeter) dimensions, and the Reed-Solomon block structure.
/// </summary>
/// <param name="SymbolRows">The full symbol height in modules, finder perimeter included.</param>
/// <param name="SymbolColumns">The full symbol width in modules, finder perimeter included.</param>
/// <param name="RegionRows">How many data regions tile the symbol vertically.</param>
/// <param name="RegionColumns">How many data regions tile the symbol horizontally.</param>
/// <param name="RegionInteriorRows">One data region's interior height in modules (its own finder perimeter excluded).</param>
/// <param name="RegionInteriorColumns">One data region's interior width in modules (its own finder perimeter excluded).</param>
/// <param name="DataCodewords">The symbol's total data-codeword capacity (content plus padding).</param>
/// <param name="ErrorCodewords">The symbol's total Reed-Solomon error-correction codewords.</param>
/// <param name="Blocks">
/// The number of interleaved Reed-Solomon blocks the data and error codewords split across.
/// 1 for every size except the nine largest squares (52x52 and up) and none of the rectangles.
/// </param>
internal readonly record struct DataMatrixSize(
    int SymbolRows,
    int SymbolColumns,
    int RegionRows,
    int RegionColumns,
    int RegionInteriorRows,
    int RegionInteriorColumns,
    int DataCodewords,
    int ErrorCodewords,
    int Blocks)
{
    /// <summary>The combined data-bearing module grid across every region (regions tiled edge-to-edge, each still carrying its own one-module perimeter).</summary>
    internal int MappingRows => RegionInteriorRows * RegionRows;

    /// <summary>See <see cref="MappingRows"/>.</summary>
    internal int MappingColumns => RegionInteriorColumns * RegionColumns;

    /// <summary>Error-correction codewords per block (uniform across every block, unlike <see cref="DataCodewordsInBlock"/>).</summary>
    internal int ErrorCodewordsPerBlock => ErrorCodewords / Blocks;

    /// <summary>Whether this is one of the 24 square sizes (as opposed to one of the 6 rectangular ones).</summary>
    internal bool IsSquare => SymbolRows == SymbolColumns;

    /// <summary>
    /// The number of data codewords in block <paramref name="blockIndex"/> (0-based). Data
    /// codewords split as evenly as possible across <see cref="Blocks"/>; when they do not divide
    /// evenly, the first <c>DataCodewords % Blocks</c> blocks carry one extra codeword each. Every
    /// size in this table divides evenly except 144x144 (1558 data codewords over 10 blocks: 8
    /// blocks of 156, 2 of 155).
    /// </summary>
    internal int DataCodewordsInBlock(int blockIndex)
    {
        var baseCount = DataCodewords / Blocks;
        var remainder = DataCodewords % Blocks;
        return blockIndex < remainder ? baseCount + 1 : baseCount;
    }
}

/// <summary>
/// The ECC 200 symbol-size table (ISO/IEC 16022:2024, the symbol attribute table underlying
/// clause 5.4 and Annex C): 24 square sizes (10x10 to 144x144) and 6 rectangular sizes (8x18 to
/// 16x48). Every dimension below was cross-derived two independent ways — from the region
/// grid/interior sizes via "total mapping-matrix cells / 8 = total codewords" (stated in the same
/// source table) and, separately, from the published capacity figures for the well-known sizes —
/// and the two derivations agree throughout.
///
/// <para>
/// One correction from the most commonly reproduced version of this table: 144x144 is listed
/// there with a "10 x 62" Reed-Solomon block breakdown (10 blocks of 62 codewords, 620 total) but
/// a block *count* column reading 8. 8 blocks of 62 would total only 496 — the same figure as
/// 132x132 — while the cell-count formula (132x132 mapping matrix, 17424 cells / 8 = 2178 total
/// codewords) requires 620 error-correction codewords, which only factors evenly as 10 blocks of
/// 62. This table uses 10, the value consistent with the formula and with the capacity figures
/// published elsewhere for 144x144 (1558 data codewords).
/// </para>
/// </summary>
internal static class DataMatrixSymbolSizes
{
    /// <summary>The 24 square sizes, ascending by data-codeword capacity (10x10 first, 144x144 last).</summary>
    internal static readonly DataMatrixSize[] Square =
    [
        new(10, 10, 1, 1, 8, 8, 3, 5, 1),
        new(12, 12, 1, 1, 10, 10, 5, 7, 1),
        new(14, 14, 1, 1, 12, 12, 8, 10, 1),
        new(16, 16, 1, 1, 14, 14, 12, 12, 1),
        new(18, 18, 1, 1, 16, 16, 18, 14, 1),
        new(20, 20, 1, 1, 18, 18, 22, 18, 1),
        new(22, 22, 1, 1, 20, 20, 30, 20, 1),
        new(24, 24, 1, 1, 22, 22, 36, 24, 1),
        new(26, 26, 1, 1, 24, 24, 44, 28, 1),
        new(32, 32, 2, 2, 14, 14, 62, 36, 1),
        new(36, 36, 2, 2, 16, 16, 86, 42, 1),
        new(40, 40, 2, 2, 18, 18, 114, 48, 1),
        new(44, 44, 2, 2, 20, 20, 144, 56, 1),
        new(48, 48, 2, 2, 22, 22, 174, 68, 1),
        new(52, 52, 2, 2, 24, 24, 204, 84, 2),
        new(64, 64, 4, 4, 14, 14, 280, 112, 2),
        new(72, 72, 4, 4, 16, 16, 368, 144, 4),
        new(80, 80, 4, 4, 18, 18, 456, 192, 4),
        new(88, 88, 4, 4, 20, 20, 576, 224, 4),
        new(96, 96, 4, 4, 22, 22, 696, 272, 4),
        new(104, 104, 4, 4, 24, 24, 816, 336, 6),
        new(120, 120, 6, 6, 18, 18, 1050, 408, 6),
        new(132, 132, 6, 6, 20, 20, 1304, 496, 8),
        new(144, 144, 6, 6, 22, 22, 1558, 620, 10),
    ];

    /// <summary>The 6 rectangular sizes, ascending by data-codeword capacity (8x18 first, 16x48 last).</summary>
    internal static readonly DataMatrixSize[] Rectangular =
    [
        new(8, 18, 1, 1, 6, 16, 5, 7, 1),
        new(8, 32, 1, 2, 6, 14, 10, 11, 1),
        new(12, 26, 1, 1, 10, 24, 16, 14, 1),
        new(12, 36, 1, 2, 10, 16, 22, 18, 1),
        new(16, 36, 1, 2, 14, 16, 32, 24, 1),
        new(16, 48, 1, 2, 14, 22, 49, 28, 1),
    ];

    /// <summary>
    /// Resolves the smallest symbol size whose data-codeword capacity holds
    /// <paramref name="requiredDataCodewords"/>, honouring <paramref name="shape"/>.
    /// <see cref="DataMatrixShape.Automatic"/> resolves within the square family only — matching
    /// the great majority of Data Matrix generators' default behaviour, and the well-known worked
    /// examples (e.g. the "Wikipedia" 16x16 example) that assume a square result. Rectangular
    /// sizes exist for width/height-constrained labels and are only used when
    /// <see cref="DataMatrixShape.Rectangular"/> is requested explicitly.
    /// </summary>
    /// <exception cref="FormatException"><paramref name="requiredDataCodewords"/> exceeds every candidate size's capacity.</exception>
    internal static DataMatrixSize Resolve(int requiredDataCodewords, DataMatrixShape shape) => shape switch
    {
        DataMatrixShape.Rectangular => ResolveFrom(Rectangular, requiredDataCodewords, "rectangular"),
        _ => ResolveFrom(Square, requiredDataCodewords, "square"),
    };

    private static DataMatrixSize ResolveFrom(DataMatrixSize[] sizes, int requiredDataCodewords, string familyName)
    {
        foreach (var candidate in sizes)
            if (candidate.DataCodewords >= requiredDataCodewords)
                return candidate;

        throw new FormatException(
            $"Content needs {requiredDataCodewords} data codewords, exceeding the largest {familyName} Data Matrix symbol's capacity of {sizes[^1].DataCodewords} data codewords.");
    }
}
