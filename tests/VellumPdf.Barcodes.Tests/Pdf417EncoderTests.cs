// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Barcodes.Pdf417;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// End-to-end tests for <see cref="Pdf417Encoder"/> and <see cref="Pdf417Barcode"/>: row
/// indicators against ISO/IEC 15438's own worked example, error-correction-level resolution, and
/// overall matrix assembly.
/// </summary>
public sealed class Pdf417EncoderTests
{
    [Fact]
    public void GetMatrix_threeRowsThreeColumnsLevelOne_rowIndicatorsMatchUssSpecWorkedExample()
    {
        // ISO/IEC 15438 section 2.2.3's own worked example: "if a symbol has 3 rows, 3 columns,
        // and error correction level 1, the (L1, L2, L3) and (R1, R2, R3) are (0, 5, 2) and
        // (2, 0, 5) respectively." (1-indexed rows; row 0 here is the spec's row 1.)
        var barcode = new Pdf417Barcode(new byte[] { 65 }) { Columns = 3, Rows = 3, ErrorCorrectionLevel = 1 };
        var matrix = barcode.GetMatrix();

        Assert.Equal(3, matrix.Height);
        Assert.Equal(Pdf417Dimensions.WidthModules(3), matrix.Width);

        AssertRowIndicators(matrix, row: 0, cluster: 0, expectedLeft: 0, expectedRight: 2);
        AssertRowIndicators(matrix, row: 1, cluster: 3, expectedLeft: 5, expectedRight: 0);
        AssertRowIndicators(matrix, row: 2, cluster: 6, expectedLeft: 2, expectedRight: 5);
    }

    [Fact]
    public void GetMatrix_everyRow_startsWithStartPatternAndEndsWithStopPattern()
    {
        var matrix = new Pdf417Barcode("Hello, PDF417!").GetMatrix();
        for (var row = 0; row < matrix.Height; row++)
        {
            Assert.Equal(Pdf417Tables.StartPattern, ReadPattern(matrix, row, 0, Pdf417Tables.PatternModules));
            Assert.Equal(Pdf417Tables.StopPattern, ReadPattern(matrix, row, matrix.Width - Pdf417Tables.StopPatternModules, Pdf417Tables.StopPatternModules));
        }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(40, 2)]
    [InlineData(41, 3)]
    [InlineData(160, 3)]
    [InlineData(161, 4)]
    [InlineData(320, 4)]
    [InlineData(321, 5)]
    [InlineData(863, 5)]
    public void ResolveRecommendedLevel_matchesIsoRecommendedMinimumTable(int dataCodewords, int expectedLevel) =>
        Assert.Equal(expectedLevel, Pdf417Encoder.ResolveRecommendedLevel(dataCodewords));

    [Fact]
    public void ResolveRecommendedLevel_beyondTableCeiling_fallsBackToTheHighestLevelThatStillFits()
    {
        // Level 5's own ceiling (863) is already the largest any level above 0 can hold, since
        // higher levels reserve more codewords for error correction, not fewer. For 864-895 data
        // codewords, level 4 (ceiling 895) is the highest that still fits.
        Assert.Equal(4, Pdf417Encoder.ResolveRecommendedLevel(864));
        Assert.Equal(4, Pdf417Encoder.ResolveRecommendedLevel(895));
        Assert.Equal(3, Pdf417Encoder.ResolveRecommendedLevel(896));
        Assert.Equal(0, Pdf417Encoder.ResolveRecommendedLevel(925));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 32)]
    [InlineData(5, 64)]
    [InlineData(6, 128)]
    [InlineData(7, 256)]
    [InlineData(8, 512)]
    public void GetMatrix_explicitLevel_addsTwoToThePowerLevelPlusOneErrorCorrectionCodewords(int level, int expectedEcCodewords)
    {
        var barcode = new Pdf417Barcode("Hello") { ErrorCorrectionLevel = level };
        var matrix = barcode.GetMatrix();

        // Total codewords minus the data-region length (recovered from the length descriptor,
        // which is always the very first data codeword) equals the error-correction codeword count.
        var firstDataPattern = ReadPattern(matrix, 0, Pdf417Tables.PatternModules * 2, Pdf417Tables.PatternModules);
        var lengthDescriptor = FindCodewordValue(0, firstDataPattern);
        var columns = (matrix.Width - (Pdf417Tables.PatternModules * 3) - Pdf417Tables.StopPatternModules) / Pdf417Tables.PatternModules;
        var totalCodewords = matrix.Height * columns;
        Assert.Equal(expectedEcCodewords, totalCodewords - lengthDescriptor);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(9)]
    public void GetMatrix_errorCorrectionLevelOutsideRange_throwsArgumentException(int level) =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { ErrorCorrectionLevel = level }.GetMatrix());

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void GetMatrix_columnsOutsideRange_throwsArgumentException(int columns) =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { Columns = columns }.GetMatrix());

    [Theory]
    [InlineData(2)]
    [InlineData(91)]
    public void GetMatrix_rowsOutsideRange_throwsArgumentException(int rows) =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { Rows = rows }.GetMatrix());

    [Fact]
    public void GetMatrix_rowHeightBelowMinimum_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { RowHeight = 2.9 }.GetMatrix());

    [Fact]
    public void GetMatrix_preferredAspectRatioNotPositive_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => new Pdf417Barcode("x") { PreferredAspectRatio = 0 }.GetMatrix());

    [Fact]
    public void GetMatrix_contentOutsideLatin1_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new Pdf417Barcode("cafĀ").GetMatrix());

    [Fact]
    public void GetMatrix_contentTooLargeForForcedDimensions_throwsFormatException() =>
        Assert.Throws<FormatException>(() => new Pdf417Barcode(new string('x', 500)) { Columns = 1, Rows = 3 }.GetMatrix());

    [Fact]
    public void GetMatrix_isCachedAndDeterministic()
    {
        var barcode = new Pdf417Barcode("Determinism check");
        var a = barcode.GetMatrix();
        var b = barcode.GetMatrix();
        Assert.Same(a, b);

        var c = new Pdf417Barcode("Determinism check").GetMatrix();
        AssertMatricesEqual(a, c);
    }

    [Fact]
    public void GetMatrix_byteContent_usesByteCompactionEndToEnd()
    {
        var matrix = new Pdf417Barcode(new byte[] { 0, 1, 2, 255 }).GetMatrix();
        Assert.True(matrix.Width > 0);
        Assert.True(matrix.Height >= Pdf417Dimensions.MinRows);
    }

    [Fact]
    public void GetMatrix_compact_leftSideIdenticalToStandardAndRightSideReplacedByOneModuleStop()
    {
        // Same content, same forced dimensions and error-correction level: data codewords, the
        // symbol length descriptor and the Reed-Solomon check codewords are identical (ISO/IEC
        // 15438), so this proves compact rendering changes nothing but the right-hand columns.
        const string content = "Compact PDF417 matrix KAT: identical left side, one-module stop.";
        var standard = new Pdf417Barcode(content) { Columns = 8, Rows = 12, ErrorCorrectionLevel = 3 }.GetMatrix();
        var compact = new Pdf417Barcode(content) { Columns = 8, Rows = 12, ErrorCorrectionLevel = 3, Compact = true }.GetMatrix();

        Assert.Equal(Pdf417Dimensions.WidthModules(8, compact: true), compact.Width);
        Assert.Equal(standard.Height, compact.Height);

        // Left region: start pattern + left row indicator + all 8 data columns, unaffected by Compact.
        var leftRegionWidth = (8 + 2) * Pdf417Tables.PatternModules;
        for (var row = 0; row < standard.Height; row++)
            for (var x = 0; x < leftRegionWidth; x++)
                Assert.Equal(standard.IsDark(x, row), compact.IsDark(x, row));

        // Right side: no right row-indicator column, just a single dark module in place of the
        // 18-module stop pattern.
        Assert.Equal(leftRegionWidth + 1, compact.Width);
        for (var row = 0; row < compact.Height; row++)
            Assert.True(compact.IsDark(leftRegionWidth, row));
    }

    // ── Macro PDF417 (ISO/IEC 15438 Annex H) ─────────────────────────────────

    [Fact]
    public void MacroControlBlock_lastSegmentWithSegmentCount_matchesHandComputedCodewordStream()
    {
        // Segment index 3 -> zero-pad to 5 digits "00003", prepend a synthetic leading 1 (the
        // same convention Numeric Compaction uses), giving decimal 100003 -> base-900 (111, 103).
        // File id 42 -> a single codeword (0-899, confirmed against zxing-cpp's actual decoder,
        // see ZxingDecodeOracleTests, which reads file id codewords one at a time, formatting each
        // as a zero-padded 3-digit decimal, until it hits 923 or 922). Last segment -> segment
        // count defaults to the caller's part count (6 here) via MacroSet, so the control block
        // carries designator 1 (Numeric Compaction): "6" -> prepend a leading 1 -> "16" decimal ->
        // base-900 single digit 16 (same worked-example rule as
        // Pdf417HighLevelEncoderTests.EncodeText_numericVector). Terminated with 922.
        var info = new MacroSegmentInfo(SegmentIndex: 3, FileId: 42, IsLast: true, new MacroPdf417Options { SegmentCount = 6 });
        var codewords = MacroControlBlock.Build(info);
        Assert.Equal([928, 111, 103, 42, 923, 1, 16, 922], codewords);
    }

    [Fact]
    public void MacroControlBlock_nonLastSegment_omitsOptionalFieldsAndTerminator()
    {
        var info = new MacroSegmentInfo(SegmentIndex: 3, FileId: 42, IsLast: false, Options: null);
        var codewords = MacroControlBlock.Build(info);
        Assert.Equal([928, 111, 103, 42], codewords);
    }

    [Theory]
    [InlineData(0, 111, 100)]
    [InlineData(899, 112, 99)]
    [InlineData(900, 112, 100)]
    [InlineData(901, 112, 101)]
    [InlineData(99998, 222, 198)]
    public void MacroControlBlock_segmentIndex_encodesAsTwoBase900CodewordsWithLeadingOnePrepended(int segmentIndex, int expectedHi, int expectedLo)
    {
        // The leading-1 prepend (Numeric Compaction's own convention, see
        // Pdf417HighLevelEncoderTests.EncodeText_numericVector) is required, not cosmetic: a
        // plain index/900, index%900 split (no leading 1) decodes to a value zxing-cpp's real
        // decoder rejects with a format error whenever the reconstructed digit string doesn't
        // start with '1'. Confirmed empirically by rendering and decoding both encodings.
        var info = new MacroSegmentInfo(segmentIndex, FileId: 0, IsLast: false, Options: null);
        var codewords = MacroControlBlock.Build(info);
        Assert.Equal([928, expectedHi, expectedLo, 0], codewords);
    }

    [Fact]
    public void MacroSet_stampsEachSymbolWithItsSegmentIndexAndOnlyTheLastIsMarkedLast()
    {
        string[] parts = ["one", "two", "three"];
        var symbols = Pdf417Barcode.MacroSet(parts, fileId: 7);

        for (var i = 0; i < symbols.Count; i++)
        {
            var info = symbols[i].MacroSegmentInfo!.Value;
            Assert.Equal(i, info.SegmentIndex);
            Assert.Equal(7, info.FileId);
            Assert.Equal(i == parts.Length - 1, info.IsLast);
        }
    }

    [Fact]
    public void MacroSet_lastSegment_defaultsSegmentCountToPartCountWhenUnset()
    {
        string[] parts = ["a", "b", "c", "d"];
        var symbols = Pdf417Barcode.MacroSet(parts, fileId: 1);

        Assert.Equal(4, symbols[^1].MacroSegmentInfo!.Value.Options!.SegmentCount);
    }

    [Fact]
    public void MacroSet_zeroParts_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => Pdf417Barcode.MacroSet(Array.Empty<string>(), fileId: 0));

    [Fact]
    public void MacroSet_tooManyParts_throwsArgumentException()
    {
        var parts = new string[100000];
        Array.Fill(parts, "x");
        Assert.Throws<ArgumentException>(() => Pdf417Barcode.MacroSet(parts, fileId: 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(900)]
    public void MacroSet_fileIdOutsideRange_throwsArgumentException(int fileId) =>
        Assert.Throws<ArgumentException>(() => Pdf417Barcode.MacroSet(["a"], fileId));

    [Fact]
    public void MacroSet_getMatrix_needsMoreCapacityThanTheSameContentWithoutMacro()
    {
        // Force exactly the capacity the plain content needs at error-correction level 0 (the
        // smallest possible EC overhead: 2 codewords), so it fits with zero padding to spare.
        // The same forced dimensions can no longer hold the macro-stamped version, proving the
        // control block genuinely added data-region codewords in Pdf417Encoder.Encode rather
        // than being stamped on the barcode and ignored.
        const string content = "Macro PDF417 end-to-end check";
        var contentCodewords = Pdf417HighLevelEncoder.EncodeText(content).Count;
        var exactRows = contentCodewords + 1 + 2; // + symbol length descriptor + level-0 EC codewords

        var plain = new Pdf417Barcode(content) { Columns = 1, Rows = exactRows, ErrorCorrectionLevel = 0 }.GetMatrix();
        Assert.Equal(exactRows, plain.Height);

        var macroInfo = Pdf417Barcode.MacroSet([content], fileId: 5)[0].MacroSegmentInfo;
        var macro = new Pdf417Barcode(content) { Columns = 1, Rows = exactRows, ErrorCorrectionLevel = 0, MacroSegmentInfo = macroInfo };
        Assert.Throws<FormatException>(() => macro.GetMatrix());
    }

    [Fact]
    public void MacroControlBlock_timestamp_convertsToUnixEpochSeconds()
    {
        // 2024-01-01T00:00:00Z -> 1704067200 Unix epoch seconds, encoded as Numeric Compaction
        // (designator 2), the same conversion MacroPdf417Options.Timestamp documents.
        var timestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var info = new MacroSegmentInfo(SegmentIndex: 0, FileId: 0, IsLast: true, new MacroPdf417Options { Timestamp = timestamp });
        var codewords = MacroControlBlock.Build(info);

        var expectedValue = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var expectedNumericCodewords = Pdf417HighLevelEncoder.EncodeNumericValue(expectedValue);

        Assert.Equal(923, codewords[4]);
        Assert.Equal(2, codewords[5]); // designator 2 = timestamp
        Assert.Equal(expectedNumericCodewords, codewords.Skip(6).Take(expectedNumericCodewords.Count));
        Assert.Equal(922, codewords[^1]);
    }

    [Fact]
    public void MacroControlBlock_fileName_matchesHandComputedTextCompactionCodewords()
    {
        // Segment index 0, file id 0 -> [928, 111, 100, 0] (same base-900 conversion as the
        // segmentIndex theory above, index 0 -> 111, 100). "MEMO" is Text Compaction, entirely in
        // the Alpha sub-mode (M=12, E=4, M=12, O=14, ISO/IEC 15438 A=0..Z=25): pairs (12,4) ->
        // 12*30+4=364 and (12,14) -> 12*30+14=374 (ISO/IEC 15438 section 2.2.4.4's base-30
        // pairing). This pins designator 0 and confirms it routes through Text, not Numeric,
        // Compaction: a Sender/Addressee designator swap or a text-through-numeric bug would both
        // change codewords[5] or the value codewords that follow it.
        var info = new MacroSegmentInfo(SegmentIndex: 0, FileId: 0, IsLast: true, new MacroPdf417Options { FileName = "MEMO" });
        var codewords = MacroControlBlock.Build(info);
        Assert.Equal([928, 111, 100, 0, 923, 0, 364, 374, 922], codewords);
    }

    [Fact]
    public void MacroControlBlock_sender_matchesHandComputedTextCompactionCodewords()
    {
        // "ACME", Alpha sub-mode throughout (A=0, C=2, M=12, E=4): pairs (0,2) -> 2 and
        // (12,4) -> 364.
        var info = new MacroSegmentInfo(SegmentIndex: 0, FileId: 0, IsLast: true, new MacroPdf417Options { Sender = "ACME" });
        var codewords = MacroControlBlock.Build(info);
        Assert.Equal([928, 111, 100, 0, 923, 3, 2, 364, 922], codewords);
    }

    [Fact]
    public void MacroControlBlock_addressee_matchesHandComputedTextCompactionCodewords()
    {
        // "BOB", Alpha sub-mode (B=1, O=14, B=1), odd character count so a trailing 29 pads the
        // last pair (the same padding rule the "PDF417" worked example above documents): values
        // 1, 14, 1, 29 -> pairs (1,14) -> 44 and (1,29) -> 59.
        var info = new MacroSegmentInfo(SegmentIndex: 0, FileId: 0, IsLast: true, new MacroPdf417Options { Addressee = "BOB" });
        var codewords = MacroControlBlock.Build(info);
        Assert.Equal([928, 111, 100, 0, 923, 4, 44, 59, 922], codewords);
    }

    [Fact]
    public void MacroControlBlock_fileSize_matchesHandComputedNumericCompactionCodewords()
    {
        // 12345 -> prepend the synthetic leading 1 Numeric Compaction always uses -> decimal
        // 112345 -> base 900: 112345 = 124*900 + 745, so (124, 745).
        var info = new MacroSegmentInfo(SegmentIndex: 0, FileId: 0, IsLast: true, new MacroPdf417Options { FileSize = 12345L });
        var codewords = MacroControlBlock.Build(info);
        Assert.Equal([928, 111, 100, 0, 923, 5, 124, 745, 922], codewords);
    }

    [Fact]
    public void MacroControlBlock_checksum_matchesHandComputedNumericCompactionCodewords()
    {
        // 500 -> prepend the leading 1 -> decimal 1500 -> base 900: 1500 = 1*900 + 600, so (1, 600).
        var info = new MacroSegmentInfo(SegmentIndex: 0, FileId: 0, IsLast: true, new MacroPdf417Options { Checksum = 500 });
        var codewords = MacroControlBlock.Build(info);
        Assert.Equal([928, 111, 100, 0, 923, 6, 1, 600, 922], codewords);
    }

    [Fact]
    public void MacroSet_timestampBeforeUnixEpoch_throwsArgumentOutOfRangeException()
    {
        var options = new MacroPdf417Options { Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(-1) };
        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf417Barcode.MacroSet(["a"], fileId: 0, options));
    }

    [Fact]
    public void MacroSet_negativeFileSize_throwsArgumentOutOfRangeException()
    {
        var options = new MacroPdf417Options { FileSize = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf417Barcode.MacroSet(["a"], fileId: 0, options));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void MacroSet_checksumOutsideRange_throwsArgumentOutOfRangeException(int checksum)
    {
        var options = new MacroPdf417Options { Checksum = checksum };
        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf417Barcode.MacroSet(["a"], fileId: 0, options));
    }

    [Fact]
    public void MacroSet_negativeSegmentCount_throwsArgumentOutOfRangeException()
    {
        var options = new MacroPdf417Options { SegmentCount = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf417Barcode.MacroSet(["a"], fileId: 0, options));
    }

    [Fact]
    public void MacroSet_autoSplit_dividesContentIntoRoughlyEqualParts()
    {
        var symbols = Pdf417Barcode.MacroSet("ABCDEFGHIJ", symbolCount: 3, fileId: 1);
        Assert.Equal(3, symbols.Count);
        // 10 runes over 3 parts: base size 3, remainder 1 -> the first part gets the extra rune.
        Assert.Equal("ABCD", symbols[0].Text);
        Assert.Equal("EFG", symbols[1].Text);
        Assert.Equal("HIJ", symbols[2].Text);
    }

    [Fact]
    public void MacroSet_autoSplit_stampsSegmentIndexAndSharedFileId()
    {
        var symbols = Pdf417Barcode.MacroSet("ABCDEF", symbolCount: 2, fileId: 9);
        Assert.Equal(0, symbols[0].MacroSegmentInfo!.Value.SegmentIndex);
        Assert.Equal(1, symbols[1].MacroSegmentInfo!.Value.SegmentIndex);
        Assert.All(symbols, s => Assert.Equal(9, s.MacroSegmentInfo!.Value.FileId));
    }

    [Fact]
    public void MacroSet_autoSplit_symbolCountOutOfRange_throwsArgumentException() =>
        Assert.Throws<ArgumentException>(() => Pdf417Barcode.MacroSet("hello", symbolCount: 0, fileId: 0));

    [Fact]
    public void MacroSet_autoSplit_unpairedSurrogate_throwsFormatException() =>
        Assert.Throws<FormatException>(() => Pdf417Barcode.MacroSet("\uD800", symbolCount: 2, fileId: 0));

    private static void AssertRowIndicators(BarcodeMatrix matrix, int row, int cluster, int expectedLeft, int expectedRight)
    {
        var leftPattern = ReadPattern(matrix, row, Pdf417Tables.PatternModules, Pdf417Tables.PatternModules);
        Assert.Equal(Pdf417Tables.GetPattern(cluster, expectedLeft), leftPattern);

        var rightStart = matrix.Width - Pdf417Tables.StopPatternModules - Pdf417Tables.PatternModules;
        var rightPattern = ReadPattern(matrix, row, rightStart, Pdf417Tables.PatternModules);
        Assert.Equal(Pdf417Tables.GetPattern(cluster, expectedRight), rightPattern);
    }

    private static uint ReadPattern(BarcodeMatrix matrix, int row, int startX, int moduleCount)
    {
        var pattern = 0u;
        for (var m = 0; m < moduleCount; m++)
            pattern = (pattern << 1) | (matrix.IsDark(startX + m, row) ? 1u : 0u);
        return pattern;
    }

    private static int FindCodewordValue(int cluster, uint pattern)
    {
        var patterns = Pdf417Tables.GetClusterPatterns(cluster);
        for (var i = 0; i < patterns.Length; i++)
            if (patterns[i] == pattern) return i;
        throw new InvalidOperationException("Pattern not found in cluster 0.");
    }

    private static void AssertMatricesEqual(BarcodeMatrix a, BarcodeMatrix b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                Assert.Equal(a.IsDark(x, y), b.IsDark(x, y));
    }
}
