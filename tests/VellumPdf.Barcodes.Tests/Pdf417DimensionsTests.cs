// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Pdf417;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for <see cref="Pdf417Dimensions"/>'s column/row solver.</summary>
public sealed class Pdf417DimensionsTests
{
    [Fact]
    public void Resolve_explicitColumnsAndRows_honoured()
    {
        var result = Pdf417Dimensions.Resolve(dataCodewords: 20, ecCodewords: 8, columns: 10, rows: 5, preferredAspectRatio: 3.0, rowHeightModules: 3.0);
        Assert.Equal(10, result.Columns);
        Assert.Equal(5, result.Rows);
        Assert.Equal(50, result.TotalCodewords);
    }

    [Fact]
    public void Resolve_explicitColumnsAndRows_tooSmallForContent_throwsFormatException() =>
        Assert.Throws<FormatException>(() => Pdf417Dimensions.Resolve(dataCodewords: 100, ecCodewords: 8, columns: 5, rows: 5, preferredAspectRatio: 3.0, rowHeightModules: 3.0));

    [Fact]
    public void Resolve_explicitColumnsAndRows_exceedingTotalLimit_throwsFormatException() =>
        Assert.Throws<FormatException>(() => Pdf417Dimensions.Resolve(dataCodewords: 100, ecCodewords: 8, columns: 30, rows: 90, preferredAspectRatio: 3.0, rowHeightModules: 3.0));

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Resolve_columnsOutsideRange_throwsArgumentException(int columns) =>
        Assert.Throws<ArgumentException>(() => Pdf417Dimensions.Resolve(dataCodewords: 20, ecCodewords: 8, columns: columns, rows: null, preferredAspectRatio: 3.0, rowHeightModules: 3.0));

    [Theory]
    [InlineData(2)]
    [InlineData(91)]
    public void Resolve_rowsOutsideRange_throwsArgumentException(int rows) =>
        Assert.Throws<ArgumentException>(() => Pdf417Dimensions.Resolve(dataCodewords: 20, ecCodewords: 8, columns: null, rows: rows, preferredAspectRatio: 3.0, rowHeightModules: 3.0));

    [Fact]
    public void Resolve_explicitColumnsOnly_solvesRows()
    {
        var result = Pdf417Dimensions.Resolve(dataCodewords: 41, ecCodewords: 9, columns: 10, rows: null, preferredAspectRatio: 3.0, rowHeightModules: 3.0);
        Assert.Equal(10, result.Columns);
        Assert.Equal(5, result.Rows); // ceil(50/10) = 5
        Assert.True(result.TotalCodewords >= 50);
    }

    [Fact]
    public void Resolve_explicitColumnsOnly_infeasibleRowCount_throwsFormatException() =>
        Assert.Throws<FormatException>(() => Pdf417Dimensions.Resolve(dataCodewords: 900, ecCodewords: 8, columns: 1, rows: null, preferredAspectRatio: 3.0, rowHeightModules: 3.0));

    [Fact]
    public void Resolve_explicitRowsOnly_solvesColumns()
    {
        var result = Pdf417Dimensions.Resolve(dataCodewords: 41, ecCodewords: 9, columns: null, rows: 10, preferredAspectRatio: 3.0, rowHeightModules: 3.0);
        Assert.Equal(10, result.Rows);
        Assert.Equal(5, result.Columns); // ceil(50/10) = 5
    }

    [Fact]
    public void Resolve_explicitRowsOnly_infeasibleColumnCount_throwsFormatException() =>
        Assert.Throws<FormatException>(() => Pdf417Dimensions.Resolve(dataCodewords: 900, ecCodewords: 8, columns: null, rows: 3, preferredAspectRatio: 3.0, rowHeightModules: 3.0));

    [Theory]
    [InlineData(10, 8)]
    [InlineData(50, 16)]
    [InlineData(200, 32)]
    public void Resolve_automatic_neverExceedsCapacityLimits(int dataCodewords, int ecCodewords)
    {
        var result = Pdf417Dimensions.Resolve(dataCodewords, ecCodewords, columns: null, rows: null, preferredAspectRatio: 3.0, rowHeightModules: 3.0);
        Assert.InRange(result.Columns, Pdf417Dimensions.MinColumns, Pdf417Dimensions.MaxColumns);
        Assert.InRange(result.Rows, Pdf417Dimensions.MinRows, Pdf417Dimensions.MaxRows);
        Assert.True(result.TotalCodewords <= Pdf417Dimensions.MaxTotalCodewords);
        Assert.True(result.TotalCodewords >= dataCodewords + ecCodewords);
    }

    [Fact]
    public void Resolve_automatic_picksColumnsClosestToPreferredAspectRatio()
    {
        // A tall aspect ratio (portrait) should need fewer columns than a wide one for the same content.
        var wide = Pdf417Dimensions.Resolve(dataCodewords: 60, ecCodewords: 16, columns: null, rows: null, preferredAspectRatio: 6.0, rowHeightModules: 3.0);
        var tall = Pdf417Dimensions.Resolve(dataCodewords: 60, ecCodewords: 16, columns: null, rows: null, preferredAspectRatio: 1.0, rowHeightModules: 3.0);
        Assert.True(wide.Columns >= tall.Columns);
    }

    [Fact]
    public void Resolve_automatic_contentExceedingMaximumCapacity_throwsFormatException() =>
        Assert.Throws<FormatException>(() => Pdf417Dimensions.Resolve(dataCodewords: 900, ecCodewords: 512, columns: null, rows: null, preferredAspectRatio: 3.0, rowHeightModules: 3.0));

    [Theory]
    [InlineData(1, 86)]
    [InlineData(2, 103)]
    [InlineData(30, 579)]
    public void WidthModules_matchesStartLeftDataRightStopFormula(int columns, int expectedWidth) =>
        Assert.Equal(expectedWidth, Pdf417Dimensions.WidthModules(columns));
}
