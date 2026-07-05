// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using CsCheck;
using VellumPdf.Barcodes.Code128;
using VellumPdf.Barcodes.EanUpc;
using VellumPdf.Barcodes.Itf;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Property-based invariants (CsCheck) for the 1D encoders.</summary>
public sealed class PropertyTests
{
    private static readonly Gen<string> TwelveDigits = Gen.Select(Gen.Char['0', '9'].Array[12], chars => new string(chars));
    private static readonly Gen<string> ThirteenDigits = Gen.Select(Gen.Char['0', '9'].Array[13], chars => new string(chars));
    private static readonly Gen<string> Ascii = Gen.Select(Gen.Char[(char)32, (char)126].Array[0, 20], chars => new string(chars));

    [Fact]
    public void EanCheckDigit_isAlwaysADigit_andRoundTripsThroughTheConstructor()
    {
        TwelveDigits.Sample(data =>
        {
            var check = EanEncoder.ComputeCheckDigit(data);
            Assert.InRange(check, 0, 9);

            var full = data + (char)('0' + check);
            var fromData = new EanBarcode(EanSymbology.Ean13, data);
            var fromFull = new EanBarcode(EanSymbology.Ean13, full);
            Assert.Equal(fromData.Digits, fromFull.Digits);

            var wrongCheck = (check + 1) % 10;
            var wrongFull = data + (char)('0' + wrongCheck);
            Assert.Throws<FormatException>(() => new EanBarcode(EanSymbology.Ean13, wrongFull));
        });
    }

    [Fact]
    public void ItfCheckDigit_isAlwaysADigit_andRoundTripsThroughValidation()
    {
        ThirteenDigits.Sample(data =>
        {
            var full = Itf14Encoder.NormalizeAndValidate(data);
            Assert.Equal(14, full.Length);
            Assert.StartsWith(data, full, StringComparison.Ordinal);

            var again = Itf14Encoder.NormalizeAndValidate(full);
            Assert.Equal(full, again);
        });
    }

    [Fact]
    public void Code128_totalModules_matchesElevenPerCodewordPlusThirteenFormula()
    {
        Ascii.Sample(content =>
        {
            var barcode = new Code128Barcode(content);
            var (_, dataSymbols, _) = Code128Encoder.EncodeSymbols(barcode);
            var codewordCount = 1 + dataSymbols.Count + 1; // start + data symbols + check (stop is the flat +13)
            var expectedModulesExcludingQuietZones = (11 * codewordCount) + 13;

            var encoded = Code128Encoder.Encode(barcode);
            var actualModules = encoded.Runs.Sum();
            Assert.Equal(expectedModulesExcludingQuietZones, actualModules, 6);

            var withQuietZones = encoded.TotalModuleWidth;
            Assert.Equal(
                expectedModulesExcludingQuietZones + encoded.QuietZoneLeft + encoded.QuietZoneRight,
                withQuietZones,
                6);
        });
    }

    [Fact]
    public void Code128_encoding_isDeterministic()
    {
        Ascii.Sample(content =>
        {
            var a = Code128Encoder.Encode(new Code128Barcode(content));
            var b = Code128Encoder.Encode(new Code128Barcode(content));
            Assert.Equal(a.Runs, b.Runs);
        });
    }

    [Fact]
    public void Ean13_encoding_isDeterministic()
    {
        TwelveDigits.Sample(data =>
        {
            var a = EanEncoder.Encode(new EanBarcode(EanSymbology.Ean13, data));
            var b = EanEncoder.Encode(new EanBarcode(EanSymbology.Ean13, data));
            Assert.Equal(a.Runs, b.Runs);
        });
    }

    [Fact]
    public void Itf14_encoding_isDeterministic()
    {
        ThirteenDigits.Sample(data =>
        {
            var a = Itf14Encoder.Encode(new Itf14Barcode(data));
            var b = Itf14Encoder.Encode(new Itf14Barcode(data));
            Assert.Equal(a.Runs, b.Runs);
        });
    }
}
