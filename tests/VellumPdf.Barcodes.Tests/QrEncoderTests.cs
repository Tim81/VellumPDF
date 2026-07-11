// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
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

    // Thonky's kanji-mode-encoding tutorial page worked example: "茗荷" (0x89D7 then 0xE4AA in
    // input order, matching the tutorial's own character ordering) as a version 1 Kanji segment.
    [Fact]
    public void BitStream_myogaKanjiExample_matchesThonkyModeCountAndData()
    {
        var writer = new BitWriter();
        var segments = new[] { new QrSegment(QrSegmentMode.Kanji, 0, 2, 2) };
        QrBitStreamBuilder.WriteSegments(
            writer,
            "茗荷",
            segments,
            mode => (QrTables.ModeIndicator(mode), QrTables.ModeIndicatorBits),
            mode => QrTables.CharacterCountBits(1, mode),
            Encoding.Latin1);

        const string expectedBits = "1000" + "00000010" + "1101010101010" + "0011010010111";
        Assert.Equal(expectedBits.Length, writer.BitCount);

        var bytes = writer.ToArray();
        var actualBits = new StringBuilder();
        for (var i = 0; i < expectedBits.Length; i++)
            actualBits.Append((bytes[i / 8] >> (7 - (i % 8))) & 1);
        Assert.Equal(expectedBits, actualBits.ToString());
    }

    [Fact]
    public void GetMatrix_kanjiContent_encodesSuccessfully()
    {
        var qr = new QrCode("点荷茗");
        Assert.NotNull(qr.GetMatrix());
    }

    [Fact]
    public void GetMatrix_kanjiContent_isDenserThanEquivalentByteMode()
    {
        // "点荷茗" (three Kanji-eligible characters) fits version 1-H (72 data bits) in Kanji
        // mode: 12 header bits + 3*13 = 51 data bits. In Byte mode, each character is a 3-byte
        // UTF-8 sequence, so the same content would need 12 header bits + 9*8 = 84 bits and spill
        // into version 2. A version 1 result here is only possible if the encoder actually chose
        // Kanji mode, not just round-tripped through Byte mode.
        var qr = new QrCode("点荷茗") { ErrorCorrection = QrErrorCorrection.H };
        var matrix = qr.GetMatrix();
        Assert.Equal(QrMatrixBuilder.SizeForVersion(1), matrix.Width);
    }

    [Fact]
    public void TextEncoding_auto_pureKanjiContent_omitsTheEciHeader()
    {
        // "こんにちは世界" is entirely Kanji-eligible, so segmentation produces no Byte-mode run.
        // Auto still picks UTF-8 (the content isn't Latin-1-representable), but the ECI header
        // that declares "the following Byte-mode bytes are UTF-8" would have nothing to apply to,
        // and some decoders (zxing-cpp among them) mishandle an ECI header followed directly by a
        // Kanji segment. The symbol's first mode indicator must be Kanji's, not ECI's.
        var qr = new QrCode("こんにちは世界") { ErrorCorrection = QrErrorCorrection.M, Version = 1, Mask = 0 };
        var matrix = qr.GetMatrix();

        var (_, isFunction) = QrMatrixBuilder.BuildFunctionPatterns(1);
        var ecInfo = QrTables.GetEcBlockInfo(1, QrErrorCorrection.M);
        var bits = ReadDataBitsInPlacementOrder(matrix, isFunction, 21, mask: 0, totalBits: ecInfo.TotalDataCodewords * 8);

        Assert.Equal(QrTables.ModeIndicator(QrSegmentMode.Kanji), ReadBitsAsInt(bits, 0, QrTables.ModeIndicatorBits));
    }

    [Fact]
    public void TextEncoding_auto_kanjiPlusNonKanjiEligibleNonLatin1Content_stillWritesTheEciHeader()
    {
        // "点😀": 点 is Kanji-eligible, but the emoji is not representable in any mode but Byte,
        // so this content does produce a Byte-mode segment and needs the UTF-8 ECI header to
        // interpret it. The omission above must not become a blanket "never write ECI" rule.
        var qr = new QrCode("点😀") { ErrorCorrection = QrErrorCorrection.M, Version = 1, Mask = 0 };
        var matrix = qr.GetMatrix();

        var (_, isFunction) = QrMatrixBuilder.BuildFunctionPatterns(1);
        var ecInfo = QrTables.GetEcBlockInfo(1, QrErrorCorrection.M);
        var bits = ReadDataBitsInPlacementOrder(matrix, isFunction, 21, mask: 0, totalBits: ecInfo.TotalDataCodewords * 8);

        Assert.Equal(QrTables.EciModeIndicator, ReadBitsAsInt(bits, 0, QrTables.ModeIndicatorBits));
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

    // ── GS1 mode ─────────────────────────────────────────────────────────

    [Fact]
    public void Gs1_none_matchesTheSymbolBuiltWithoutSettingGs1()
    {
        // Regression guard: Gs1 defaults to None, and setting it explicitly must not perturb the
        // plain-text path at all.
        var withDefault = new QrCode("HELLO WORLD");
        var withExplicitNone = new QrCode("HELLO WORLD") { Gs1 = QrGs1Mode.None };
        Assert.True(MatricesEqual(withDefault.GetMatrix(), withExplicitNone.GetMatrix()));
    }

    [Fact]
    public void Gs1_elementString_knownPayload_encodesSuccessfully()
    {
        const string content = "(01)09501101020917(17)261231(10)ABC123";
        var qr = new QrCode(content) { Gs1 = QrGs1Mode.ElementString };
        Assert.NotNull(qr.GetMatrix());
    }

    [Fact]
    public void Gs1_elementString_writesTheFnc1FirstPositionIndicatorBeforeTheData()
    {
        // AI 01 is fixed-length (14-digit GTIN), so the parsed payload needs no separator and is
        // one pure-numeric run: FNC1(4) + mode(4) + count(10) + numeric data.
        const string content = "(01)09501101020917";
        var qr = new QrCode(content) { Gs1 = QrGs1Mode.ElementString, ErrorCorrection = QrErrorCorrection.M, Version = 1, Mask = 0 };
        var matrix = qr.GetMatrix();

        var (_, isFunction) = QrMatrixBuilder.BuildFunctionPatterns(1);
        var ecInfo = QrTables.GetEcBlockInfo(1, QrErrorCorrection.M);
        var bits = ReadDataBitsInPlacementOrder(matrix, isFunction, 21, mask: 0, totalBits: ecInfo.TotalDataCodewords * 8);

        Assert.Equal(QrTables.Fnc1FirstPositionModeIndicator, ReadBitsAsInt(bits, 0, QrTables.ModeIndicatorBits));
        Assert.Equal(QrTables.ModeIndicator(QrSegmentMode.Numeric), ReadBitsAsInt(bits, 4, QrTables.ModeIndicatorBits));

        var countBits = QrTables.CharacterCountBits(1, QrSegmentMode.Numeric);
        Assert.Equal(16, ReadBitsAsInt(bits, 8, countBits)); // "01" + the 14-digit GTIN value = 16 digits
    }

    [Fact]
    public void Gs1_elementString_fnc1BitsCountTowardCapacity_soTheSameContentNeedsOneMoreVersionThanPlainText()
    {
        // AI 90 (company-internal, variable length, no fixed length) plus a 39-digit value is 41
        // numeric characters: exactly 137 numeric data bits (13 full 3-digit groups = 130 bits,
        // plus a trailing 2-digit group = 7 bits). Version 1-L holds 152 data bits: 14 (mode +
        // count, no FNC1) + 137 = 151 fits; 18 (mode + count + the 4-bit FNC1 indicator) + 137 =
        // 155 does not — so only the GS1-mode symbol needs to spill into version 2.
        var digits = new string('1', 39);
        var content = $"(90){digits}";

        var plainPayload = Gs1ElementString.Parse(content).EncoderPayload;
        var plain = new QrCode(plainPayload) { ErrorCorrection = QrErrorCorrection.L };
        Assert.Equal(QrMatrixBuilder.SizeForVersion(1), plain.GetMatrix().Width);

        var gs1 = new QrCode(content) { Gs1 = QrGs1Mode.ElementString, ErrorCorrection = QrErrorCorrection.L };
        Assert.Equal(QrMatrixBuilder.SizeForVersion(2), gs1.GetMatrix().Width);
    }

    [Fact]
    public void Gs1_digitalLink_encodesTheSameMatrixAsPlainTextOfTheCanonicalUri()
    {
        const string content = "(01)09501101020917(17)261231(10)ABC123";
        var expectedUri = Gs1DigitalLink.Build(content);

        var digitalLink = new QrCode(content) { Gs1 = QrGs1Mode.DigitalLink };
        var plainUri = new QrCode(expectedUri);

        // DigitalLink is "just a URL": no FNC1, no mode-indicator change, so it must produce
        // exactly the matrix a plain-text QR of the same URI would.
        Assert.True(MatricesEqual(digitalLink.GetMatrix(), plainUri.GetMatrix()));
    }

    [Fact]
    public void Gs1_elementString_malformedContent_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new QrCode("not a GS1 element string") { Gs1 = QrGs1Mode.ElementString }.GetMatrix());

    [Fact]
    public void Gs1_digitalLink_malformedContent_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new QrCode("not a GS1 element string") { Gs1 = QrGs1Mode.DigitalLink }.GetMatrix());

    [Fact]
    public void Gs1_byteArrayConstructor_throwsArgumentException()
    {
        byte[] content = [0x30, 0x31];
        Assert.Throws<ArgumentException>(() => new QrCode(content) { Gs1 = QrGs1Mode.ElementString }.GetMatrix());
    }

    [Fact]
    public void Gs1_elementString_emptyContent_throwsFormatException() =>
        // Gs1ElementString.Parse itself throws ArgumentException for empty input (a guard written
        // against its own parameter contract), but QrCode.Gs1's documented contract -- and the
        // barcodes guide -- promises FormatException for any malformed GS1 content. QrEncoder must
        // intercept the empty case before it reaches Parse.
        Assert.Throws<FormatException>(() => new QrCode("") { Gs1 = QrGs1Mode.ElementString }.GetMatrix());

    [Fact]
    public void Gs1_digitalLink_emptyContent_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new QrCode("") { Gs1 = QrGs1Mode.DigitalLink }.GetMatrix());

    [Fact]
    public void Gs1_elementString_separatorAlwaysWrittenAsByteModeCodeword_betweenNumericRuns()
    {
        // AI 90 (company-internal, variable length) is not the last element, so its value needs a
        // separator before AI 91; both AI+value runs ("90111" and "91222") are five-digit strings
        // cheap enough that Numeric mode wins over Byte for them (header 14 + data 17 bits = 31,
        // versus Byte's header 12 + data 40 = 52), so the separator has no digit neighbour to hide
        // inside and must appear as its own one-codeword Byte segment.
        const string content = "(90)111(91)222";
        var qr = new QrCode(content) { Gs1 = QrGs1Mode.ElementString, ErrorCorrection = QrErrorCorrection.M, Version = 1, Mask = 0 };
        var matrix = qr.GetMatrix();

        var (_, isFunction) = QrMatrixBuilder.BuildFunctionPatterns(1);
        var ecInfo = QrTables.GetEcBlockInfo(1, QrErrorCorrection.M);
        var bits = ReadDataBitsInPlacementOrder(matrix, isFunction, 21, mask: 0, totalBits: ecInfo.TotalDataCodewords * 8);

        var numericCountBits = QrTables.CharacterCountBits(1, QrSegmentMode.Numeric);
        var byteCountBits = QrTables.CharacterCountBits(1, QrSegmentMode.Byte);

        // FNC1, then Numeric("90111"): a 3-digit group (10 bits) followed by a 2-digit group (7
        // bits) = 17 data bits, regardless of the digits' values.
        Assert.Equal(QrTables.Fnc1FirstPositionModeIndicator, ReadBitsAsInt(bits, 0, 4));
        Assert.Equal(QrTables.ModeIndicator(QrSegmentMode.Numeric), ReadBitsAsInt(bits, 4, 4));
        Assert.Equal(5, ReadBitsAsInt(bits, 8, numericCountBits));
        var afterFirstNumeric = 8 + numericCountBits + 17;

        // The separator: exactly one Byte-mode codeword, value 0x1D.
        Assert.Equal(QrTables.ModeIndicator(QrSegmentMode.Byte), ReadBitsAsInt(bits, afterFirstNumeric, 4));
        var byteCountPos = afterFirstNumeric + 4;
        Assert.Equal(1, ReadBitsAsInt(bits, byteCountPos, byteCountBits));
        var byteDataPos = byteCountPos + byteCountBits;
        Assert.Equal(0x1D, ReadBitsAsInt(bits, byteDataPos, 8));

        // Then the second Numeric run, "91222".
        var afterSeparator = byteDataPos + 8;
        Assert.Equal(QrTables.ModeIndicator(QrSegmentMode.Numeric), ReadBitsAsInt(bits, afterSeparator, 4));
        Assert.Equal(5, ReadBitsAsInt(bits, afterSeparator + 4, numericCountBits));
    }

    [Fact]
    public void Gs1_elementString_percentInValue_isCarriedAsByteModeCodeword_notAlphanumeric()
    {
        // AI 01's fixed 14-digit value, AI 10's own "10", and the leading "50" of its value form
        // one contiguous 20-digit run (Numeric mode). "%" is deliberately excluded from GS1 QR's
        // alphanumeric charset (see PrepareGs1ElementStringContent's remarks: alphanumeric mode's
        // %-as-separator escape is unsafe once the segmenter picks modes automatically), so "%OFF"
        // must fall to Byte mode, with the raw, undoubled 0x25 byte for "%".
        const string content = "(01)09501101020917(10)50%OFF";
        var qr = new QrCode(content) { Gs1 = QrGs1Mode.ElementString, ErrorCorrection = QrErrorCorrection.M, Version = 2, Mask = 0 };
        var matrix = qr.GetMatrix();

        var (_, isFunction) = QrMatrixBuilder.BuildFunctionPatterns(2);
        var ecInfo = QrTables.GetEcBlockInfo(2, QrErrorCorrection.M);
        var bits = ReadDataBitsInPlacementOrder(matrix, isFunction, QrMatrixBuilder.SizeForVersion(2), mask: 0, totalBits: ecInfo.TotalDataCodewords * 8);

        var numericCountBits = QrTables.CharacterCountBits(2, QrSegmentMode.Numeric);
        var byteCountBits = QrTables.CharacterCountBits(2, QrSegmentMode.Byte);

        Assert.Equal(QrTables.Fnc1FirstPositionModeIndicator, ReadBitsAsInt(bits, 0, 4));
        Assert.Equal(QrTables.ModeIndicator(QrSegmentMode.Numeric), ReadBitsAsInt(bits, 4, 4));
        Assert.Equal(20, ReadBitsAsInt(bits, 8, numericCountBits));

        // 20 digits = six 3-digit groups (10 bits each) + one 2-digit group (7 bits) = 67 data bits.
        var byteModePos = 8 + numericCountBits + 67;
        Assert.Equal(QrTables.ModeIndicator(QrSegmentMode.Byte), ReadBitsAsInt(bits, byteModePos, 4));

        var byteCountPos = byteModePos + 4;
        Assert.Equal(4, ReadBitsAsInt(bits, byteCountPos, byteCountBits)); // "%OFF"

        var percentBitPos = byteCountPos + byteCountBits;
        Assert.Equal(0x25, ReadBitsAsInt(bits, percentBitPos, 8)); // '%'
    }

    [Fact]
    public void Gs1_elementString_multiSegment_writesFnc1IndicatorExactlyOnce()
    {
        // A numeric run, then a mixed byte-mode run (letters, the field separator, and short
        // digit runs too cheap to be worth a new Numeric header) and a third AI: several
        // segments, giving the "FNC1 written once per symbol, not once per segment" invariant
        // something to regress against.
        const string content = "(01)09501101020917(10)LOT99(21)SER456";
        var qr = new QrCode(content) { Gs1 = QrGs1Mode.ElementString, ErrorCorrection = QrErrorCorrection.M, Version = 2, Mask = 0 };
        var matrix = qr.GetMatrix();

        var (_, isFunction) = QrMatrixBuilder.BuildFunctionPatterns(2);
        var ecInfo = QrTables.GetEcBlockInfo(2, QrErrorCorrection.M);
        var bits = ReadDataBitsInPlacementOrder(matrix, isFunction, QrMatrixBuilder.SizeForVersion(2), mask: 0, totalBits: ecInfo.TotalDataCodewords * 8);

        Assert.Equal(QrTables.Fnc1FirstPositionModeIndicator, ReadBitsAsInt(bits, 0, 4));
        // The first 18 characters (AI 01's fixed-length value plus AI 10's own digits) are one
        // contiguous digit run: Numeric mode is far cheaper here than Byte, so this is the only
        // segmentation the cost model would ever choose for that prefix.
        Assert.Equal(QrTables.ModeIndicator(QrSegmentMode.Numeric), ReadBitsAsInt(bits, 4, 4));

        // Walk every subsequent segment header generically (ISO/IEC 18004 Table 2/3's own
        // mode-indicator/count/data-width rules -- not this encoder's internals) and confirm the
        // FNC1 indicator never reappears after position 0.
        var pos = 4;
        var fnc1SightingsAfterPositionZero = 0;
        while (pos + 4 <= bits.Length)
        {
            var indicator = ReadBitsAsInt(bits, pos, 4);
            if (indicator == QrTables.Fnc1FirstPositionModeIndicator)
            {
                fnc1SightingsAfterPositionZero++;
                break; // can't sensibly keep decoding past a misplaced FNC1; the assertion below fails the test
            }

            if (indicator is not (0b0001 or 0b0010 or 0b0100)) break; // terminator (0000) or padding: no more segments

            var mode = indicator switch
            {
                0b0001 => QrSegmentMode.Numeric,
                0b0010 => QrSegmentMode.Alphanumeric,
                _ => QrSegmentMode.Byte,
            };
            pos += 4;

            var countBits = QrTables.CharacterCountBits(2, mode);
            var count = ReadBitsAsInt(bits, pos, countBits);
            pos += countBits + SpecDataBits(mode, count);
        }

        Assert.Equal(0, fnc1SightingsAfterPositionZero);
    }

    [Fact]
    public void Gs1_elementString_boundaryPayload_doesNotDoubleCountFnc1BitsAgainstCapacity()
    {
        // AI 90 (company-internal, variable length, the only element so no separator applies)
        // with a 14-digit value: AI + value is one contiguous 16-digit numeric run (header 14
        // bits + data 54 bits [five 3-digit groups = 50, plus a trailing 1-digit group = 4] = 68
        // content bits). Version 1-H's capacity is exactly 72 bits (9 data codewords): 72-68 = 4
        // bits of headroom, exactly the FNC1-in-first-position indicator's width. If QrEncoder
        // ever double-counted gs1Bits (charging the 4-bit FNC1 cost twice against capacity while
        // only ever writing it once), this payload would wrongly appear to need 76 bits and spill
        // into version 2.
        const string content = "(90)12345678901234";
        var qr = new QrCode(content) { Gs1 = QrGs1Mode.ElementString, ErrorCorrection = QrErrorCorrection.H };
        var matrix = qr.GetMatrix();
        Assert.Equal(QrMatrixBuilder.SizeForVersion(1), matrix.Width);
    }

    // ── Structured Append ────────────────────────────────────────────────

    [Fact]
    public void StructuredAppendHeader_bitStream_matchesModeSequenceIndicatorAndParity()
    {
        // ISO/IEC 18004 §8.1: mode (0011) + sequence indicator (upper nibble = 0-based position,
        // lower nibble = total - 1) + parity. "Symbol 2 of 3" is index 1 of total 3: upper 0001,
        // lower 0010.
        var writer = new BitWriter();
        QrBitStreamBuilder.WriteStructuredAppendHeader(writer, index: 1, total: 3, parity: 0xA5);

        const string expectedBits = "0011" + "0001" + "0010" + "10100101";
        Assert.Equal(expectedBits.Length, writer.BitCount);

        var bytes = writer.ToArray();
        var actualBits = new StringBuilder();
        for (var i = 0; i < expectedBits.Length; i++)
            actualBits.Append((bytes[i / 8] >> (7 - (i % 8))) & 1);
        Assert.Equal(expectedBits, actualBits.ToString());
    }

    [Fact]
    public void StructuredAppend_twoPartMessage_parityIsXorOfConcatenatedBytes()
    {
        // "HI" + "!" = "HI!" -> 0x48 ^ 0x49 ^ 0x21 = 0x20, hand-derived.
        var symbols = QrCode.StructuredAppend(["HI", "!"]);
        Assert.Equal(2, symbols.Count);
        Assert.All(symbols, s => Assert.Equal((byte)0x20, s.StructuredAppendInfo!.Value.Parity));
    }

    [Fact]
    public void StructuredAppend_threePartMessage_parityIsXorOfConcatenatedBytes()
    {
        // "A" + "BC" + "D" = "ABCD" -> 0x41 ^ 0x42 ^ 0x43 ^ 0x44 = 0x04, hand-derived.
        var symbols = QrCode.StructuredAppend(["A", "BC", "D"]);
        Assert.Equal(3, symbols.Count);
        Assert.All(symbols, s => Assert.Equal((byte)0x04, s.StructuredAppendInfo!.Value.Parity));
    }

    [Fact]
    public void StructuredAppend_stampsEachSymbolWithItsIndexAndTheSharedTotal()
    {
        var symbols = QrCode.StructuredAppend(["A", "B", "C"]);
        for (var i = 0; i < symbols.Count; i++)
        {
            var info = symbols[i].StructuredAppendInfo;
            Assert.NotNull(info);
            Assert.Equal(i, info!.Value.Index);
            Assert.Equal(3, info.Value.Total);
        }
    }

    [Fact]
    public void StructuredAppend_sixteenParts_isAccepted()
    {
        var parts = Enumerable.Range(0, 16).Select(i => i.ToString()).ToArray();
        var symbols = QrCode.StructuredAppend(parts);
        Assert.Equal(16, symbols.Count);
        Assert.Equal(0, symbols[0].StructuredAppendInfo!.Value.Index);
        Assert.Equal(15, symbols[15].StructuredAppendInfo!.Value.Index);
        Assert.All(symbols, s => Assert.Equal(16, s.StructuredAppendInfo!.Value.Total));
    }

    [Fact]
    public void StructuredAppend_zeroParts_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => QrCode.StructuredAppend(Array.Empty<string>()));

    [Fact]
    public void StructuredAppend_seventeenParts_throwsArgumentException()
    {
        var parts = Enumerable.Range(0, 17).Select(i => i.ToString()).ToArray();
        Assert.Throws<ArgumentException>(() => QrCode.StructuredAppend(parts));
    }

    [Fact]
    public void StructuredAppend_headerIsWrittenBeforeTheFirstDataModeIndicator()
    {
        // Force a fixed version/mask on the first symbol of a 2-part set so the encoded bits can
        // be read back positionally: the SA header (20 bits) must precede the data segment's own
        // mode indicator, the same ordering the GS1 FNC1-first-position tests above check for
        // their marker.
        var symbols = QrCode.StructuredAppend(["HELLO", "WORLD"]);
        var first = symbols[0];
        var withForcedVersion = new QrCode(first.Text!)
        {
            ErrorCorrection = QrErrorCorrection.M,
            Version = 1,
            Mask = 0,
            StructuredAppendInfo = first.StructuredAppendInfo,
        };
        var matrix = withForcedVersion.GetMatrix();

        var (_, isFunction) = QrMatrixBuilder.BuildFunctionPatterns(1);
        var ecInfo = QrTables.GetEcBlockInfo(1, QrErrorCorrection.M);
        var bits = ReadDataBitsInPlacementOrder(matrix, isFunction, 21, mask: 0, totalBits: ecInfo.TotalDataCodewords * 8);

        Assert.Equal(QrTables.StructuredAppendModeIndicator, ReadBitsAsInt(bits, 0, 4));
        var info = first.StructuredAppendInfo!.Value;
        Assert.Equal((info.Index << 4) | (info.Total - 1), ReadBitsAsInt(bits, 4, 8));
        Assert.Equal(info.Parity, (byte)ReadBitsAsInt(bits, 12, 8));
        Assert.Equal(QrTables.ModeIndicator(QrSegmentMode.Alphanumeric), ReadBitsAsInt(bits, 20, 4));
    }

    [Fact]
    public void StructuredAppend_autoSplit_dividesContentIntoRoughlyEqualParts()
    {
        var symbols = QrCode.StructuredAppend("ABCDEFGHIJ", symbolCount: 3);
        Assert.Equal(3, symbols.Count);
        // 10 runes over 3 parts: base size 3, remainder 1 -> the first part gets the extra rune.
        Assert.Equal("ABCD", symbols[0].Text);
        Assert.Equal("EFG", symbols[1].Text);
        Assert.Equal("HIJ", symbols[2].Text);
    }

    [Fact]
    public void StructuredAppend_autoSplit_neverSplitsASurrogatePair()
    {
        // Two astral-plane emoji, each a UTF-16 surrogate pair: splitting on code units instead
        // of runes would cut the first emoji in half.
        const string content = "😀😃";
        var symbols = QrCode.StructuredAppend(content, symbolCount: 2);
        Assert.Equal(2, symbols.Count);
        Assert.Equal("😀", symbols[0].Text);
        Assert.Equal("😃", symbols[1].Text);
        Assert.Equal(content, string.Concat(symbols.Select(s => s.Text)));
    }

    [Fact]
    public void StructuredAppend_autoSplit_symbolCountOutOfRange_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => QrCode.StructuredAppend("hello", symbolCount: 0));

    /// <summary>ISO/IEC 18004 Table 2/3's mode/count-to-data-bit-width rule, reimplemented independently of <see cref="QrEncoder"/> for decode-side test assertions.</summary>
    private static int SpecDataBits(QrSegmentMode mode, int count) => mode switch
    {
        QrSegmentMode.Numeric => (10 * (count / 3)) + (count % 3) switch { 0 => 0, 1 => 4, _ => 7 },
        QrSegmentMode.Alphanumeric => (11 * (count / 2)) + (count % 2 == 1 ? 6 : 0),
        QrSegmentMode.Byte => count * 8,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

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

    /// <summary>Reads <paramref name="length"/> bits from <paramref name="bits"/> starting at <paramref name="start"/> as a big-endian integer.</summary>
    private static int ReadBitsAsInt(bool[] bits, int start, int length)
    {
        var value = 0;
        for (var i = 0; i < length; i++) value = (value << 1) | (bits[start + i] ? 1 : 0);
        return value;
    }

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
