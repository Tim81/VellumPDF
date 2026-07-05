// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Itf;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="Itf14Encoder"/> against GS1 General Specifications §5.3 / Wikipedia's
/// Interleaved 2 of 5 encoding table.
/// </summary>
public sealed class Itf14EncoderTests
{
    [Fact]
    public void NormalizeAndValidate_thirteenDigits_computesGtinCheckDigit()
    {
        // Universal GTIN check-digit algorithm applied to a 13-digit ITF-14 payload.
        const string thirteen = "1234567890120";
        var full = Itf14Encoder.NormalizeAndValidate(thirteen);
        Assert.Equal(14, full.Length);
        Assert.StartsWith(thirteen, full, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeAndValidate_fourteenDigits_andThirteenDigits_produceTheSameCanonicalForm()
    {
        var fromThirteen = Itf14Encoder.NormalizeAndValidate("1234567890128");
        var fromFourteen = Itf14Encoder.NormalizeAndValidate(fromThirteen);
        Assert.Equal(fromThirteen, fromFourteen);
    }

    [Fact]
    public void NormalizeAndValidate_wrongCheckDigit_throwsFormatException() =>
        Assert.Throws<FormatException>(() => Itf14Encoder.NormalizeAndValidate("12345678901289"));

    [Fact]
    public void NormalizeAndValidate_wrongLength_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => Itf14Encoder.NormalizeAndValidate("123"));

    [Fact]
    public void Encode_startPattern_isFourNarrowRuns()
    {
        var barcode = new Itf14Barcode("1234567890128");
        var runs = Itf14Encoder.Encode(barcode).Runs;
        Assert.Equal([1.0, 1.0, 1.0, 1.0], runs.Take(4));
    }

    [Fact]
    public void Encode_stopPattern_isWideNarrowNarrow()
    {
        var barcode = new Itf14Barcode("1234567890128") { WideNarrowRatio = 3.0 };
        var runs = Itf14Encoder.Encode(barcode).Runs;
        Assert.Equal([3.0, 1.0, 1.0], runs.TakeLast(3));
    }

    [Fact]
    public void Encode_pairRuns_matchTheInterleavedTwoOfFivePattern()
    {
        // Digits "00" interleaved: bar digit '0' = nnWWn, space digit '0' = nnWWn.
        var barcode = new Itf14Barcode("0000000000000") { WideNarrowRatio = 2.5 };
        var runs = Itf14Encoder.Encode(barcode).Runs;

        // After the 4-run start pattern, the first pair "00" interleaves bar='0' (nnWWn) with
        // space='0' (nnWWn): n,n,n,n,W,W,W,W,n,n.
        var pair = runs.Skip(4).Take(10).ToArray();
        Assert.Equal([1.0, 1.0, 1.0, 1.0, 2.5, 2.5, 2.5, 2.5, 1.0, 1.0], pair);
    }

    [Theory]
    [InlineData(2.24)]
    [InlineData(3.01)]
    [InlineData(double.NaN)]
    public void Encode_wideNarrowRatioOutsideGs1Range_throwsArgumentException(double ratio)
    {
        var barcode = new Itf14Barcode("1234567890128") { WideNarrowRatio = ratio };
        Assert.Throws<ArgumentException>(() => Itf14Encoder.Encode(barcode));
    }

    [Fact]
    public void Measure_bearerFrame_addsToWidthAndHeight()
    {
        var framed = new Itf14Barcode("1234567890128") { BearerBars = ItfBearerBarStyle.Frame, ModuleSize = 1 };
        var none = new Itf14Barcode("1234567890128") { BearerBars = ItfBearerBarStyle.None, ModuleSize = 1 };

        var framedSize = framed.Measure();
        var noneSize = none.Measure();

        Assert.True(framedSize.Width > noneSize.Width);
        Assert.True(framedSize.Height > noneSize.Height);
    }

    [Fact]
    public void Measure_bearerHorizontal_addsToHeightOnly()
    {
        var horizontal = new Itf14Barcode("1234567890128") { BearerBars = ItfBearerBarStyle.Horizontal, ModuleSize = 1 };
        var none = new Itf14Barcode("1234567890128") { BearerBars = ItfBearerBarStyle.None, ModuleSize = 1 };

        var horizontalSize = horizontal.Measure();
        var noneSize = none.Measure();

        Assert.Equal(noneSize.Width, horizontalSize.Width, 6);
        Assert.True(horizontalSize.Height > noneSize.Height);
    }
}
