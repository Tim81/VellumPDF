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
