// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="QrTables"/>. The error-correction block table is checked for internal
/// consistency across every version and level (each row's block counts and codeword lengths must
/// sum to that version's total codeword count, which is independent of level); the total-codeword
/// figures and alignment pattern centres for versions 2, 7 and 40 are cross-checked against the
/// ISO/IEC 18004 text (Table 9 and Annex E) and Thonky's published tutorial.
/// </summary>
public sealed class QrTablesTests
{
    [Theory]
    [InlineData(1, 26)]
    [InlineData(7, 196)]
    [InlineData(39, 3532)]
    [InlineData(40, 3706)]
    public void GetEcBlockInfo_totalCodewords_matchesPublishedFigures(int version, int expectedTotal)
    {
        foreach (var level in Enum.GetValues<QrErrorCorrection>())
            Assert.Equal(expectedTotal, QrTables.GetEcBlockInfo(version, level).TotalCodewords);
    }

    [Fact]
    public void GetEcBlockInfo_everyVersionAndLevel_dataPlusEcCodewordsEqualTheTotal()
    {
        for (var version = 1; version <= 40; version++)
        {
            foreach (var level in Enum.GetValues<QrErrorCorrection>())
            {
                var info = QrTables.GetEcBlockInfo(version, level);
                var actualTotal = info.TotalDataCodewords + (info.EcCodewordsPerBlock * info.TotalBlocks);
                Assert.True(
                    actualTotal == info.TotalCodewords,
                    $"v{version}-{level}: {info.TotalDataCodewords} data + {info.EcCodewordsPerBlock}*{info.TotalBlocks} ec = {actualTotal}, expected {info.TotalCodewords}.");
            }
        }
    }

    [Fact]
    public void GetEcBlockInfo_everyVersion_totalCodewordsIsTheSameAcrossAllFourLevels()
    {
        for (var version = 1; version <= 40; version++)
        {
            var l = QrTables.GetEcBlockInfo(version, QrErrorCorrection.L).TotalCodewords;
            foreach (var level in Enum.GetValues<QrErrorCorrection>())
                Assert.Equal(l, QrTables.GetEcBlockInfo(version, level).TotalCodewords);
        }
    }

    [Fact]
    public void GetEcBlockInfo_version1M_matchesAnnexI()
    {
        // Annex I: version 1-M has 16 data codewords and 10 EC codewords in its single block.
        var info = QrTables.GetEcBlockInfo(1, QrErrorCorrection.M);
        Assert.Equal(16, info.TotalDataCodewords);
        Assert.Equal(10, info.EcCodewordsPerBlock);
        Assert.Equal(1, info.TotalBlocks);
    }

    [Theory]
    [InlineData(2, new[] { 6, 18 })]
    [InlineData(7, new[] { 6, 22, 38 })]
    [InlineData(40, new[] { 6, 30, 58, 86, 114, 142, 170 })]
    public void GetAlignmentCentres_matchesPublishedPositions(int version, int[] expected) =>
        Assert.Equal(expected, QrTables.GetAlignmentCentres(version));

    [Fact]
    public void GetAlignmentCentres_version1_isEmpty() => Assert.Empty(QrTables.GetAlignmentCentres(1));

    [Fact]
    public void AlphanumericCharset_has45Characters() => Assert.Equal(45, QrTables.AlphanumericCharset.Length);

    [Fact]
    public void ModeIndicator_kanji_isTable2Value() => Assert.Equal(0b1000, QrTables.ModeIndicator(QrSegmentMode.Kanji));

    [Theory]
    [InlineData(1, 8)]
    [InlineData(9, 8)]
    [InlineData(10, 10)]
    [InlineData(26, 10)]
    [InlineData(27, 12)]
    [InlineData(40, 12)]
    public void CharacterCountBits_kanji_matchesTable3(int version, int expectedBits) =>
        Assert.Equal(expectedBits, QrTables.CharacterCountBits(version, QrSegmentMode.Kanji));

    [Theory]
    [InlineData('0', 0)]
    [InlineData('9', 9)]
    [InlineData('A', 10)]
    [InlineData('Z', 35)]
    [InlineData(' ', 36)]
    [InlineData(':', 44)]
    [InlineData('a', -1)]
    public void AlphanumericValue_matchesTable5(char c, int expected) => Assert.Equal(expected, QrTables.AlphanumericValue(c));

    [Theory]
    [InlineData(1, MicroSymbolPlaceholder.M1, 0)]
    [InlineData(2, MicroSymbolPlaceholder.L, 1)]
    [InlineData(2, MicroSymbolPlaceholder.M, 2)]
    [InlineData(3, MicroSymbolPlaceholder.L, 3)]
    [InlineData(3, MicroSymbolPlaceholder.M, 4)]
    [InlineData(4, MicroSymbolPlaceholder.L, 5)]
    [InlineData(4, MicroSymbolPlaceholder.M, 6)]
    [InlineData(4, MicroSymbolPlaceholder.Q, 7)]
    public void MicroSymbolNumber_matchesTable13(int microVersion, MicroSymbolPlaceholder levelPlaceholder, int expected)
    {
        var level = levelPlaceholder switch
        {
            MicroSymbolPlaceholder.M1 => QrErrorCorrection.L, // ignored for M1
            MicroSymbolPlaceholder.L => QrErrorCorrection.L,
            MicroSymbolPlaceholder.M => QrErrorCorrection.M,
            MicroSymbolPlaceholder.Q => QrErrorCorrection.Q,
            _ => throw new ArgumentOutOfRangeException(nameof(levelPlaceholder)),
        };
        Assert.Equal(expected, QrTables.MicroSymbolNumber(microVersion, level));
    }

    /// <summary>ISO/IEC 18004 Table 7: Micro QR data+EC codewords must total the same per-version bit count across every offered level.</summary>
    [Theory]
    [InlineData(2, 80)]
    [InlineData(3, 132)]
    [InlineData(4, 192)]
    public void GetMicroCapacity_totalBitsIsConstantAcrossLevelsForAVersion(int microVersion, int expectedTotalBits)
    {
        QrErrorCorrection[] levels = microVersion switch
        {
            2 or 3 => [QrErrorCorrection.L, QrErrorCorrection.M],
            4 => [QrErrorCorrection.L, QrErrorCorrection.M, QrErrorCorrection.Q],
            _ => throw new ArgumentOutOfRangeException(nameof(microVersion)),
        };

        foreach (var level in levels)
        {
            var capacity = QrTables.GetMicroCapacity(microVersion, level);
            var totalBits = capacity.DataBits + (capacity.EcCodewords * 8);
            Assert.Equal(expectedTotalBits, totalBits);
        }
    }

    [Fact]
    public void GetMicroCapacity_m2L_matchesAnnexI()
    {
        var capacity = QrTables.GetMicroCapacity(2, QrErrorCorrection.L);
        Assert.Equal(5, capacity.DataCodewords);
        Assert.Equal(5, capacity.EcCodewords);
        Assert.False(capacity.LastCodewordIsHalfWidth);
    }

    public enum MicroSymbolPlaceholder { M1, L, M, Q }
}
