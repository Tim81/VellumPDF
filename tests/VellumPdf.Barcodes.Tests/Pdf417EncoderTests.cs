// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Pdf417;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// End-to-end tests for <see cref="Pdf417Encoder"/> and <see cref="Pdf417Barcode"/>: row
/// indicators against ISO/IEC 15438's own worked example, error-correction-level resolution, and
/// overall matrix assembly.
/// </summary>
public sealed class Pdf417EncoderTests
{
    [Fact]
    public void GetMatrix_threeRowsThreeColumnsLevelOne_rowIndicatorsMatchUssSpecWorkedExample()
    {
        // ISO/IEC 15438 section 2.2.3's own worked example: "if a symbol has 3 rows, 3 columns,
        // and error correction level 1, the (L1, L2, L3) and (R1, R2, R3) are (0, 5, 2) and
        // (2, 0, 5) respectively." (1-indexed rows; row 0 here is the spec's row 1.)
        var barcode = new Pdf417Barcode(new byte[] { 65 }) { Columns = 3, Rows = 3, ErrorCorrectionLevel = 1 };
        var matrix = barcode.GetMatrix();

        Assert.Equal(3, matrix.Height);
        Assert.Equal(Pdf417Dimensions.WidthModules(3), matrix.Width);

        AssertRowIndicators(matrix, row: 0, cluster: 0, expectedLeft: 0, expectedRight: 2);
        AssertRowIndicators(matrix, row: 1, cluster: 3, expectedLeft: 5, expectedRight: 0);
        AssertRowIndicators(matrix, row: 2, cluster: 6, expectedLeft: 2, expectedRight: 5);
    }

    [Fact]
    public void GetMatrix_everyRow_startsWithStartPatternAndEndsWithStopPattern()
    {
        var matrix = new Pdf417Barcode("Hello, PDF417!").GetMatrix();
        for (var row = 0; row < matrix.Height; row++)
        {
            Assert.Equal(Pdf417Tables.StartPattern, ReadPattern(matrix, row, 0, Pdf417Tables.PatternModules));
            Assert.Equal(Pdf417Tables.StopPattern, ReadPattern(matrix, row, matrix.Width - Pdf417Tables.StopPatternModules, Pdf417Tables.StopPatternModules));
        }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(40, 2)]
    [InlineData(41, 3)]
    [InlineData(160, 3)]
    [InlineData(161, 4)]
    [InlineData(320, 4)]
    [InlineData(321, 5)]
    [InlineData(863, 5)]
    public void ResolveRecommendedLevel_matchesIsoRecommendedMinimumTable(int dataCodewords, int expectedLevel) =>
        Assert.Equal(expectedLevel, Pdf417Encoder.ResolveRecommendedLevel(dataCodewords));

    [Fact]
    public void ResolveRecommendedLevel_beyondTableCeiling_fallsBackToTheHighestLevelThatStillFits()
    {
        // Level 5's own ceiling (863) is already the largest any level above 0 can hold, since
        // higher levels reserve more codewords for error correction, not fewer. For 864-895 data
        // codewords, level 4 (ceiling 895) is the highest that still fits.
        Assert.Equal(4, Pdf417Encoder.ResolveRecommendedLevel(864));
        Assert.Equal(4, Pdf417Encoder.ResolveRecommendedLevel(895));
        Assert.Equal(3, Pdf417Encoder.ResolveRecommendedLevel(896));
        Assert.Equal(0, Pdf417Encoder.ResolveRecommendedLevel(925));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 32)]
    [InlineData(5, 64)]
    [InlineData(6, 128)]
    [InlineData(7, 256)]
    [InlineData(8, 512)]
    public void GetMatrix_explicitLevel_addsTwoToThePowerLevelPlusOneErrorCorrectionCodewords(int level, int expectedEcCodewords)
    {
        var barcode = new Pdf417Barcode("Hello") { ErrorCorrectionLevel = level };
        var matrix = barcode.GetMatrix();

        // Total codewords minus the data-region length (recovered from the length descriptor,
        // which is always the very first data codeword) equals the error-correction codeword count.
        var firstDataPattern = ReadPattern(matrix, 0, Pdf417Tables.PatternModules * 2, Pdf417Tables.PatternModules);
        var lengthDescriptor = FindCodewordValue(0, firstDataPattern);
        var columns = (matrix.Width - (Pdf417Tables.PatternModules * 3) - Pdf417Tables.StopPatternModules) / Pdf417Tables.PatternModules;
        var totalCodewords = matrix.Height * columns;
        Assert.Equal(expectedEcCodewords, totalCodewords - lengthDescriptor);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(9)]
    public void GetMatrix_errorCorrectionLevelOutsideRange_throwsArgumentException(int level) =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { ErrorCorrectionLevel = level }.GetMatrix());

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void GetMatrix_columnsOutsideRange_throwsArgumentException(int columns) =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { Columns = columns }.GetMatrix());

    [Theory]
    [InlineData(2)]
    [InlineData(91)]
    public void GetMatrix_rowsOutsideRange_throwsArgumentException(int rows) =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { Rows = rows }.GetMatrix());

    [Fact]
    public void GetMatrix_rowHeightBelowMinimum_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { RowHeight = 2.9 }.GetMatrix());

    [Fact]
    public void GetMatrix_preferredAspectRatioNotPositive_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { PreferredAspectRatio = 0 }.GetMatrix());

    [Fact]
    public void GetMatrix_contentOutsideLatin1_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new Pdf417Barcode("cafĀ").GetMatrix());

    [Fact]
    public void GetMatrix_contentTooLargeForForcedDimensions_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new Pdf417Barcode(new string('x', 500)) { Columns = 1, Rows = 3 }.GetMatrix());

    [Fact]
    public void GetMatrix_isCachedAndDeterministic()
    {
        var barcode = new Pdf417Barcode("Determinism check");
        var a = barcode.GetMatrix();
        var b = barcode.GetMatrix();
        Assert.Same(a, b);

        var c = new Pdf417Barcode("Determinism check").GetMatrix();
        AssertMatricesEqual(a, c);
    }

    [Fact]
    public void GetMatrix_byteContent_usesByteCompactionEndToEnd()
    {
        var matrix = new Pdf417Barcode(new byte[] { 0, 1, 2, 255 }).GetMatrix();
        Assert.True(matrix.Width > 0);
        Assert.True(matrix.Height >= Pdf417Dimensions.MinRows);
    }

    private static void AssertRowIndicators(BarcodeMatrix matrix, int row, int cluster, int expectedLeft, int expectedRight)
    {
        var leftPattern = ReadPattern(matrix, row, Pdf417Tables.PatternModules, Pdf417Tables.PatternModules);
        Assert.Equal(Pdf417Tables.GetPattern(cluster, expectedLeft), leftPattern);

        var rightStart = matrix.Width - Pdf417Tables.StopPatternModules - Pdf417Tables.PatternModules;
        var rightPattern = ReadPattern(matrix, row, rightStart, Pdf417Tables.PatternModules);
        Assert.Equal(Pdf417Tables.GetPattern(cluster, expectedRight), rightPattern);
    }

    private static uint ReadPattern(BarcodeMatrix matrix, int row, int startX, int moduleCount)
    {
        var pattern = 0u;
        for (var m = 0; m < moduleCount; m++)
            pattern = (pattern << 1) | (matrix.IsDark(startX + m, row) ? 1u : 0u);
        return pattern;
    }

    private static int FindCodewordValue(int cluster, uint pattern)
    {
        var patterns = Pdf417Tables.GetClusterPatterns(cluster);
        for (var i = 0; i < patterns.Length; i++)
            if (patterns[i] == pattern) return i;
        throw new InvalidOperationException("Pattern not found in cluster 0.");
    }

    private static void AssertMatricesEqual(BarcodeMatrix a, BarcodeMatrix b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                Assert.Equal(a.IsDark(x, y), b.IsDark(x, y));
    }
}
