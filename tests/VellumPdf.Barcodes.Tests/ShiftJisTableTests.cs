// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="ShiftJisTable"/> against the three ISO/IEC 18004 §7.4.6 worked vectors
/// (one from each eligible Shift-JIS block) and a handful of ineligible code points.
/// </summary>
public sealed class ShiftJisTableTests
{
    [Theory]
    [InlineData(0x70B9, 0x935F)] // 点, block 1 (0x8140-0x9FFC)
    [InlineData(0x8377, 0x89D7)] // 荷, block 1
    [InlineData(0x8317, 0xE4AA)] // 茗, block 2 (0xE040-0xEBBF)
    public void TryGetShiftJis_workedVectors_returnsThePinnedShiftJisCode(int unicodeScalar, int expectedShiftJis)
    {
        Assert.True(ShiftJisTable.TryGetShiftJis(unicodeScalar, out var shiftJis));
        Assert.Equal(expectedShiftJis, shiftJis);
    }

    [Fact]
    public void TryGetShiftJis_hiragana_isEligible() =>
        Assert.True(ShiftJisTable.TryGetShiftJis(0x3041, out _)); // ぁ, a block-1 Shift-JIS code

    [Theory]
    [InlineData('A')]
    [InlineData('0')]
    public void TryGetShiftJis_asciiCharacter_isNotEligible(int unicodeScalar) =>
        Assert.False(ShiftJisTable.TryGetShiftJis(unicodeScalar, out _));

    [Fact]
    public void TryGetShiftJis_surrogateRangeCodePoint_isNotEligible() =>
        // No Shift-JIS X 0208 Kanji maps into the UTF-16 surrogate range, and 0xD800 is not a
        // valid Unicode scalar value on its own; this guards the binary search against that input.
        Assert.False(ShiftJisTable.TryGetShiftJis(0xD800, out _));

    [Fact]
    public void TryGetShiftJis_emojiCodePoint_isNotEligible() =>
        Assert.False(ShiftJisTable.TryGetShiftJis(0x1F600, out _)); // U+1F600 GRINNING FACE

    [Theory]
    [InlineData(0x8140, 0x3000)] // block 1 lower bound: IDEOGRAPHIC SPACE
    [InlineData(0x9FFC, 0x6ECC)] // block 1 upper bound
    [InlineData(0xE040, 0x6F3E)] // block 2 lower bound
    [InlineData(0xEAA4, 0x7199)] // block 2's highest mapped entry (0xEBBF itself is unassigned)
    public void TryGetShiftJis_blockBoundaryEntries_roundTrip(int shiftJis, int unicodeScalar)
    {
        Assert.True(ShiftJisTable.TryGetShiftJis(unicodeScalar, out var actual));
        Assert.Equal(shiftJis, actual);
    }

    // ── CP932 round-trip ambiguity (SHIFTJIS.TXT dual-mapped code points) ────

    [Theory]
    [InlineData(0x005C)] // REVERSE SOLIDUS: SHIFTJIS.TXT maps 0x815F here, but CP932 decodes 0x815F to U+FF3C
    [InlineData(0x00A2)] // CENT SIGN
    [InlineData(0x00A3)] // POUND SIGN
    [InlineData(0x00AC)] // NOT SIGN
    [InlineData(0x2212)] // MINUS SIGN
    [InlineData(0x301C)] // WAVE DASH
    [InlineData(0x2016)] // DOUBLE VERTICAL LINE
    public void TryGetShiftJis_cp932AmbiguousScalar_isNotEligible(int unicodeScalar) =>
        // These scalars also have a single-byte or otherwise-mapped Shift-JIS form; the obsolete
        // SHIFTJIS.TXT source assigns them a second, double-byte Kanji-block code that a CP932
        // decoder (what zxing-cpp and every mainstream reader use) maps back to a different
        // scalar. Encoding one of them in Kanji mode would decode to the wrong character, so the
        // generator drops any entry that does not round-trip through CP932.
        Assert.False(ShiftJisTable.TryGetShiftJis(unicodeScalar, out _));

    [Fact]
    public void TryGetShiftJis_ordinaryKanjiNearAmbiguousScalars_stillEligible()
    {
        // 点 (U+70B9 -> 0x935F) is an unrelated Kanji-block entry with no single-byte alias;
        // confirms the CP932 round-trip filter only removed the ambiguous scalars above, not
        // ordinary Kanji.
        Assert.True(ShiftJisTable.TryGetShiftJis(0x70B9, out var shiftJis));
        Assert.Equal(0x935F, shiftJis);
    }
}
