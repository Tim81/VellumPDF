// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for <see cref="BarcodeGeometry"/>'s module-size resolution and 1D footprint math.</summary>
public sealed class BarcodeGeometryTests
{
    [Fact]
    public void ResolveModuleSize_bothModuleSizeAndTargetWidthSet_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => BarcodeGeometry.ResolveModuleSize(2, 100, 50, 1));

    [Fact]
    public void ResolveModuleSize_neitherSet_returnsTheDefault() =>
        Assert.Equal(1.5, BarcodeGeometry.ResolveModuleSize(null, null, 50, 1.5));

    [Fact]
    public void ResolveModuleSize_moduleSizeSet_isUsedDirectly() =>
        Assert.Equal(3.0, BarcodeGeometry.ResolveModuleSize(3.0, null, 50, 1));

    [Fact]
    public void ResolveModuleSize_targetWidthSet_derivesModuleSizeFromTotalModules() =>
        Assert.Equal(2.0, BarcodeGeometry.ResolveModuleSize(null, 100, 50, 1));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ResolveModuleSize_nonPositiveOrNonFiniteModuleSize_throws(double moduleSize) =>
        Assert.Throws<ArgumentException>(() => BarcodeGeometry.ResolveModuleSize(moduleSize, null, 50, 1));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void ResolveModuleSize_nonPositiveOrNonFiniteTargetWidth_throws(double targetWidth) =>
        Assert.Throws<ArgumentException>(() => BarcodeGeometry.ResolveModuleSize(null, targetWidth, 50, 1));

    [Fact]
    public void Measure_bothModuleSizeAndTargetWidthSet_throwsArgumentException()
    {
        var barcode = new Code128Barcode("ABC") { ModuleSize = 2, TargetWidth = 100 };
        Assert.Throws<ArgumentException>(() => barcode.Measure());
    }

    [Fact]
    public void Measure_targetWidth_roundTrips()
    {
        var barcode = new Code128Barcode("ABC") { TargetWidth = 120 };
        var size = barcode.Measure();
        Assert.Equal(120.0, size.Width, 6);
    }

    [Fact]
    public void Measure_includeQuietZone_widensTheFootprint()
    {
        var withQuiet = new Code128Barcode("ABC") { ModuleSize = 1 };
        var withoutQuiet = new Code128Barcode("ABC") { ModuleSize = 1, IncludeQuietZone = false };
        Assert.True(withQuiet.Measure().Width > withoutQuiet.Measure().Width);
    }

    [Fact]
    public void Measure_showText_addsToHeight()
    {
        var withText = new Code128Barcode("ABC") { ModuleSize = 1 };
        var withoutText = new Code128Barcode("ABC") { ModuleSize = 1, ShowText = false };
        Assert.True(withText.Measure().Height > withoutText.Measure().Height);
    }
}
