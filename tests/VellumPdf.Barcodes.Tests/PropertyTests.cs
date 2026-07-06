// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using CsCheck;
using VellumPdf.Barcodes.Code128;
using VellumPdf.Barcodes.Code39;
using VellumPdf.Barcodes.EanUpc;
using VellumPdf.Barcodes.Internal;
using VellumPdf.Barcodes.Itf;
using VellumPdf.Barcodes.Pdf417;

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

    public static TheoryData<string> BinaryFieldNames => new() { "Gf16", "Gf64", "Gf256", "Gf1024", "Gf4096" };

    private static GaloisField ResolveBinaryField(string name) => name switch
    {
        "Gf16" => GaloisField.Gf16,
        "Gf64" => GaloisField.Gf64,
        "Gf256" => GaloisField.Gf256,
        "Gf1024" => GaloisField.Gf1024,
        "Gf4096" => GaloisField.Gf4096,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown field."),
    };

    // A systematic Reed-Solomon codeword (data followed by its check symbols) is always a
    // multiple of the generator polynomial, so it must evaluate to zero at every one of the
    // generator's roots — regardless of field, first root, data, or error-correction count. This
    // is the same defining property ReedSolomonBinaryTests' pinned vectors are cross-checked
    // against, exercised here over random data and error-correction counts for each of the five
    // field sizes this package uses.
    [Theory]
    [MemberData(nameof(BinaryFieldNames))]
    public void ReedSolomonBinary_anyDataAndErrorCorrectionCount_codewordVanishesAtGeneratorRoots(string fieldName)
    {
        var field = ResolveBinaryField(fieldName);
        const int firstRoot = 1;
        var reedSolomon = new ReedSolomonBinary(field, firstRoot);

        var dataGen = Gen.Int[0, field.Size - 1].Array[1, 20];
        var errorCorrectionCountGen = Gen.Int[1, field.Size - 2]; // [1, Size - 1)

        Gen.Select(dataGen, errorCorrectionCountGen).Sample((data, errorCorrectionCount) =>
        {
            var remainder = reedSolomon.ComputeRemainder(data, errorCorrectionCount);

            var codeword = new int[data.Length + remainder.Length];
            data.CopyTo(codeword, 0);
            remainder.CopyTo(codeword, data.Length);

            for (var i = 0; i < remainder.Length; i++)
            {
                var root = field.Exp(firstRoot + i);
                var acc = 0;
                foreach (var coefficient in codeword)
                    acc = field.Multiply(acc, root) ^ coefficient;
                Assert.Equal(0, acc);
            }
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

    private static readonly Gen<char> Code39StandardChar = Gen.Select(Gen.Int[0, Code39Tables.Characters.Length - 1], i => Code39Tables.Characters[i]);
    private static readonly Gen<string> Code39StandardContent = Gen.Select(Code39StandardChar.Array[0, 20], chars => new string(chars));
    private static readonly Gen<string> Code39AsciiContent = Gen.Select(Gen.Char[(char)0, (char)127].Array[0, 20], chars => new string(chars));
    private static readonly Gen<string> SixDigits = Gen.Select(Gen.Char['0', '9'].Array[6], chars => new string(chars));

    [Fact]
    public void Code39_standardContent_encoding_isDeterministic()
    {
        Code39StandardContent.Sample(content =>
        {
            var a = Code39Encoder.Encode(new Code39Barcode(content));
            var b = Code39Encoder.Encode(new Code39Barcode(content));
            Assert.Equal(a.Runs, b.Runs);
        });
    }

    [Fact]
    public void Code39_standardContent_totalModules_matchesNineTimesSymbolsPlusGapsFormula()
    {
        Code39StandardContent.Sample(content =>
        {
            var encoded = Code39Encoder.Encode(new Code39Barcode(content));
            var symbolCount = content.Length + 2; // + start + stop
            var expectedModules = (9 * symbolCount) + (symbolCount - 1); // one narrow gap between each pair
            Assert.Equal(expectedModules, encoded.Runs.Count);

            // Every run is either the narrow-module gap (1) or a data element (1 = narrow, ratio = wide).
            Assert.All(encoded.Runs, r => Assert.True(r == 1 || r == 2.5));
        });
    }

    [Fact]
    public void Code39_fullAsciiContent_anyAsciiEncodesWithoutThrowing_andIsDeterministic()
    {
        Code39AsciiContent.Sample(content =>
        {
            var a = Code39Encoder.Encode(new Code39Barcode(content) { FullAscii = true });
            var b = Code39Encoder.Encode(new Code39Barcode(content) { FullAscii = true });
            Assert.Equal(a.Runs, b.Runs);

            // Every run is a valid module width for the default WideNarrowRatio (2.5), and the
            // HRI always shows the original, un-expanded content.
            Assert.All(a.Runs, r => Assert.True(r == 1 || r == 2.5));
            Assert.Equal(content, Assert.Single(a.HriGroups).Text);
        });
    }

    [Fact]
    public void UpcE_sixDigits_alwaysNormalizesToAnEightDigitCanonicalForm_stableUnderRevalidation()
    {
        SixDigits.Sample(six =>
        {
            var barcode = new EanBarcode(EanSymbology.UpcE, six);
            Assert.Equal(8, barcode.Digits.Length);
            Assert.True(barcode.Digits[0] is '0'); // six-digit input defaults to number system 0

            // Re-validating the canonical 8-digit form (number system + six digits + check digit)
            // must reproduce itself exactly -- the check digit is stable under revalidation.
            var again = new EanBarcode(EanSymbology.UpcE, barcode.Digits);
            Assert.Equal(barcode.Digits, again.Digits);
        });
    }

    [Fact]
    public void UpcE_encoding_isDeterministic_forAnySixDigits()
    {
        SixDigits.Sample(six =>
        {
            var a = EanEncoder.Encode(new EanBarcode(EanSymbology.UpcE, six));
            var b = EanEncoder.Encode(new EanBarcode(EanSymbology.UpcE, six));
            Assert.Equal(a.Runs, b.Runs);

            // 51 modules total: 3 (start guard) + 6*7 (digits) + 6 (special end guard).
            Assert.Equal(51, a.Runs.Sum());
        });
    }

    private static readonly Gen<byte[]> Pdf417ByteContent = Gen.Byte.Array[1, 60];

    [Fact]
    public void Pdf417Barcode_anyByteContentWithinCapacity_encodesDeterministicallyWithAValidWidth()
    {
        Pdf417ByteContent.Sample(content =>
        {
            BarcodeMatrix a, b;
            try
            {
                a = new Pdf417Barcode(content).GetMatrix();
                b = new Pdf417Barcode(content).GetMatrix();
            }
            catch (FormatException)
            {
                return; // beyond PDF417's maximum capacity; not a property violation
            }

            Assert.InRange(a.Height, Pdf417Dimensions.MinRows, Pdf417Dimensions.MaxRows);

            var columns = (a.Width - (Pdf417Tables.PatternModules * 3) - Pdf417Tables.StopPatternModules) / Pdf417Tables.PatternModules;
            Assert.Equal(a.Width, Pdf417Dimensions.WidthModules(columns));
            Assert.InRange(columns, Pdf417Dimensions.MinColumns, Pdf417Dimensions.MaxColumns);

            for (var row = 0; row < a.Height; row++)
            {
                Assert.True(RowStartsAndEndsWithStartStopPatterns(a, row));
                Assert.True(RowStartsAndEndsWithStartStopPatterns(b, row));
            }

            Assert.Equal(MatrixToBits(a), MatrixToBits(b));
        });
    }

    private static bool RowStartsAndEndsWithStartStopPatterns(BarcodeMatrix matrix, int row)
    {
        var start = 0u;
        for (var m = 0; m < Pdf417Tables.PatternModules; m++) start = (start << 1) | (matrix.IsDark(m, row) ? 1u : 0u);

        var stop = 0u;
        var stopStart = matrix.Width - Pdf417Tables.StopPatternModules;
        for (var m = 0; m < Pdf417Tables.StopPatternModules; m++) stop = (stop << 1) | (matrix.IsDark(stopStart + m, row) ? 1u : 0u);

        return start == Pdf417Tables.StartPattern && stop == Pdf417Tables.StopPattern;
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
