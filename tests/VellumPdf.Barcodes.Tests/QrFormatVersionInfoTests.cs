// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="QrFormatVersionInfo"/>'s BCH computations, checked against every row of
/// ISO/IEC 18004 Annex C's Table C.1 (all 32 format information data values, masked both ways) and
/// Annex D's Table D.1 (all 34 version information values for versions 7-40), plus the worked
/// examples from Annex C/D themselves and the vectors Thonky publishes for HELLO WORLD.
/// </summary>
public sealed class QrFormatVersionInfoTests
{
    // Table C.1: (5-bit data value, QR-masked field as hex, Micro-QR-masked field as hex).
    public static TheoryData<int, int, int> TableC1 { get; } = new()
    {
        { 0b00000, 0x5412, 0x4445 },
        { 0b00001, 0x5125, 0x4172 },
        { 0b00010, 0x5E7C, 0x4E2B },
        { 0b00011, 0x5B4B, 0x4B1C },
        { 0b00100, 0x45F9, 0x55AE },
        { 0b00101, 0x40CE, 0x5099 },
        { 0b00110, 0x4F97, 0x5FC0 },
        { 0b00111, 0x4AA0, 0x5AF7 },
        { 0b01000, 0x77C4, 0x6793 },
        { 0b01001, 0x72F3, 0x62A4 },
        { 0b01010, 0x7DAA, 0x6DFD },
        { 0b01011, 0x789D, 0x68CA },
        { 0b01100, 0x662F, 0x7678 },
        { 0b01101, 0x6318, 0x734F },
        { 0b01110, 0x6C41, 0x7C16 },
        { 0b01111, 0x6976, 0x7921 },
        { 0b10000, 0x1689, 0x06DE },
        { 0b10001, 0x13BE, 0x03E9 },
        { 0b10010, 0x1CE7, 0x0CB0 },
        { 0b10011, 0x19D0, 0x0987 },
        { 0b10100, 0x0762, 0x1735 },
        { 0b10101, 0x0255, 0x1202 },
        { 0b10110, 0x0D0C, 0x1D5B },
        { 0b10111, 0x083B, 0x186C },
        { 0b11000, 0x355F, 0x2508 },
        { 0b11001, 0x3068, 0x203F },
        { 0b11010, 0x3F31, 0x2F66 },
        { 0b11011, 0x3A06, 0x2A51 },
        { 0b11100, 0x24B4, 0x34E3 },
        { 0b11101, 0x2183, 0x31D4 },
        { 0b11110, 0x2EDA, 0x3E8D },
        { 0b11111, 0x2BED, 0x3BBA },
    };

    // Table D.1: (version 7-40, 18-bit version information as hex).
    public static TheoryData<int, int> TableD1 { get; } = new()
    {
        { 7, 0x07C94 }, { 8, 0x085BC }, { 9, 0x09A99 }, { 10, 0x0A4D3 },
        { 11, 0x0BBF6 }, { 12, 0x0C762 }, { 13, 0x0D847 }, { 14, 0x0E60D },
        { 15, 0x0F928 }, { 16, 0x10B78 }, { 17, 0x1145D }, { 18, 0x12A17 },
        { 19, 0x13532 }, { 20, 0x149A6 }, { 21, 0x15683 }, { 22, 0x168C9 },
        { 23, 0x177EC }, { 24, 0x18EC4 }, { 25, 0x191E1 }, { 26, 0x1AFAB },
        { 27, 0x1B08E }, { 28, 0x1CC1A }, { 29, 0x1D33F }, { 30, 0x1ED75 },
        { 31, 0x1F250 }, { 32, 0x209D5 }, { 33, 0x216F0 }, { 34, 0x228BA },
        { 35, 0x2379F }, { 36, 0x24B0B }, { 37, 0x2542E }, { 38, 0x26A64 },
        { 39, 0x27541 }, { 40, 0x28C69 },
    };

    [Theory]
    [MemberData(nameof(TableC1))]
    public void ComputeQrFormatBits_everyTableC1Row_matches(int dataBits, int qrMaskedHex, int microMaskedHex)
    {
        _ = microMaskedHex;
        var level = (dataBits >> 3) switch
        {
            0b01 => QrErrorCorrection.L,
            0b00 => QrErrorCorrection.M,
            0b11 => QrErrorCorrection.Q,
            0b10 => QrErrorCorrection.H,
            _ => throw new InvalidOperationException(),
        };
        var mask = dataBits & 0b111;

        Assert.Equal(qrMaskedHex, QrFormatVersionInfo.ComputeQrFormatBits(level, mask));
    }

    [Theory]
    [MemberData(nameof(TableC1))]
    public void ComputeMicroFormatBits_everyTableC1Row_matches(int dataBits, int qrMaskedHex, int microMaskedHex)
    {
        _ = qrMaskedHex;
        var symbolNumber = dataBits >> 2;
        var maskReference = dataBits & 0b11;

        Assert.Equal(microMaskedHex, QrFormatVersionInfo.ComputeMicroFormatBits(symbolNumber, maskReference));
    }

    [Theory]
    [MemberData(nameof(TableD1))]
    public void ComputeVersionBits_everyTableD1Row_matches(int version, int expectedHex) =>
        Assert.Equal(expectedHex, QrFormatVersionInfo.ComputeVersionBits(version));

    [Fact]
    public void ComputeQrFormatBits_annexIExample_maskPattern2LevelM()
    {
        // ISO/IEC 18004 Annex I: level M, mask 010 -> unmasked 000101001101110, masked 101111001111100.
        var bits = QrFormatVersionInfo.ComputeQrFormatBits(QrErrorCorrection.M, 0b010);
        Assert.Equal(Convert.ToInt32("101111001111100", 2), bits);
    }

    [Theory]
    [InlineData(QrErrorCorrection.L, 0, "111011111000100")]
    [InlineData(QrErrorCorrection.M, 0, "101010000010010")]
    [InlineData(QrErrorCorrection.Q, 1, "011000001101000")]
    [InlineData(QrErrorCorrection.H, 7, "000100000111011")]
    public void ComputeQrFormatBits_thonkyVectors_match(QrErrorCorrection level, int mask, string expectedBinary) =>
        Assert.Equal(Convert.ToInt32(expectedBinary, 2), QrFormatVersionInfo.ComputeQrFormatBits(level, mask));

    [Theory]
    [InlineData(7, "000111110010010100")]
    [InlineData(40, "101000110001101001")]
    public void ComputeVersionBits_thonkyVectors_match(int version, string expectedBinary) =>
        Assert.Equal(Convert.ToInt32(expectedBinary, 2), QrFormatVersionInfo.ComputeVersionBits(version));

    [Fact]
    public void ComputeMicroFormatBits_annexIExample_symbolNumber1MaskReference1()
    {
        // ISO/IEC 18004 Annex I: M2-L (symbol number 1), mask 01 -> masked 101000010011001.
        var bits = QrFormatVersionInfo.ComputeMicroFormatBits(1, 0b01);
        Assert.Equal(Convert.ToInt32("101000010011001", 2), bits);
    }

    [Theory]
    [InlineData(QrErrorCorrection.L, 0b01)]
    [InlineData(QrErrorCorrection.M, 0b00)]
    [InlineData(QrErrorCorrection.Q, 0b11)]
    [InlineData(QrErrorCorrection.H, 0b10)]
    public void QrErrorCorrectionIndicator_matchesTable12(QrErrorCorrection level, int expected) =>
        Assert.Equal(expected, QrFormatVersionInfo.QrErrorCorrectionIndicator(level));
}
