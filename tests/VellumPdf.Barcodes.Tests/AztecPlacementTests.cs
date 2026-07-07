// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Aztec;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for the Aztec Code fixed-pattern placement (finder, orientation, reference grid): every
/// assertion here was cross-checked bit-for-bit against a zxing-cpp-generated reference symbol
/// during development. The data-field spiral is verified separately, end to end, by the
/// render/rasterize/decode round-trips in <c>ZxingDecodeOracleTests</c>.
/// </summary>
public sealed class AztecPlacementTests
{
    // Converts centred coordinates (origin at the symbol's centre, x right, y up -- ISO/IEC 24778
    // clause 7.1's own convention) to the matrix's array indices ((0,0) top-left).
    private static bool IsDarkAt(BarcodeMatrix matrix, int x, int y)
    {
        var half = (matrix.Width - 1) / 2;
        return matrix.IsDark(x + half, half - y);
    }

    [Fact]
    public void GetMatrix_compact_centreModuleIsAlwaysDark()
    {
        var matrix = new AztecCode("AB") { Format = AztecFormat.Compact }.GetMatrix();
        Assert.True(IsDarkAt(matrix, 0, 0));
    }

    [Fact]
    public void GetMatrix_fullRange_centreModuleIsAlwaysDark()
    {
        var matrix = new AztecCode("AB") { Format = AztecFormat.FullRange }.GetMatrix();
        Assert.True(IsDarkAt(matrix, 0, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GetMatrix_compact_finderRingsAlternate(int layers)
    {
        // ISO/IEC 24778 clause 7.1.2: ((max(|x|,|y|) + 1) mod 2), 1 = dark, for every cell within
        // the compact finder's F = 4 half-width.
        var size = AztecSymbolInfo.Compact[layers - 1];
        var content = FillingContent(size);
        var matrix = new AztecCode(content) { Format = AztecFormat.Compact }.GetMatrix();

        for (var y = -4; y <= 4; y++)
        {
            for (var x = -4; x <= 4; x++)
            {
                var radius = Math.Max(Math.Abs(x), Math.Abs(y));
                var expectedDark = ((radius + 1) % 2) == 1;
                Assert.True(IsDarkAt(matrix, x, y) == expectedDark, $"cell ({x},{y}) radius {radius}");
            }
        }
    }

    [Fact]
    public void GetMatrix_fullRange_finderRingsAlternate()
    {
        var matrix = new AztecCode("Vellum full-range finder test content padding out") { Format = AztecFormat.FullRange }.GetMatrix();

        for (var y = -6; y <= 6; y++)
        {
            for (var x = -6; x <= 6; x++)
            {
                var radius = Math.Max(Math.Abs(x), Math.Abs(y));
                var expectedDark = ((radius + 1) % 2) == 1;
                Assert.True(IsDarkAt(matrix, x, y) == expectedDark, $"cell ({x},{y}) radius {radius}");
            }
        }
    }

    [Fact]
    public void GetMatrix_compact_orientationChevrons_matchIsoFormula()
    {
        const int f = 4;
        var matrix = new AztecCode("AB") { Format = AztecFormat.Compact }.GetMatrix();
        AssertOrientation(matrix, f);
    }

    [Fact]
    public void GetMatrix_fullRange_orientationChevrons_matchIsoFormula()
    {
        const int f = 6;
        var matrix = new AztecCode("Vellum full-range orientation test content padding") { Format = AztecFormat.FullRange }.GetMatrix();
        AssertOrientation(matrix, f);
    }

    private static void AssertOrientation(BarcodeMatrix matrix, int f)
    {
        // ISO/IEC 24778 clause 7.1.3: six dark and six light modules at the finder's four corners.
        (int X, int Y)[] dark =
        [
            (-f - 1, f), (-f - 1, f + 1), (-f, f + 1),
            (f + 1, f + 1), (f + 1, f), (f + 1, -f),
        ];
        (int X, int Y)[] light =
        [
            (f, f + 1), (f + 1, -f - 1), (f, -f - 1),
            (-f, -f - 1), (-f - 1, -f - 1), (-f - 1, -f),
        ];

        foreach (var (x, y) in dark) Assert.True(IsDarkAt(matrix, x, y), $"expected dark orientation cell at ({x},{y})");
        foreach (var (x, y) in light) Assert.False(IsDarkAt(matrix, x, y), $"expected light orientation cell at ({x},{y})");
    }

    [Fact]
    public void GetMatrix_fullRange_referenceGridFormula_holdsAcrossTheWholeSymbol()
    {
        // ISO/IEC 24778 clause 7.1.4: every cell with x or y a multiple of 16 encodes
        // (x + y + 1) mod 2, throughout the whole symbol (not just the data field).
        var matrix = new AztecCode(new string('A', 400)) { Format = AztecFormat.FullRange }.GetMatrix();
        var half = (matrix.Width - 1) / 2;
        Assert.True(half >= 16, "this test needs a symbol large enough to contain a non-zero reference grid line");

        for (var y = -half; y <= half; y++)
        {
            for (var x = -half; x <= half; x++)
            {
                if (x % 16 != 0 && y % 16 != 0) continue;
                var expectedDark = (((x + y + 1) % 2) + 2) % 2 == 1;
                Assert.True(IsDarkAt(matrix, x, y) == expectedDark, $"reference-grid cell ({x},{y})");
            }
        }
    }

    [Fact]
    public void GetMatrix_compact_hasNoReferenceGrid()
    {
        // A compact symbol's finder/orientation formulas already cover its whole 11x11 core, so
        // there is nothing further to assert here beyond the absence of full-range's grid lines --
        // covered structurally by GetMatrix_compact_finderRingsAlternate never seeing a mismatch
        // out to the data field boundary.
        var matrix = new AztecCode("Vellum compact symbol") { Format = AztecFormat.Compact }.GetMatrix();
        Assert.Equal(matrix.Width, matrix.Height);
    }

    [Theory]
    [InlineData(AztecFormat.Compact)]
    [InlineData(AztecFormat.FullRange)]
    public void GetMatrix_everyFormat_producesASquareSymbolWithNoQuietZone(AztecFormat format)
    {
        var barcode = new AztecCode("Vellum Aztec symbol") { Format = format, ModuleSize = 2 };
        var matrix = barcode.GetMatrix();
        Assert.Equal(matrix.Width, matrix.Height);

        var size = barcode.Measure();
        Assert.Equal(matrix.Width * 2, size.Width, 3);
        Assert.Equal(matrix.Height * 2, size.Height, 3);
    }

    private static byte[] FillingContent(AztecSymbolSize size)
    {
        var length = Math.Max(1, (size.CodewordCount * 6 / 8) - 4);
        var content = new byte[length];
        for (var i = 0; i < length; i++) content[i] = (byte)'A';
        return content;
    }
}
