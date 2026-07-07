// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Aztec;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for the Aztec high-level encoder, bit-stuffing, symbol-size selection, and the mode message.</summary>
public sealed class AztecEncoderTests
{
    private static string BitsToString(List<bool> bits) => string.Concat(bits.Select(b => b ? '1' : '0'));

    private static List<bool> Bits(string s) => [.. s.Select(c => c == '1')];

    // ---- High-level encoder: mode transitions ----

    [Fact]
    public void Encode_upperOnlyContent_staysInUpperMode_noLatchesOrShifts()
    {
        // "AB": both letters land directly in Upper's own table (codes 2, 3), 5 bits each, no
        // latch/shift codes at all.
        var bits = AztecHighLevelEncoder.Encode("AB"u8);
        Assert.Equal(10, bits.Count);
        Assert.Equal("0001000011", BitsToString(bits)); // code 2 = 00010, code 3 = 00011
    }

    [Fact]
    public void Encode_lowerRun_latchesToLower()
    {
        // Three lower-case letters is enough to prefer a Lower latch (L/L = code 28 = 11100 in
        // Upper) over three single-character detours, since there is no per-character Lower shift.
        var bits = AztecHighLevelEncoder.Encode("abc"u8);
        var text = BitsToString(bits);
        Assert.StartsWith("11100", text); // Upper's L/L
        Assert.Equal(5 + 3 * 5, bits.Count); // one latch + 3 direct Lower codes
    }

    [Fact]
    public void Encode_singleDigitAmongLetters_latchesToDigitThenShiftsBackForTheTrailingLetter()
    {
        // Digit has no shift from Upper, only Latch (D/L = code 30), so a single embedded digit
        // still needs a full latch there; returning to Upper for just the trailing 'B' is cheaper
        // as Digit's own Upper shift (U/S = code 15) than a second latch.
        var bits = AztecHighLevelEncoder.Encode("A1B"u8);
        var text = BitsToString(bits);
        Assert.Equal("00010" + "11110" + "0011" + "1111" + "00011", text);
        // 'A' (Upper 2) + D/L (Upper 30) + '1' (Digit 3, 4 bits) + U/S (Digit 15, 4 bits) + 'B' (Upper 3)
    }

    [Fact]
    public void Encode_punctuationChar_usesShiftNotLatch_forASingleOccurrence()
    {
        // A single '!' amid otherwise-Upper content: Upper has a direct Punct shift (P/S = code 0),
        // cheaper than a Punct latch since only one character needs it.
        var bits = AztecHighLevelEncoder.Encode("A!B"u8);
        var text = BitsToString(bits);
        Assert.Equal("00010" + "00000" + "00110" + "00011", text);
        // 'A' (Upper 2) + P/S (Upper 0) + '!' (Punct 6) + 'B' (Upper 3)
    }

    [Fact]
    public void Encode_lowerToUpperSingleLetter_usesUpperShift()
    {
        // Lower has a direct Upper shift (U/S = code 28); a single embedded upper-case letter
        // should shift rather than latch away from Lower and back.
        var bits = AztecHighLevelEncoder.Encode("aAa"u8);
        var text = BitsToString(bits);
        Assert.Equal("11100" + "00010" + "11100" + "00010" + "00010", text);
        // L/L (Upper 28) + 'a' (Lower 2) + U/S (Lower 28) + 'A' (Upper 2) + 'a' (Lower 2)
    }

    [Fact]
    public void Encode_mixedModeControlChar_latchesToMixed()
    {
        var bits = AztecHighLevelEncoder.Encode([1]); // Ctrl-A: Mixed code 2, no shift path exists
        var text = BitsToString(bits);
        Assert.Equal("11101" + "00010", text); // Upper's M/L (29) + Mixed code 2
    }

    [Fact]
    public void Encode_nulByte_usesBinaryShiftEvenThoughItIsBelow128()
    {
        // NUL has no code in any of the five character tables (see AztecTablesTests), so it must
        // always go through binary shift, unlike every other byte 0x01-0x7F.
        var bits = AztecHighLevelEncoder.Encode([0]);
        var text = BitsToString(bits);
        Assert.Equal("11111" + "00001" + "00000000", text); // Upper's B/S (31) + length 1 + the raw byte
    }

    [Fact]
    public void Encode_highByte_usesBinaryShift()
    {
        byte[] content = [0xFF];
        var bits = AztecHighLevelEncoder.Encode(content);
        var text = BitsToString(bits);
        Assert.Equal("11111" + "00001" + "11111111", text);
    }

    [Fact]
    public void Encode_binaryRunOverThirtyOneBytes_usesExtendedElevenBitLength()
    {
        var content = new byte[32];
        for (var i = 0; i < content.Length; i++) content[i] = 0xFF;
        var bits = AztecHighLevelEncoder.Encode(content);
        var text = BitsToString(bits);
        // B/S, 5-bit zero (signals extended length), 11-bit (32-31=1), then 32 raw bytes.
        Assert.StartsWith("11111" + "00000" + "00000000001", text);
        Assert.Equal(5 + 5 + 11 + (32 * 8), bits.Count);
    }

    [Fact]
    public void Encode_binaryShiftFromDigitMode_latchesToUpperFirst()
    {
        // Digit has no direct binary-shift code (only 16 codes, 0-15); a byte needing binary shift
        // while in Digit mode must first latch to Upper (Digit's U/L = code 14, 4 bits).
        var content = new byte[] { (byte)'1', 0xFF };
        var bits = AztecHighLevelEncoder.Encode(content);
        var text = BitsToString(bits);
        Assert.Equal("11110" + "0011" + "1110" + "11111" + "00001" + "11111111", text);
        // D/L (Upper 30) + '1' (Digit 3, 4 bits) + U/L (Digit 14, 4 bits) + B/S (Upper 31) + len 1 + byte
    }

    [Fact]
    public void Encode_isDeterministic()
    {
        var a = AztecHighLevelEncoder.Encode("VellumPdf Aztec 123!"u8);
        var b = AztecHighLevelEncoder.Encode("VellumPdf Aztec 123!"u8);
        Assert.Equal(a, b);
    }

    // ---- Bit-stuffing ----

    [Fact]
    public void StuffCodewords_allZeroRun_stuffsComplementaryOneBit()
    {
        // 5 zero bits (wordBits - 1 for a 6-bit word): the codeword is completed with a 1 rather
        // than reading a 6th zero from the stream, and only 5 bits are consumed.
        var codewords = AztecEncoder.StuffCodewords(Bits("00000" + "1"), 6);
        Assert.Equal(2, codewords.Length);
        Assert.Equal(0b000001, codewords[0]);
    }

    [Fact]
    public void StuffCodewords_allOneRun_stuffsComplementaryZeroBit()
    {
        var codewords = AztecEncoder.StuffCodewords(Bits("11111" + "0"), 6);
        Assert.Equal(0b111110, codewords[0]);
    }

    [Fact]
    public void StuffCodewords_noCodewordIsAllZeroOrAllOne()
    {
        var random = new Random(12345);
        var bits = new List<bool>();
        for (var i = 0; i < 500; i++) bits.Add(random.Next(2) == 1);

        foreach (var wordBits in new[] { 6, 8, 10, 12 })
        {
            var codewords = AztecEncoder.StuffCodewords(bits, wordBits);
            var max = (1 << wordBits) - 1;
            Assert.All(codewords, c => Assert.True(c != 0 && c != max, $"codeword {c} is all-0 or all-1 for wordBits={wordBits}"));
        }
    }

    [Fact]
    public void StuffCodewords_finalPartialCodeword_padsWithOneBits()
    {
        // 2 bits short of a full 6-bit codeword: padded with 1s to "XXXX11".
        var codewords = AztecEncoder.StuffCodewords(Bits("0101"), 6);
        Assert.Equal([0b010111], codewords);
    }

    [Fact]
    public void StuffCodewords_finalPartialCodeword_flipsLastBitIfPaddingWouldMakeAllOnes()
    {
        // "11111" padded with a 1 would be all-ones (illegal); the last bit flips to 0 instead.
        var codewords = AztecEncoder.StuffCodewords(Bits("11111"), 6);
        Assert.Equal([0b111110], codewords);
    }

    [Fact]
    public void StuffCodewords_exactMultipleOfWordBits_needsNoPadding()
    {
        var codewords = AztecEncoder.StuffCodewords(Bits("010101"), 6);
        Assert.Equal([0b010101], codewords);
    }

    // ---- Mode message ----

    [Fact]
    public void BuildModeMessage_compact_encodesLayersAndDataCodewordCount_andPassesItsOwnRsCheck()
    {
        var size = AztecSymbolInfo.Compact[1]; // 2 layers
        var bits = AztecEncoder.BuildModeMessage(size, dataCodewordCount: 19);
        Assert.Equal(28, bits.Count);

        AssertModeMessageChecksumValid(bits, dataWordCount: 2, checkWordCount: 5);

        var layersMinus1 = ReadBits(bits, 0, 2);
        var codewordsMinus1 = ReadBits(bits, 2, 6);
        Assert.Equal(1, layersMinus1); // 2 layers - 1
        Assert.Equal(18, codewordsMinus1); // 19 - 1
    }

    [Fact]
    public void BuildModeMessage_fullRange_encodesLayersAndDataCodewordCount_andPassesItsOwnRsCheck()
    {
        var size = AztecSymbolInfo.FullRange[8]; // layer 9 (first 10-bit-word size)
        var bits = AztecEncoder.BuildModeMessage(size, dataCodewordCount: 100);
        Assert.Equal(40, bits.Count);

        AssertModeMessageChecksumValid(bits, dataWordCount: 4, checkWordCount: 6);

        var layersMinus1 = ReadBits(bits, 0, 5);
        var codewordsMinus1 = ReadBits(bits, 5, 11);
        Assert.Equal(8, layersMinus1); // layer 9 - 1
        Assert.Equal(99, codewordsMinus1);
    }

    private static void AssertModeMessageChecksumValid(List<bool> bits, int dataWordCount, int checkWordCount)
    {
        var nibbles = new int[dataWordCount + checkWordCount];
        for (var i = 0; i < nibbles.Length; i++) nibbles[i] = ReadBits(bits, i * 4, 4);

        var rs = new ReedSolomonBinary(GaloisField.Gf16, firstRoot: 1);
        var expectedCheck = rs.ComputeRemainder(nibbles[..dataWordCount], checkWordCount);
        Assert.Equal(expectedCheck, nibbles[dataWordCount..]);
    }

    private static int ReadBits(List<bool> bits, int start, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++) value = (value << 1) | (bits[start + i] ? 1 : 0);
        return value;
    }

    // ---- Symbol-size selection ----

    [Fact]
    public void SelectSize_shortContent_picksSmallestCompact()
    {
        var bits = AztecHighLevelEncoder.Encode("AB"u8);
        var (size, dataCodewords) = AztecEncoder.SelectSize(bits, AztecFormat.Automatic, ecPercent: 23);
        Assert.True(size.IsCompact);
        Assert.Equal(1, size.Layers);
        Assert.True(dataCodewords.Length <= size.CodewordCount);
    }

    [Fact]
    public void SelectSize_automatic_prefersCompactOverFullRangeWhenBothFit()
    {
        var bits = AztecHighLevelEncoder.Encode("AB"u8);
        var (size, _) = AztecEncoder.SelectSize(bits, AztecFormat.Automatic, ecPercent: 23);
        Assert.True(size.IsCompact);
    }

    [Fact]
    public void SelectSize_forcedFullRange_neverPicksCompact()
    {
        var bits = AztecHighLevelEncoder.Encode("AB"u8);
        var (size, _) = AztecEncoder.SelectSize(bits, AztecFormat.FullRange, ecPercent: 23);
        Assert.False(size.IsCompact);
    }

    [Fact]
    public void SelectSize_contentExceedingLargestCompact_throwsForCompactFormat()
    {
        var bits = AztecHighLevelEncoder.Encode(new byte[400]);
        Assert.Throws<FormatException>(() => AztecEncoder.SelectSize(bits, AztecFormat.Compact, ecPercent: 23));
    }

    [Fact]
    public void SelectSize_contentExceedingEverySize_throwsForFullRangeFormat()
    {
        var bits = AztecHighLevelEncoder.Encode(new byte[3000]);
        Assert.Throws<FormatException>(() => AztecEncoder.SelectSize(bits, AztecFormat.FullRange, ecPercent: 23));
    }

    [Fact]
    public void SelectSize_higherErrorCorrectionPercent_needsALargerSymbol()
    {
        var bits = AztecHighLevelEncoder.Encode("VellumPdf Aztec Code error correction sizing test content"u8);
        var (lowEc, _) = AztecEncoder.SelectSize(bits, AztecFormat.Automatic, ecPercent: 5);
        var (highEc, _) = AztecEncoder.SelectSize(bits, AztecFormat.Automatic, ecPercent: 90);
        Assert.True(highEc.CodewordCount >= lowEc.CodewordCount);
    }

    // ---- Public API surface ----

    [Fact]
    public void AztecCode_nullText_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AztecCode((string)null!));
    }

    [Fact]
    public void AztecCode_nullBytes_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AztecCode((byte[])null!));
    }

    [Fact]
    public void AztecCode_emptyText_throws()
    {
        Assert.Throws<ArgumentException>(() => new AztecCode(""));
    }

    [Fact]
    public void AztecCode_emptyBytes_throws()
    {
        Assert.Throws<ArgumentException>(() => new AztecCode(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(96)]
    public void AztecCode_errorCorrectionPercentOutOfRange_throws(int percent)
    {
        var barcode = new AztecCode("Vellum") { ErrorCorrectionPercent = percent };
        Assert.Throws<ArgumentException>(() => barcode.GetMatrix());
    }

    [Fact]
    public void AztecCode_nonLatin1Text_throwsFormatException()
    {
        var barcode = new AztecCode("café中"); // trailing CJK character is outside Latin-1
        Assert.Throws<FormatException>(() => barcode.GetMatrix());
    }

    [Fact]
    public void AztecCode_getMatrix_isDeterministic()
    {
        var a = new AztecCode("VellumPdf").GetMatrix();
        var b = new AztecCode("VellumPdf").GetMatrix();
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                Assert.Equal(a.IsDark(x, y), b.IsDark(x, y));
    }

    [Fact]
    public void AztecCode_getMatrix_producesASquareSymbol()
    {
        var matrix = new AztecCode("VellumPdf Aztec Code").GetMatrix();
        Assert.Equal(matrix.Width, matrix.Height);
    }
}
