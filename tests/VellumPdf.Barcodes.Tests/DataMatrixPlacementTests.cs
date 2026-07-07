// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.DataMatrix;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for the ECC 200 default symbol-character placement algorithm (Annex F).
///
/// <para>
/// <see cref="Place_smallestSymbolKnownAnswer_matchesPublishedBitPlacement"/> is a genuine
/// known-answer test: the 8-codeword, 8x8-mapping-matrix example is small enough that the regular
/// diagonal "utah" sweep and its wrap-around never need a corner substitution (verified by hand
/// against the published bit-placement figure this package's guide cites), so every one of its 64
/// bits is pinned to a specific codeword and bit index. This is the strongest placement guarantee
/// this package currently makes; see <see cref="DataMatrixPlacement"/>'s remarks for what is and
/// is not yet verified for symbol sizes that need a corner substitution.
/// </para>
/// </summary>
public sealed class DataMatrixPlacementTests
{
    [Fact]
    public void Place_smallestSymbolKnownAnswer_matchesPublishedBitPlacement()
    {
        // The commonly-cited smallest ECC200 example (3 data + 5 EC codewords, an 8x8 mapping
        // matrix from a 10x10 symbol) -- see ReedSolomonBinaryTests' "three-codeword" vector for
        // where these EC values come from.
        int[] codewords = [142, 164, 186, 114, 25, 5, 88, 102];

        var modules = DataMatrixPlacement.Place(codewords, 8, 8);

        // Expected cell = codeword.bit label (1-indexed, bit 1 = MSB), transcribed from the
        // published 8x8 placement diagram.
        (int Codeword, int Bit)[,] labels =
        {
            { (2, 1), (2, 2), (3, 6), (3, 7), (3, 8), (4, 3), (4, 4), (4, 5) },
            { (2, 3), (2, 4), (2, 5), (5, 1), (5, 2), (4, 6), (4, 7), (4, 8) },
            { (2, 6), (2, 7), (2, 8), (5, 3), (5, 4), (5, 5), (1, 1), (1, 2) },
            { (1, 5), (6, 1), (6, 2), (5, 6), (5, 7), (5, 8), (1, 3), (1, 4) },
            { (1, 8), (6, 3), (6, 4), (6, 5), (8, 1), (8, 2), (1, 6), (1, 7) },
            { (7, 2), (6, 6), (6, 7), (6, 8), (8, 3), (8, 4), (8, 5), (7, 1) },
            { (7, 4), (7, 5), (3, 1), (3, 2), (8, 6), (8, 7), (8, 8), (7, 3) },
            { (7, 7), (7, 8), (3, 3), (3, 4), (3, 5), (4, 1), (4, 2), (7, 6) },
        };

        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 8; col++)
            {
                var (codeword, bit) = labels[row, col];
                var value = codewords[codeword - 1];
                var expectedDark = ((value >> (8 - bit)) & 1) != 0;
                Assert.True(
                    modules[row, col] == expectedDark,
                    $"Cell ({row},{col}) should hold codeword {codeword} bit {bit} ({(expectedDark ? "dark" : "light")}) but was {(modules[row, col] ? "dark" : "light")}.");
            }
        }
    }

    [Fact]
    public void Place_everyCellIsSetExactlyOnce_forTheKnownAnswerSize()
    {
        int[] codewords = [142, 164, 186, 114, 25, 5, 88, 102];
        var a = DataMatrixPlacement.Place(codewords, 8, 8);
        var b = DataMatrixPlacement.Place(codewords, 8, 8);

        for (var row = 0; row < 8; row++)
            for (var col = 0; col < 8; col++)
                Assert.Equal(a[row, col], b[row, col]);
    }

    [Fact]
    public void Place_producesTheRequestedDimensions()
    {
        int[] codewords = [142, 164, 186, 114, 25, 5, 88, 102];
        var modules = DataMatrixPlacement.Place(codewords, 8, 8);
        Assert.Equal(8, modules.GetLength(0));
        Assert.Equal(8, modules.GetLength(1));
    }
}
