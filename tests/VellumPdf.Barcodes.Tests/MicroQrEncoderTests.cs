// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Barcodes.Internal;
using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// End-to-end tests for <see cref="MicroQrCode"/>/<see cref="MicroQrEncoder"/> against the
/// ISO/IEC 18004:2015 Annex I worked example (encoding "01234567" as a version M2-L symbol) and
/// the per-version mode/error-correction restrictions of Table 7/Table 13.
/// </summary>
public sealed class MicroQrEncoderTests
{
    private static readonly int[] AnnexIDataCodewords = [64, 24, 172, 195, 0];
    private static readonly int[] AnnexIEcCodewords = [134, 13, 34, 174, 48];

    // Independently verified M1-L "12345" vector: ISO/IEC 18004 Annex I only works a Micro QR
    // example at M2-L, so there is no official M1 figure to transcribe. These codewords were
    // hand-derived from the sub-clause 7.4/Table 2/Table 3 rules (see
    // DataCodewords_m1TwelveThreeFourFive_keepsTheHalfWidthCodewordByteAligned), the
    // error-correction codewords were cross-checked against a from-scratch GF(256) Reed-Solomon
    // implementation, and the resulting matrix (see
    // GetMatrix_m1TwelveThreeFourFive_matchesIndependentlyVerifiedModuleGrid) was cross-checked
    // module-for-module against segno 1.6.6 (a third-party, spec-compliant Python Micro QR
    // encoder) and confirmed to decode as "12345" via zxing-cpp 3.0.0.
    private static readonly int[] M1TwelveThreeFourFiveDataCodewords = [163, 218, 208];
    private static readonly int[] M1TwelveThreeFourFiveEcCodewords = [110, 199];

    [Fact]
    public void BitStream_annexIExample_matchesModeCountAndData()
    {
        // ISO/IEC 18004 Annex I, Step 1: mode(0) + count(1000) + numeric data + terminator(00000).
        const string expectedBits = "0" + "1000" + "0000001100" + "0101011001" + "1000011" + "00000";

        var writer = new BitWriter();
        var segments = new[] { new QrSegment(QrSegmentMode.Numeric, 0, 8, 8) };
        QrBitStreamBuilder.WriteSegments(
            writer,
            "01234567",
            segments,
            mode => (QrTables.MicroModeIndicator(2, mode), QrTables.MicroModeIndicatorBits(2)),
            mode => QrTables.MicroCharacterCountBits(2, mode),
            Encoding.Latin1);

        Assert.Equal(32, writer.BitCount); // 1 (mode) + 4 (count) + 27 (data) bits, before the terminator

        var dataCodewords = QrBitStreamBuilder.Finish(writer, dataCodewordCount: 5, QrTables.MicroTerminatorBits(2), lastCodewordIsHalfWidth: false);
        Assert.Equal(AnnexIDataCodewords, dataCodewords.Select(b => (int)b));

        var bytes = dataCodewords;
        var actualBits = new StringBuilder();
        for (var i = 0; i < expectedBits.Length; i++)
            actualBits.Append((bytes[i / 8] >> (7 - (i % 8))) & 1);
        Assert.Equal(expectedBits, actualBits.ToString());
    }

    [Fact]
    public void EcCodewords_annexIExample_matchesReedSolomonOfTheDataCodewords()
    {
        var data = AnnexIDataCodewords.Select(v => (byte)v).ToArray();
        var ec = ReedSolomonGf256.ComputeRemainder(data, 5);
        Assert.Equal(AnnexIEcCodewords, ec.Select(b => (int)b));
    }

    [Fact]
    public void GetMatrix_annexIExample_isThirteenByThirteen()
    {
        var micro = new MicroQrCode("01234567") { ErrorCorrection = QrErrorCorrection.L, Version = 2 };
        var matrix = micro.GetMatrix();
        Assert.Equal(13, matrix.Width);
        Assert.Equal(13, matrix.Height);
    }

    [Fact]
    public void GetMatrix_annexIExample_producesTheAnnexIFormatInfo()
    {
        // Annex I selects mask reference 01 (condition index 4) for symbol number 1 (M2-L):
        // masked format information 101000010011001.
        var micro = new MicroQrCode("01234567") { ErrorCorrection = QrErrorCorrection.L, Version = 2 };
        var matrix = micro.GetMatrix();

        var formatBits = ReadMicroFormatInfo(matrix);
        Assert.Equal(Convert.ToInt32("101000010011001", 2), formatBits);
    }

    [Fact]
    public void GetMatrix_annexIExample_finderCornerIsDark()
    {
        var micro = new MicroQrCode("01234567") { Version = 2 };
        var matrix = micro.GetMatrix();
        Assert.True(matrix.IsDark(0, 0));
        Assert.True(matrix.IsDark(6, 0));
        Assert.True(matrix.IsDark(0, 6));
        Assert.True(matrix.IsDark(6, 6));
    }

    [Fact]
    public void GetMatrix_annexIExample_matchesTheFigureI4ModuleGrid()
    {
        // ISO/IEC 18004:2015 Annex I, Figure I.4 ("Final version M2-L symbol encoding 01234567"),
        // transcribed by extracting the figure's embedded image from the PDF, thresholding it
        // into a 13x13 module grid (sampling each module's centre pixel), and confirming with
        // zxing-cpp 3.0.0 that the figure itself decodes to "01234567" before transcribing it
        // here. This is a stronger check than the codeword tests above: it validates the finder,
        // separator, timing pattern, format information and masked data region all at once,
        // directly against the standard's own figure rather than against intermediate values this
        // same production code also computed.
        var micro = new MicroQrCode("01234567") { ErrorCorrection = QrErrorCorrection.L, Version = 2 };
        var matrix = micro.GetMatrix();

        string[] expected =
        [
            "1111111010101",
            "1000001011101",
            "1011101001101",
            "1011101001111",
            "1011101011100",
            "1000001010001",
            "1111111001111",
            "0000000001100",
            "1101000010001",
            "0110101010101",
            "1110011111110",
            "0001010000110",
            "1110100110111",
        ];

        AssertMatrixEquals(expected, matrix);
    }

    [Fact]
    public void DataCodewords_m1TwelveThreeFourFive_keepsTheHalfWidthCodewordByteAligned()
    {
        // "12345" at M1 (numeric only, no mode indicator, 3-bit count): mode() + count(101) +
        // "123"->0001111011 (10 bits) + "45"->0101101 (7 bits) = 20 bits, which exactly fills
        // M1-L's 20-bit capacity (3 codewords x 8 bits, minus 4 for the half-width last one), so
        // the terminator is shortened to nothing. Splitting into codewords: byte0=10100011=0xA3,
        // byte1=11011010=0xDA, and the remaining 4 bits "1101" left exactly as BitWriter.ToArray()
        // produces them for a partial final byte: shifted into the high nibble, zero-padded in the
        // low nibble, i.e. 0xD0 (208) rather than shifted down to a compact 0x0D (13). This
        // matters because the next codeword (below) is Reed-Solomon over this exact byte value.
        var writer = new BitWriter();
        var segments = new[] { new QrSegment(QrSegmentMode.Numeric, 0, 5, 5) };
        QrBitStreamBuilder.WriteSegments(
            writer,
            "12345",
            segments,
            mode => (QrTables.MicroModeIndicator(1, mode), QrTables.MicroModeIndicatorBits(1)),
            mode => QrTables.MicroCharacterCountBits(1, mode),
            Encoding.Latin1);

        Assert.Equal(20, writer.BitCount);

        var dataCodewords = QrBitStreamBuilder.Finish(writer, dataCodewordCount: 3, QrTables.MicroTerminatorBits(1), lastCodewordIsHalfWidth: true);
        Assert.Equal(M1TwelveThreeFourFiveDataCodewords, dataCodewords.Select(b => (int)b));
    }

    [Fact]
    public void EcCodewords_m1TwelveThreeFourFive_matchesReedSolomonOfTheByteAlignedDataCodewords()
    {
        var data = M1TwelveThreeFourFiveDataCodewords.Select(v => (byte)v).ToArray();
        var ec = ReedSolomonGf256.ComputeRemainder(data, 2);
        Assert.Equal(M1TwelveThreeFourFiveEcCodewords, ec.Select(b => (int)b));
    }

    [Fact]
    public void GetMatrix_m1TwelveThreeFourFive_matchesIndependentlyVerifiedModuleGrid()
    {
        // Regression for a decode failure an end-to-end zxing-cpp smoke test found on the M1
        // golden barcode: two encoder bugs, both specific to versions M1/M3, combined to corrupt
        // every codeword from the second column-pair onwards. First, the half-width final data
        // codeword was shifted down to a compact 0-15 value before Reed-Solomon saw it (see
        // DataCodewords_m1TwelveThreeFourFive_keepsTheHalfWidthCodewordByteAligned), so the
        // computed error-correction codewords did not match what a decoder reconstructs. Second,
        // the zig-zag placement scan's up/down alternation was derived from the column index
        // itself (correct only when the symbol's side length mod 4 == 1) rather than from the
        // column-pair's position in the scan sequence — every full-size QR side length happens to
        // satisfy that mod-4 condition, and so do Micro QR's M2 (13) and M4 (17), but not M1 (11)
        // or M3 (15), so the bug was invisible until a non-M2/M4 Micro QR symbol was decoded.
        var micro = new MicroQrCode("12345") { Version = 1 };
        var matrix = micro.GetMatrix();

        string[] expected =
        [
            "11111110101",
            "10000010110",
            "10111010100",
            "10111010000",
            "10111010111",
            "10000010011",
            "11111110100",
            "00000000011",
            "11001110011",
            "01010001100",
            "11110000011",
        ];

        AssertMatrixEquals(expected, matrix);
    }

    private static void AssertMatrixEquals(string[] expected, BarcodeMatrix matrix)
    {
        Assert.Equal(expected.Length, matrix.Width);
        Assert.Equal(expected.Length, matrix.Height);
        for (var row = 0; row < expected.Length; row++)
            for (var col = 0; col < expected.Length; col++)
                Assert.True((expected[row][col] == '1') == matrix.IsDark(col, row), $"Mismatch at row {row}, column {col}.");
    }

    [Fact]
    public void M1_alphanumericContent_throwsFormatException()
    {
        var micro = new MicroQrCode("AB") { Version = 1 };
        Assert.Throws<FormatException>(() => micro.GetMatrix());
    }

    [Fact]
    public void M1_numericContent_succeeds()
    {
        var micro = new MicroQrCode("123") { Version = 1 };
        Assert.NotNull(micro.GetMatrix());
        Assert.Equal(11, micro.GetMatrix().Width);
    }

    [Fact]
    public void M2_byteOnlyContent_throwsFormatException()
    {
        var micro = new MicroQrCode("ab") { Version = 2 };
        Assert.Throws<FormatException>(() => micro.GetMatrix());
    }

    [Fact]
    public void M2_alphanumericContent_succeeds()
    {
        var micro = new MicroQrCode("AB12") { Version = 2 };
        Assert.NotNull(micro.GetMatrix());
    }

    [Fact]
    public void M3_byteContent_succeeds()
    {
        var micro = new MicroQrCode("ab") { Version = 3 };
        Assert.NotNull(micro.GetMatrix());
    }

    [Fact]
    public void ErrorCorrectionQ_forcedOnVersionTwo_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new MicroQrCode("A") { Version = 2, ErrorCorrection = QrErrorCorrection.Q }.GetMatrix());

    [Fact]
    public void ErrorCorrectionQ_forcedOnVersionThree_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new MicroQrCode("A") { Version = 3, ErrorCorrection = QrErrorCorrection.Q }.GetMatrix());

    [Fact]
    public void ErrorCorrectionQ_forcedOnVersionFour_succeeds()
    {
        var micro = new MicroQrCode("A") { Version = 4, ErrorCorrection = QrErrorCorrection.Q };
        Assert.NotNull(micro.GetMatrix());
    }

    [Fact]
    public void ErrorCorrectionH_throwsForEveryVersion()
    {
        foreach (var version in new[] { 2, 3, 4 })
            Assert.Throws<ArgumentException>(() => new MicroQrCode("A") { Version = version, ErrorCorrection = QrErrorCorrection.H }.GetMatrix());
    }

    [Fact]
    public void Version_outOfRange_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new MicroQrCode("A") { Version = 5 }.GetMatrix());

    [Fact]
    public void NonLatin1Content_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new MicroQrCode("😀").GetMatrix());

    [Fact]
    public void AutoVersion_shortNumericContent_choosesM1()
    {
        var micro = new MicroQrCode("1");
        Assert.Equal(11, micro.GetMatrix().Width);
    }

    [Fact]
    public void AutoVersion_alphanumericContent_choosesAtLeastM2()
    {
        var micro = new MicroQrCode("AB");
        Assert.True(micro.GetMatrix().Width >= 13);
    }

    [Fact]
    public void AutoVersion_requestingLevelM_skipsM1EvenForNumericContent()
    {
        var micro = new MicroQrCode("1") { ErrorCorrection = QrErrorCorrection.M };
        Assert.True(micro.GetMatrix().Width >= 13);
    }

    [Fact]
    public void GetMatrix_calledTwice_returnsTheSameCachedInstance()
    {
        var micro = new MicroQrCode("cache");
        Assert.Same(micro.GetMatrix(), micro.GetMatrix());
    }

    private static int ReadMicroFormatInfo(BarcodeMatrix matrix)
    {
        var bits = 0;
        for (var i = 0; i <= 6; i++) bits |= BitAt(matrix, 8, i + 1) << i;
        bits |= BitAt(matrix, 8, 8) << 7;
        for (var i = 8; i <= 14; i++) bits |= BitAt(matrix, 15 - i, 8) << i;
        return bits;
    }

    private static int BitAt(BarcodeMatrix matrix, int x, int y) => matrix.IsDark(x, y) ? 1 : 0;
}
