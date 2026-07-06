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

    // ── UPC-E ─────────────────────────────────────────────────────────────

    [Fact]
    public void UpcE_sixDigits_defaultsToNumberSystemZero_andComputesCheckDigit()
    {
        // Known UPC-A <-> UPC-E compression pair (Wikipedia's Universal Product Code UPC-E
        // example): UPC-E "654321" expands to UPC-A "065100004327" under number system 0.
        var barcode = new EanBarcode(EanSymbology.UpcE, "654321");
        Assert.Equal("06543217", barcode.Digits);
    }

    [Fact]
    public void UpcE_sevenDigits_leadingNumberSystemOne_expandsToTheOtherKnownUpcA()
    {
        // The same six digits under number system 1 expand to UPC-A "165100004324" (Wikipedia).
        var barcode = new EanBarcode(EanSymbology.UpcE, "1654321");
        Assert.Equal("16543214", barcode.Digits);
    }

    [Fact]
    public void UpcE_eightDigits_correctCheckDigit_isAccepted()
    {
        var barcode = new EanBarcode(EanSymbology.UpcE, "16543214");
        Assert.Equal("16543214", barcode.Digits);
    }

    [Fact]
    public void UpcE_eightDigits_wrongCheckDigit_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new EanBarcode(EanSymbology.UpcE, "16543210"));

    [Fact]
    public void UpcE_invalidNumberSystem_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new EanBarcode(EanSymbology.UpcE, "2654321"));

    [Fact]
    public void UpcE_wrongLength_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new EanBarcode(EanSymbology.UpcE, "123"));

    [Fact]
    public void UpcE_nonDigitCharacter_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new EanBarcode(EanSymbology.UpcE, "65432X"));

    [Theory]
    [InlineData("065100004327", "06543217")] // last digit 0/1/2 case
    [InlineData("165100004324", "16543214")]
    [InlineData("042100005264", "04252614")] // additional case-A (last digit 0/1/2) example
    public void UpcE_fromUpcAForm_compressesToTheExpectedCanonicalDigits(string upcA, string expectedCanonical)
    {
        var barcode = new EanBarcode(EanSymbology.UpcE, upcA);
        Assert.Equal(expectedCanonical, barcode.Digits);
    }

    [Fact]
    public void UpcE_fromUpcAForm_notCompressible_throwsFormatException() =>
        // "012345678905": manufacturer/product digits carry no suppressible zero pattern.
        Assert.Throws<FormatException>(() => new EanBarcode(EanSymbology.UpcE, "012345678905"));

    // Regression coverage for a fixed bug: the last-digit 5-9 zero-suppression branch passed
    // all 6 compressed digits as the manufacturer code instead of the first 5, producing a
    // 12-digit expansion and a wrong check digit for roughly half of all UPC-E inputs. Each
    // vector below is hand-derived from the GS1 UPC-E structure table and the standard GTIN
    // check-digit algorithm (alternating weights 3, 1, ... from the rightmost data digit),
    // independently of this codebase's implementation.
    [Theory]
    // "123455": last digit 5 (the 5-9 branch) -> mfr = X1..X5 = "12345", product = "0000" + X6
    // = "00005". UPC-A data (11 digits, ns 0): "0 12345 00005" = 0,1,2,3,4,5,0,0,0,0,5.
    // Weighted sum (weight 3 on the rightmost digit, alternating): 5*3 + 0*1 + 0*3 + 0*1 + 0*3
    // + 5*1 + 4*3 + 3*1 + 2*3 + 1*1 + 0*3 = 15+0+0+0+0+5+12+3+6+1+0 = 42. Check = (10 - 42%10)
    // % 10 = 8. Canonical digits: ns(0) + six(123455) + check(8) = "01234558".
    [InlineData("123455", "01234558")]
    // "123433": last digit 3 -> mfr = X1..X3 + "0" + "0" = "123" + "00" = "12300", product =
    // "000" + X4 + X5 = "000" + "43" = "00043". UPC-A data: "0 12300 00043" =
    // 0,1,2,3,0,0,0,0,0,4,3. Weighted sum: 3*3 + 4*1 + 0*3 + 0*1 + 0*3 + 0*1 + 0*3 + 3*1 + 2*3
    // + 1*1 + 0*3 = 9+4+0+0+0+0+0+3+6+1+0 = 23. Check = (10 - 23%10) % 10 = 7. Canonical
    // digits: "0" + "123433" + "7" = "01234337".
    [InlineData("123433", "01234337")]
    // "567894": last digit 4 -> mfr = X1..X4 + "0" = "5678" + "0" = "56780", product = "0000" +
    // X5 = "00009". UPC-A data: "0 56780 00009" = 0,5,6,7,8,0,0,0,0,0,9. Weighted sum, right to
    // left (weight 3 on the rightmost digit, alternating): 9*3 + 0*1 + 0*3 + 0*1 + 0*3 + 0*1 +
    // 8*3 + 7*1 + 6*3 + 5*1 + 0*3 = 27+0+0+0+0+0+24+7+18+5+0 = 81. Check = (10 - 81%10) % 10 =
    // 9. Canonical digits: "0" + "567894" + "9" = "05678949".
    [InlineData("567894", "05678949")]
    public void UpcE_sixDigits_lastDigitThreeFourOrFiveToNine_expandsToHandDerivedCanonicalDigits(
        string six, string expectedCanonical)
    {
        var barcode = new EanBarcode(EanSymbology.UpcE, six);
        Assert.Equal(expectedCanonical, barcode.Digits);
    }

    // Every existing last-digit-0/1/2 vector in this file happens to use last digit 1
    // ("654321", "1654321", "042100005264" all end in 1); these two hand-derived vectors cover
    // last digit 0 and last digit 2 of that same branch, independently of this codebase's
    // implementation.
    [Theory]
    // "123450": last digit 0 -> mfr = X1 + X2 + X6 + "00" = "1" + "2" + "0" + "00" = "12000",
    // product = "00" + X3 + X4 + X5 = "00" + "345" = "00345". UPC-A data (11 digits, ns 0):
    // "0 12000 00345" = 0,1,2,0,0,0,0,0,3,4,5. Weighted sum (weight 3 on the rightmost digit,
    // alternating): 5*3 + 4*1 + 3*3 + 0*1 + 0*3 + 0*1 + 0*3 + 0*1 + 2*3 + 1*1 + 0*3 =
    // 15+4+9+0+0+0+0+0+6+1+0 = 35. Check = (10 - 35%10) % 10 = 5. Canonical digits:
    // ns(0) + six(123450) + check(5) = "01234505".
    [InlineData("123450", "01234505")]
    // "123452": last digit 2 -> mfr = X1 + X2 + X6 + "00" = "1" + "2" + "2" + "00" = "12200",
    // product = "00" + X3 + X4 + X5 = "00" + "345" = "00345". UPC-A data: "0 12200 00345" =
    // 0,1,2,2,0,0,0,0,3,4,5. Weighted sum: 5*3 + 4*1 + 3*3 + 0*1 + 0*3 + 0*1 + 0*3 + 2*1 + 2*3 +
    // 1*1 + 0*3 = 15+4+9+0+0+0+0+2+6+1+0 = 37. Check = (10 - 37%10) % 10 = 3. Canonical digits:
    // "0" + "123452" + "3" = "01234523".
    [InlineData("123452", "01234523")]
    public void UpcE_sixDigits_lastDigitZeroOrTwo_expandsToHandDerivedCanonicalDigits(
        string six, string expectedCanonical)
    {
        var barcode = new EanBarcode(EanSymbology.UpcE, six);
        Assert.Equal(expectedCanonical, barcode.Digits);
    }

    // Round-trip: compressing each vector's implied full UPC-A number (11 data digits + the
    // hand-derived check digit above) back down must reproduce the original six compressed
    // digits, proving ExpandUpcEToUpcA and CompressUpcAToUpcE agree in both directions.
    [Theory]
    [InlineData("012345000058", "123455")]
    [InlineData("012300000437", "123433")]
    [InlineData("056780000099", "567894")]
    public void UpcE_lastDigitThreeFourOrFiveToNine_upcARoundTripsBackToOriginalSixDigits(
        string upcA12, string expectedSix)
    {
        var barcode = new EanBarcode(EanSymbology.UpcE, upcA12);
        Assert.Equal(expectedSix, barcode.Digits[1..7]);
    }

    [Fact]
    public void UpcE_bits_useTheParityTableForItsCheckDigitAndNumberSystem()
    {
        // "06543217": number system 0, check digit 7 -> parity GLGLGL (EanTables.UpcESystem0Parity[7]).
        var barcode = new EanBarcode(EanSymbology.UpcE, "654321");
        var expectedBits =
            "101" // start guard
            + EanTables.G[6] + EanTables.L[5] + EanTables.G[4] + EanTables.L[3] + EanTables.G[2] + EanTables.L[1]
            + "010101"; // special 6-module end guard (no middle guard)

        Assert.Equal(51, expectedBits.Length);

        var runs = EanEncoder.Encode(barcode).Runs;
        Assert.Equal(expectedBits, RunsToBits(runs));
    }

    [Fact]
    public void UpcE_bits_numberSystemOne_usesTheComplementParityTable()
    {
        // "16543214": number system 1, check digit 4. The standard GS1 UPC-E parity table (also
        // reproduced on Wikipedia's Universal Product Code page) gives check-digit-4 parity as
        // EOEEOO (E=even=G, O=odd=L) for number system 0 -> GLGGLL, and the number-system-1 table
        // is that pattern with every letter swapped -> LGLLGG. The LGLLGG sequence is hardcoded
        // literally here (not read from EanTables.UpcESystem1Parity) so a typo in that table's
        // check-digit-4 row would be caught rather than silently matched.
        var barcode = new EanBarcode(EanSymbology.UpcE, "1654321");
        var expectedBits =
            "101" // start guard
            + EanTables.L[6] + EanTables.G[5] + EanTables.L[4] + EanTables.L[3] + EanTables.G[2] + EanTables.G[1]
            + "010101"; // special 6-module end guard (no middle guard)

        Assert.Equal(51, expectedBits.Length);

        var runs = EanEncoder.Encode(barcode).Runs;
        Assert.Equal(expectedBits, RunsToBits(runs));
    }

    [Fact]
    public void UpcE_bits_numberSystemZero_checkDigitNine_matchesStandardParityRow()
    {
        // "567894" (last digit 4, canonical "05678949" -- see the hand-derived check-digit
        // workup in UpcE_sixDigits_lastDigitThreeFourOrFiveToNine_expandsToHandDerivedCanonicalDigits)
        // has check digit 9. The standard GS1 UPC-E parity table gives check-digit-9 parity as
        // EOOEOE (E=even=G, O=odd=L) -> GLLGLG, hardcoded literally so a typo in
        // EanTables.UpcESystem0Parity's check-digit-9 row would be caught.
        var barcode = new EanBarcode(EanSymbology.UpcE, "567894");
        var expectedBits =
            "101" // start guard
            + EanTables.G[5] + EanTables.L[6] + EanTables.L[7] + EanTables.G[8] + EanTables.L[9] + EanTables.G[4]
            + "010101"; // special 6-module end guard (no middle guard)

        Assert.Equal(51, expectedBits.Length);

        var runs = EanEncoder.Encode(barcode).Runs;
        Assert.Equal(expectedBits, RunsToBits(runs));
    }

    [Fact]
    public void UpcE_hriGroups_hasOutsideLeft_oneBelowGroupOfSixDigits_andOutsideRight()
    {
        var encoded = EanEncoder.Encode(new EanBarcode(EanSymbology.UpcE, "654321"));
        Assert.Equal(3, encoded.HriGroups.Count);
        Assert.Equal(HriAnchor.OutsideLeft, encoded.HriGroups[0].Anchor);
        Assert.Equal("0", encoded.HriGroups[0].Text);
        Assert.Equal(HriAnchor.Below, encoded.HriGroups[1].Anchor);
        Assert.Equal("654321", encoded.HriGroups[1].Text);
        Assert.Equal(HriAnchor.OutsideRight, encoded.HriGroups[2].Anchor);
        Assert.Equal("7", encoded.HriGroups[2].Text);
    }

    [Fact]
    public void UpcE_encoding_isDeterministic()
    {
        var a = EanEncoder.Encode(new EanBarcode(EanSymbology.UpcE, "654321"));
        var b = EanEncoder.Encode(new EanBarcode(EanSymbology.UpcE, "654321"));
        Assert.Equal(a.Runs, b.Runs);
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
