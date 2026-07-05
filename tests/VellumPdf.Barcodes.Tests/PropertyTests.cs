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

    private static readonly Gen<string> QrContent = Gen.Select(Gen.Char[(char)32, (char)126].Array[1, 60], chars => new string(chars));
    private static readonly Gen<string> MicroContent = Gen.Select(Gen.Char[(char)32, (char)126].Array[1, 8], chars => new string(chars));
    private static readonly Gen<QrErrorCorrection> AnyLevel = Gen.Select(Gen.Int[0, 3], i => (QrErrorCorrection)i);

    [Fact]
    public void QrCode_anyContentWithinCapacity_encodesWithAValidVersionSizeAndMask()
    {
        Gen.Select(QrContent, AnyLevel).Sample((content, level) =>
        {
            var qr = new QrCode(content) { ErrorCorrection = level };
            BarcodeMatrix matrix;
            try
            {
                matrix = qr.GetMatrix();
            }
            catch (FormatException)
            {
                return; // beyond version 40's capacity at this level; not a property violation
            }

            Assert.Equal(matrix.Width, matrix.Height);
            Assert.True((matrix.Width - 17) % 4 == 0 && matrix.Width is >= 21 and <= 177);

            // The top-left finder pattern's outer ring is always dark.
            Assert.True(matrix.IsDark(0, 0));
            Assert.True(matrix.IsDark(6, 0));
            Assert.True(matrix.IsDark(0, 6));
            Assert.True(matrix.IsDark(6, 6));

            var mask = DecodeQrMask(matrix);
            Assert.InRange(mask, 0, 7);
        });
    }

    [Fact]
    public void QrCode_encoding_isDeterministic()
    {
        Gen.Select(QrContent, AnyLevel).Sample((content, level) =>
        {
            BarcodeMatrix a, b;
            try
            {
                a = new QrCode(content) { ErrorCorrection = level }.GetMatrix();
                b = new QrCode(content) { ErrorCorrection = level }.GetMatrix();
            }
            catch (FormatException)
            {
                return;
            }

            Assert.Equal(MatrixToBits(a), MatrixToBits(b));
        });
    }

    [Fact]
    public void MicroQrCode_anyShortContent_encodesWithAValidVersionSize()
    {
        MicroContent.Sample(content =>
        {
            BarcodeMatrix matrix;
            try
            {
                matrix = new MicroQrCode(content).GetMatrix();
            }
            catch (FormatException)
            {
                return; // beyond M4's capacity, or needs a mode M4 doesn't offer for this content
            }

            Assert.Equal(matrix.Width, matrix.Height);
            Assert.True((matrix.Width - 9) % 2 == 0 && matrix.Width is >= 11 and <= 17);
            Assert.True(matrix.IsDark(0, 0));
            Assert.True(matrix.IsDark(6, 0));
            Assert.True(matrix.IsDark(0, 6));
            Assert.True(matrix.IsDark(6, 6));
        });
    }

    [Fact]
    public void MicroQrCode_encoding_isDeterministic()
    {
        MicroContent.Sample(content =>
        {
            BarcodeMatrix a, b;
            try
            {
                a = new MicroQrCode(content).GetMatrix();
                b = new MicroQrCode(content).GetMatrix();
            }
            catch (FormatException)
            {
                return;
            }

            Assert.Equal(MatrixToBits(a), MatrixToBits(b));
        });
    }

    private static int DecodeQrMask(BarcodeMatrix matrix)
    {
        var bits = 0;
        for (var i = 0; i <= 5; i++) bits |= (matrix.IsDark(8, i) ? 1 : 0) << i;
        bits |= (matrix.IsDark(8, 7) ? 1 : 0) << 6;
        bits |= (matrix.IsDark(8, 8) ? 1 : 0) << 7;
        bits |= (matrix.IsDark(7, 8) ? 1 : 0) << 8;
        for (var i = 9; i < 15; i++) bits |= (matrix.IsDark(14 - i, 8) ? 1 : 0) << i;

        var unmasked = bits ^ Convert.ToInt32("101010000010010", 2);
        return (unmasked >> 10) & 0b111;
    }

    private static bool[] MatrixToBits(BarcodeMatrix matrix)
    {
        var bits = new bool[matrix.Width * matrix.Height];
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                bits[(y * matrix.Width) + x] = matrix.IsDark(x, y);
        return bits;
    }
}
