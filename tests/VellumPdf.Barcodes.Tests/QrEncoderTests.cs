// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Barcodes.Internal;
using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// End-to-end tests for <see cref="QrCode"/>/<see cref="QrEncoder"/> against the ISO/IEC
/// 18004:2015 Annex I worked example (encoding "01234567" as a version 1-M symbol) and the
/// data/error-correction codewords Thonky's tutorial publishes for "HELLO WORLD" at 1-M.
/// </summary>
public sealed class QrEncoderTests
{
    // ISO/IEC 18004 Annex I, Step 1: mode(0001) + count(0000001000) + numeric data, terminated.
    private const string AnnexIBitStream = "0001" + "0000001000" + "0000001100" + "0101011001" + "1000011";

    private static readonly int[] AnnexIDataCodewords =
        [16, 32, 12, 86, 97, 128, 236, 17, 236, 17, 236, 17, 236, 17, 236, 17];

    private static readonly int[] AnnexIEcCodewords =
        [165, 36, 212, 193, 237, 54, 199, 135, 44, 85];

    [Fact]
    public void BitStream_annexIExample_matchesModeCountAndData()
    {
        var writer = new BitWriter();
        var segments = new[] { new QrSegment(QrSegmentMode.Numeric, 0, 8, 8) };
        QrBitStreamBuilder.WriteSegments(
            writer,
            "01234567",
            segments,
            mode => (QrTables.ModeIndicator(mode), QrTables.ModeIndicatorBits),
            mode => QrTables.CharacterCountBits(1, mode),
            Encoding.Latin1);

        Assert.Equal(AnnexIBitStream.Length, writer.BitCount);

        var bytes = writer.ToArray();
        var actualBits = new StringBuilder();
        for (var i = 0; i < AnnexIBitStream.Length; i++)
            actualBits.Append((bytes[i / 8] >> (7 - (i % 8))) & 1);
        Assert.Equal(AnnexIBitStream, actualBits.ToString());
    }

    [Fact]
    public void DataCodewords_annexIExample_matchesModeCountDataTerminatorAndPads()
    {
        var writer = new BitWriter();
        var segments = new[] { new QrSegment(QrSegmentMode.Numeric, 0, 8, 8) };
        QrBitStreamBuilder.WriteSegments(
            writer,
            "01234567",
            segments,
            mode => (QrTables.ModeIndicator(mode), QrTables.ModeIndicatorBits),
            mode => QrTables.CharacterCountBits(1, mode),
            Encoding.Latin1);

        var dataCodewords = QrBitStreamBuilder.Finish(writer, dataCodewordCount: 16, QrTables.TerminatorBits, lastCodewordIsHalfWidth: false);

        Assert.Equal(AnnexIDataCodewords, dataCodewords.Select(b => (int)b));
    }

    [Fact]
    public void EcCodewords_annexIExample_matchesReedSolomonOfTheDataCodewords()
    {
        var data = AnnexIDataCodewords.Select(v => (byte)v).ToArray();
        var ec = ReedSolomonGf256.ComputeRemainder(data, 10);
        Assert.Equal(AnnexIEcCodewords, ec.Select(b => (int)b));
    }

    [Fact]
    public void GetMatrix_annexIExample_isTwentyOneByTwentyOne()
    {
        var qr = new QrCode("01234567") { ErrorCorrection = QrErrorCorrection.M, Version = 1 };
        var matrix = qr.GetMatrix();
        Assert.Equal(21, matrix.Width);
        Assert.Equal(21, matrix.Height);
    }

    [Fact]
    public void GetMatrix_annexIExample_forcedMaskTwo_producesTheAnnexIFormatInfo()
    {
        // ISO Annex I, Step 5: level M, mask 010 -> masked format information 101111001111100.
        // Forcing the mask (rather than relying on auto-selection, which scores penalties across
        // the whole symbol and is not guaranteed to reproduce one specific historical example's
        // choice — see GetMatrix_autoSelectedMask_formatInfoRoundTripsThroughTheChosenMask below)
        // isolates the format-info BCH/XOR pipeline against the annex's own numbers.
        var qr = new QrCode("01234567") { ErrorCorrection = QrErrorCorrection.M, Version = 1, Mask = 2 };
        var matrix = qr.GetMatrix();

        var formatBits = ReadQrFormatInfo(matrix, 21);
        Assert.Equal(Convert.ToInt32("101111001111100", 2), formatBits);
    }

    [Fact]
    public void GetMatrix_autoSelectedMask_formatInfoRoundTripsThroughTheChosenMask()
    {
        // Whichever mask auto-selection picks, the format information written into the symbol
        // must decode back to that same mask and error-correction level.
        var qr = new QrCode("01234567") { ErrorCorrection = QrErrorCorrection.M, Version = 1 };
        var matrix = qr.GetMatrix();

        var formatBits = ReadQrFormatInfo(matrix, 21);
        var decodedMask = DecodeMaskFromFormatBits(formatBits);
        Assert.InRange(decodedMask, 0, 7);
        Assert.Equal(formatBits, QrFormatVersionInfo.ComputeQrFormatBits(QrErrorCorrection.M, decodedMask));
    }

    [Fact]
    public void GetMatrix_annexIExample_dataAndEcCodewordsRoundTripThroughMaskTwoAndThePlacementOrder()
    {
        // Forcing mask 2 (as Annex I specifies) and reading the encoding region back in the same
        // zig-zag order used to place it, then undoing the mask, must reproduce the exact Annex I
        // codeword sequence (16,32,12,86,97,128,236,17,... + the 10 EC codewords). This validates
        // the placement order and masking against the spec's own numbers without needing to
        // transcribe the (graphical, not text-extractable) figure.
        var qr = new QrCode("01234567") { ErrorCorrection = QrErrorCorrection.M, Version = 1, Mask = 2 };
        var matrix = qr.GetMatrix();

        var (_, isFunction) = QrMatrixBuilder.BuildFunctionPatterns(1);
        var bits = ReadDataBitsInPlacementOrder(matrix, isFunction, 21, mask: 2, totalBits: 26 * 8);

        var codewords = new int[26];
        for (var i = 0; i < 26; i++)
        {
            var value = 0;
            for (var b = 0; b < 8; b++) value = (value << 1) | (bits[(i * 8) + b] ? 1 : 0);
            codewords[i] = value;
        }

        Assert.Equal(AnnexIDataCodewords.Concat(AnnexIEcCodewords), codewords);
    }

    [Fact]
    public void GetMatrix_annexIExample_threeFinderCornersAreDark()
    {
        var qr = new QrCode("01234567") { ErrorCorrection = QrErrorCorrection.M, Version = 1 };
        var matrix = qr.GetMatrix();

        Assert.True(matrix.IsDark(0, 0));
        Assert.True(matrix.IsDark(6, 0));
        Assert.True(matrix.IsDark(0, 6));
        Assert.True(matrix.IsDark(14, 0));
        Assert.True(matrix.IsDark(20, 0));
        Assert.True(matrix.IsDark(0, 14));
        Assert.True(matrix.IsDark(0, 20));
    }

    [Fact]
    public void DataCodewords_helloWorld1M_matchesThonkyVector()
    {
        var writer = new BitWriter();
        var segments = new[] { new QrSegment(QrSegmentMode.Alphanumeric, 0, 11, 11) };
        QrBitStreamBuilder.WriteSegments(
            writer,
            "HELLO WORLD",
            segments,
            mode => (QrTables.ModeIndicator(mode), QrTables.ModeIndicatorBits),
            mode => QrTables.CharacterCountBits(1, mode),
            Encoding.Latin1);

        var dataCodewords = QrBitStreamBuilder.Finish(writer, dataCodewordCount: 16, QrTables.TerminatorBits, lastCodewordIsHalfWidth: false);

        int[] expected = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
        Assert.Equal(expected, dataCodewords.Select(b => (int)b));
    }

    [Fact]
    public void EcCodewords_helloWorld1M_matchesThonkyVector()
    {
        int[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
        var ec = ReedSolomonGf256.ComputeRemainder(data.Select(v => (byte)v).ToArray(), 10);

        int[] expected = [196, 35, 39, 119, 235, 215, 231, 226, 93, 23];
        Assert.Equal(expected, ec.Select(b => (int)b));
    }

    [Fact]
    public void Version_forced_isHonoured()
    {
        var qr = new QrCode("HELLO") { Version = 5 };
        var matrix = qr.GetMatrix();
        Assert.Equal(QrMatrixBuilder.SizeForVersion(5), matrix.Width);
    }

    [Fact]
    public void Mask_forced_isHonoured()
    {
        var withMask3 = new QrCode("HELLO WORLD") { Mask = 3 };
        var matrix = withMask3.GetMatrix();
        var formatBits = ReadQrFormatInfo(matrix, matrix.Width);
        var decodedMask = DecodeMaskFromFormatBits(formatBits);
        Assert.Equal(3, decodedMask);
    }

    [Fact]
    public void Version_tooSmallForContent_throwsFormatExceptionMentioningLengthAndCapacity()
    {
        var content = new string('A', 200); // far beyond version 1-H's alphanumeric capacity
        var qr = new QrCode(content) { Version = 1, ErrorCorrection = QrErrorCorrection.H };
        var ex = Assert.Throws<FormatException>(() => qr.GetMatrix());
        Assert.Contains("200", ex.Message);
    }

    [Fact]
    public void Content_exceedingVersion40Capacity_throwsFormatException()
    {
        var content = new string('9', 8000); // beyond even version 40-L's numeric capacity (7089)
        var qr = new QrCode(content);
        Assert.Throws<FormatException>(() => qr.GetMatrix());
    }

    [Fact]
    public void Version_outOfRange_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new QrCode("x") { Version = 41 }.GetMatrix());

    [Fact]
    public void Mask_outOfRange_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new QrCode("x") { Mask = 8 }.GetMatrix());

    [Theory]
    [InlineData(QrTextEncoding.Latin1)]
    [InlineData(QrTextEncoding.Utf8)]
    [InlineData(QrTextEncoding.Utf8Eci)]
    [InlineData(QrTextEncoding.Auto)]
    public void TextEncoding_latin1RepresentableContent_encodesSuccessfully(QrTextEncoding encoding)
    {
        var qr = new QrCode("Grusse") { TextEncoding = encoding };
        Assert.NotNull(qr.GetMatrix());
    }

    [Fact]
    public void TextEncoding_latin1_nonLatin1Content_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new QrCode("😀") { TextEncoding = QrTextEncoding.Latin1 }.GetMatrix());

    [Theory]
    [InlineData(QrTextEncoding.Utf8)]
    [InlineData(QrTextEncoding.Utf8Eci)]
    [InlineData(QrTextEncoding.Auto)]
    public void TextEncoding_nonLatin1Content_encodesSuccessfully(QrTextEncoding encoding)
    {
        var qr = new QrCode("Grüße 😀") { TextEncoding = encoding };
        Assert.NotNull(qr.GetMatrix());
    }

    [Fact]
    public void TextEncoding_auto_nonLatin1Content_producesTheSameMatrixAsUtf8Eci()
    {
        var auto = new QrCode("Grüße 😀") { TextEncoding = QrTextEncoding.Auto, Version = 3, Mask = 0 };
        var eci = new QrCode("Grüße 😀") { TextEncoding = QrTextEncoding.Utf8Eci, Version = 3, Mask = 0 };
        Assert.True(MatricesEqual(auto.GetMatrix(), eci.GetMatrix()));
    }

    [Fact]
    public void GetMatrix_calledTwice_returnsTheSameCachedInstance()
    {
        var qr = new QrCode("cache me");
        Assert.Same(qr.GetMatrix(), qr.GetMatrix());
    }

    [Fact]
    public void ByteArrayConstructor_encodesVerbatimBytesInByteMode()
    {
        byte[] content = [0x41, 0x42, 0xE9]; // 'A', 'B', and Latin-1 é (not valid UTF-8 on its own)
        var qr = new QrCode(content);
        Assert.NotNull(qr.GetMatrix());
    }

    private static bool MatricesEqual(BarcodeMatrix a, BarcodeMatrix b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.IsDark(x, y) != b.IsDark(x, y)) return false;
        return true;
    }

    private static int ReadQrFormatInfo(BarcodeMatrix matrix, int size)
    {
        var bits = 0;
        for (var i = 0; i <= 5; i++) bits |= BitAt(matrix, 8, i) << i;
        bits |= BitAt(matrix, 8, 7) << 6;
        bits |= BitAt(matrix, 8, 8) << 7;
        bits |= BitAt(matrix, 7, 8) << 8;
        for (var i = 9; i < 15; i++) bits |= BitAt(matrix, 14 - i, 8) << i;
        return bits;
    }

    private static int DecodeMaskFromFormatBits(int formatBits)
    {
        // The 15-bit field is (2-bit EC indicator, 3-bit mask) followed by 10 BCH bits, so the
        // data occupies the top 5 bits and the mask is bits 12-10.
        var unmasked = formatBits ^ Convert.ToInt32("101010000010010", 2);
        return (unmasked >> 10) & 0b111;
    }

    private static int BitAt(BarcodeMatrix matrix, int x, int y) => matrix.IsDark(x, y) ? 1 : 0;

    /// <summary>Reads the data/EC bits back off the encoding region in the same order <see cref="QrMatrixBuilder.PlaceData"/> writes them, undoing <paramref name="mask"/>.</summary>
    private static bool[] ReadDataBitsInPlacementOrder(BarcodeMatrix matrix, bool[,] isFunction, int size, int mask, int totalBits)
    {
        var bits = new bool[totalBits];
        var bitIndex = 0;

        for (var x = size - 1; x >= 1; x -= 2)
        {
            if (x == 6) x = 5;
            var upward = ((x + 1) & 2) == 0;
            for (var vert = 0; vert < size; vert++)
            {
                var y = upward ? size - 1 - vert : vert;
                for (var j = 0; j < 2; j++)
                {
                    var xx = x - j;
                    if (isFunction[y, xx]) continue;
                    if (bitIndex >= totalBits) continue;

                    var masked = matrix.IsDark(xx, y);
                    var unmasked = masked ^ QrMasking.Condition(mask, y, xx);
                    bits[bitIndex] = unmasked;
                    bitIndex++;
                }
            }
        }

        return bits;
    }
}
