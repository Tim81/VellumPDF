// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.EanUpc;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="EanEncoder"/> and <see cref="EanBarcode"/> against the Wikipedia
/// International Article Number / EAN-5 / EAN-2 worked examples.
/// </summary>
public sealed class EanEncoderTests
{
    [Fact]
    public void ComputeCheckDigit_ean13Example_400638133393_is1() =>
        Assert.Equal(1, EanEncoder.ComputeCheckDigit("400638133393"));

    [Fact]
    public void ComputeCheckDigit_upcAExample_03600029145_is2() =>
        Assert.Equal(2, EanEncoder.ComputeCheckDigit("03600029145"));

    [Fact]
    public void ComputeCheckDigit_isbnBooklandExample_978020113447_is6() =>
        Assert.Equal(6, EanEncoder.ComputeCheckDigit("978020113447"));

    [Fact]
    public void EanBarcode_ean13_invalidCheckDigit_throwsFormatException() =>
        // The correct check digit for "400638133393" is 1 (see ComputeCheckDigit_ean13Example_400638133393_is1); 2 is wrong.
        Assert.Throws<FormatException>(() => new EanBarcode(EanSymbology.Ean13, "4006381333932"));

    [Fact]
    public void EanBarcode_ean13_wrongLength_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new EanBarcode(EanSymbology.Ean13, "123"));

    [Fact]
    public void EanBarcode_ean13_twelveAndThirteenDigitInputs_produceIdenticalSymbols()
    {
        var fromData = new EanBarcode(EanSymbology.Ean13, "400638133393");
        var fromFull = new EanBarcode(EanSymbology.Ean13, "4006381333931");

        Assert.Equal(fromData.Digits, fromFull.Digits);
        Assert.Equal(EanEncoder.Encode(fromData).Runs, EanEncoder.Encode(fromFull).Runs);
    }

    [Fact]
    public void EanBarcode_upcA_elevenAndTwelveDigitInputs_produceIdenticalSymbols()
    {
        var fromData = new EanBarcode(EanSymbology.UpcA, "03600029145");
        var fromFull = new EanBarcode(EanSymbology.UpcA, "036000291452");

        Assert.Equal(fromData.Digits, fromFull.Digits);
        Assert.Equal(EanEncoder.Encode(fromData).Runs, EanEncoder.Encode(fromFull).Runs);
    }

    [Fact]
    public void Ean13_fullNinetyFiveModuleRun_forDerivedGtin_matchesHandAssembledTables()
    {
        // 4003994155486: first digit 4 -> parity LGLLGG for the left group "003994";
        // right group "155486" is always R-coded. Oracle (zxing-cpp) confirms this end-to-end
        // in a later stage; this test only checks the encoder wires the tables correctly.
        const string digits = "4003994155486";
        var expectedBits =
            "101" // start guard
            + EanTables.L[0] + EanTables.G[0] + EanTables.L[3] + EanTables.L[9] + EanTables.G[9] + EanTables.G[4]
            + "01010" // middle guard
            + EanTables.R[1] + EanTables.R[5] + EanTables.R[5] + EanTables.R[4] + EanTables.R[8] + EanTables.R[6]
            + "101"; // end guard

        Assert.Equal(95, expectedBits.Length);

        var barcode = new EanBarcode(EanSymbology.Ean13, digits);
        var runs = EanEncoder.Encode(barcode).Runs;
        var actualBits = RunsToBits(runs);

        Assert.Equal(expectedBits, actualBits);
    }

    [Theory]
    [InlineData("52495", 1, "GLGLL")] // Wikipedia EAN-5 worked example
    public void ComputeEan5Checksum_andParity_matchWikipediaExample(string digits, int expectedChecksum, string expectedParity)
    {
        var checksum = EanEncoder.ComputeEan5Checksum(digits);
        Assert.Equal(expectedChecksum, checksum);
        Assert.Equal(expectedParity, EanTables.Ean5Parity[checksum]);
    }

    [Theory]
    [InlineData("53", "LG")] // Wikipedia EAN-2 worked example: 53 mod 4 = 1 -> LG
    [InlineData("00", "LL")]
    [InlineData("03", "GG")]
    public void Ean2Parity_byValueModFour_matchesWikipediaExample(string digits, string expectedParity)
    {
        var value = int.Parse(digits) % 4;
        Assert.Equal(expectedParity, EanTables.Ean2Parity[value]);
    }

    [Fact]
    public void HriGroups_ean13_hasOutsideLeftAndTwoBelowGroups()
    {
        var encoded = EanEncoder.Encode(new EanBarcode(EanSymbology.Ean13, "400638133393"));
        Assert.Equal(3, encoded.HriGroups.Count);
        Assert.Equal(HriAnchor.OutsideLeft, encoded.HriGroups[0].Anchor);
        Assert.Equal(HriAnchor.Below, encoded.HriGroups[1].Anchor);
        Assert.Equal(HriAnchor.Below, encoded.HriGroups[2].Anchor);
    }

    [Fact]
    public void HriGroups_ean8_hasTwoBelowGroups_noOutsideGroups()
    {
        var encoded = EanEncoder.Encode(new EanBarcode(EanSymbology.Ean8, "7351353"));
        Assert.Equal(2, encoded.HriGroups.Count);
        Assert.All(encoded.HriGroups, g => Assert.Equal(HriAnchor.Below, g.Anchor));
    }

    [Fact]
    public void HriGroups_upcA_hasOutsideLeft_twoBelow_andOutsideRight()
    {
        var encoded = EanEncoder.Encode(new EanBarcode(EanSymbology.UpcA, "03600029145"));
        Assert.Equal(4, encoded.HriGroups.Count);
        Assert.Equal(HriAnchor.OutsideLeft, encoded.HriGroups[0].Anchor);
        Assert.Equal(HriAnchor.Below, encoded.HriGroups[1].Anchor);
        Assert.Equal(HriAnchor.Below, encoded.HriGroups[2].Anchor);
        Assert.Equal(HriAnchor.OutsideRight, encoded.HriGroups[3].Anchor);
    }

    [Fact]
    public void HriGroups_withAddOn_includesAnAboveAnchoredGroup()
    {
        var encoded = EanEncoder.Encode(new EanBarcode(EanSymbology.Ean13, "400638133393") { AddOn = "52495" });
        Assert.Contains(encoded.HriGroups, g => g.Anchor == HriAnchor.Above && g.Text == "52495");
    }

    [Fact]
    public void AddOn_withAddOn_rightQuietZoneIsFive()
    {
        var encoded = EanEncoder.Encode(new EanBarcode(EanSymbology.Ean13, "400638133393") { AddOn = "53" });
        Assert.Equal(5, encoded.QuietZoneRight);
    }

    [Fact]
    public void AddOn_invalidLength_throwsArgumentException()
    {
        var barcode = new EanBarcode(EanSymbology.Ean13, "400638133393") { AddOn = "123" };
        Assert.Throws<ArgumentException>(() => EanEncoder.Encode(barcode));
    }

    private static string RunsToBits(IReadOnlyList<double> runs)
    {
        var sb = new System.Text.StringBuilder();
        var bar = true;
        foreach (var run in runs)
        {
            sb.Append(bar ? '1' : '0', (int)run);
            bar = !bar;
        }

        return sb.ToString();
    }
}
