// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Code39;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for <see cref="Code39Encoder"/> and <see cref="Code39Barcode"/>.</summary>
public sealed class Code39EncoderTests
{
    [Fact]
    public void ValidateStandardContent_rejectsALowercaseLetter() =>
        Assert.Throws<ArgumentException>(() => Code39Encoder.ValidateStandardContent("code39"));

    [Fact]
    public void ValidateStandardContent_acceptsTheFullStandardSet() =>
        Code39Encoder.ValidateStandardContent("ABC 123-.$/+%");

    [Fact]
    public void Encode_nonFullAscii_rejectsALowercaseLetter()
    {
        var barcode = new Code39Barcode("code39");
        Assert.Throws<ArgumentException>(() => Code39Encoder.Encode(barcode));
    }

    [Fact]
    public void ExpandFullAscii_rejectsACharacterAboveAscii() =>
        Assert.Throws<ArgumentException>(() => Code39Encoder.ExpandFullAscii("café"));

    [Fact]
    public void Encode_fullAscii_lowercaseLetter_isAcceptedAndEncodesLonger()
    {
        var barcode = new Code39Barcode("abc") { FullAscii = true };
        var encoded = Code39Encoder.Encode(barcode);

        // Full ASCII expands each lowercase letter to a two-character shift pair ("+A" etc.),
        // so three source characters become six encoded characters, each a standard-set pattern.
        var expectedSymbolCount = 2 /* start+stop */ + 6 /* "+A+B+C" */;
        var expectedGapCount = expectedSymbolCount - 1;
        Assert.Equal((9 * expectedSymbolCount) + expectedGapCount, encoded.Runs.Count);
    }

    [Fact]
    public void Encode_hriGroup_showsOriginalContent_notTheExpandedOrDelimitedForm()
    {
        var barcode = new Code39Barcode("abc") { FullAscii = true, CheckDigit = true };
        var encoded = Code39Encoder.Encode(barcode);

        var group = Assert.Single(encoded.HriGroups);
        Assert.Equal("abc", group.Text);
        Assert.Equal(HriAnchor.Below, group.Anchor);
    }

    [Theory]
    [InlineData("1234", 10)] // sum of values 1+2+3+4=10; 10 mod 43 = 10 -> 'A'
    // Values (index into Code39Tables.Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%"):
    // V=31, E=14, L=21, L=21, U=30, M=22, 3=3, 9=9. Sum = 151. Unlike "1234"'s sum of 10, 151
    // exceeds 43 (in fact 3*43=129), so the modulo wraps around: 151 mod 43 = 22 -> 'M'.
    [InlineData("VELLUM39", 22)]
    public void ComputeCheckValue_matchesTheModulo43WorkedExample(string content, int expectedValue) =>
        Assert.Equal(expectedValue, Code39Encoder.ComputeCheckValue(content));

    [Fact]
    public void Encode_dataCharacterPattern_forA_matchesUssThirtyNineTable()
    {
        // AIM USS-39 Table 2 lists 'A' as bar/space widths wide,narrow,narrow,narrow,narrow,
        // wide,narrow,narrow,wide = WNNNNWNNW. Hardcoded independently of
        // Code39Tables.Patterns so a transposition in that table would be caught here.
        double[] expected = [2.5, 1, 1, 1, 1, 2.5, 1, 1, 2.5];

        var runs = Code39Encoder.Encode(new Code39Barcode("A")).Runs;

        // Skip the start character's 9-element pattern plus its inter-character gap (10 runs);
        // the next 9 runs are 'A's own data-character pattern.
        Assert.Equal(expected, runs.Skip(10).Take(9));
    }

    [Fact]
    public void CheckDigit_appendsTheComputedCharacterBeforeTheStopPattern()
    {
        // "1234" -> check value 10 -> Code39Tables.Characters[10] = 'A'.
        var withCheck = new Code39Barcode("1234") { CheckDigit = true };
        var withoutCheck = new Code39Barcode("1234");

        var runsWithCheck = Code39Encoder.Encode(withCheck).Runs;
        var runsWithoutCheck = Code39Encoder.Encode(withoutCheck).Runs;

        // One extra symbol (9 elements) plus its inter-character gap (1 element) = 10 more runs.
        Assert.Equal(runsWithoutCheck.Count + 10, runsWithCheck.Count);
    }

    [Fact]
    public void ComputeCheckValue_overFullAsciiExpansion_ofLowercaseA_is8()
    {
        // "a" (ASCII 97) expands to the shift pair "+A" under Full ASCII. Values (index into
        // Code39Tables.Characters): '+' = 41, 'A' = 10. Sum = 51; 51 mod 43 = 8 -> '8'.
        var expanded = Code39Encoder.ExpandFullAscii("a");
        Assert.Equal("+A", expanded);
        Assert.Equal(8, Code39Encoder.ComputeCheckValue(expanded));
    }

    [Fact]
    public void Encode_startAndStopPattern_areTheFirstAndLastNineRuns()
    {
        var encoded = Code39Encoder.Encode(new Code39Barcode("A"));
        var expected = Code39Tables.StartStopPattern.Select(e => e == 'W' ? 2.5 : 1.0).ToArray();

        Assert.Equal(expected, encoded.Runs.Take(9));
        Assert.Equal(expected, encoded.Runs.TakeLast(9));
    }

    [Fact]
    public void Encode_interCharacterGap_isASingleNarrowModule()
    {
        // "*" (9) + gap(1) + "A" (9) + gap(1) + "*" (9) = 29 runs total.
        var encoded = Code39Encoder.Encode(new Code39Barcode("A"));
        Assert.Equal(29, encoded.Runs.Count);
        Assert.Equal(1.0, encoded.Runs[9]);  // gap after start
        Assert.Equal(1.0, encoded.Runs[19]); // gap after 'A'
    }

    [Theory]
    [InlineData(1.99)]
    [InlineData(3.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Encode_wideNarrowRatioOutsideIsoRange_throwsArgumentException(double ratio)
    {
        var barcode = new Code39Barcode("A") { WideNarrowRatio = ratio };
        Assert.Throws<ArgumentException>(() => Code39Encoder.Encode(barcode));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    [InlineData(2.5)]
    public void Encode_wideNarrowRatioAtOrWithinIsoRange_succeeds(double ratio)
    {
        var barcode = new Code39Barcode("A") { WideNarrowRatio = ratio };
        Code39Encoder.Encode(barcode); // must not throw
    }

    [Fact]
    public void Encode_isDeterministic()
    {
        var a = Code39Encoder.Encode(new Code39Barcode("VELLUM-39"));
        var b = Code39Encoder.Encode(new Code39Barcode("VELLUM-39"));
        Assert.Equal(a.Runs, b.Runs);
    }

    [Fact]
    public void Encode_quietZones_areTenModulesEachSide()
    {
        var encoded = Code39Encoder.Encode(new Code39Barcode("A"));
        Assert.Equal(10, encoded.QuietZoneLeft);
        Assert.Equal(10, encoded.QuietZoneRight);
    }
}
