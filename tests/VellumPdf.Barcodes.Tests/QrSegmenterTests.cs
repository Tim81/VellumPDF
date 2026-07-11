// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for <see cref="QrSegmenter"/>'s per-character dynamic program over numeric/alphanumeric/byte mode.</summary>
public sealed class QrSegmenterTests
{
    private static int HeaderBitsV1(QrSegmentMode mode) => 4 + mode switch
    {
        QrSegmentMode.Numeric => 10,
        QrSegmentMode.Alphanumeric => 9,
        QrSegmentMode.Byte => 8,
        QrSegmentMode.Kanji => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static IReadOnlyList<QrSegment> Segment(string content, bool allowAlphanumeric = true, bool allowByte = true, bool allowKanji = true) =>
        QrSegmenter.Segment(content, HeaderBitsV1, Encoding.Latin1, allowAlphanumeric, allowByte, allowKanji);

    // Kanji-mode tests need a byte-mode cost that reflects reality: QrEncoder only ever offers
    // Kanji mode on the plain-text path, which picks UTF-8 (not Latin-1) once any character falls
    // outside Latin-1. Latin-1's lossy best-fit fallback for non-Latin-1 runes would otherwise
    // under-price Byte mode here and mask the DP's actual mode choice.
    private static IReadOnlyList<QrSegment> SegmentUtf8(string content, bool allowAlphanumeric = true, bool allowKanji = true) =>
        QrSegmenter.Segment(content, HeaderBitsV1, Encoding.UTF8, allowAlphanumeric, allowByte: true, allowKanji);

    [Fact]
    public void Segment_pureDigits_isOneNumericSegment()
    {
        var segments = Segment("0123456789");
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Numeric, segment.Mode);
        Assert.Equal(10, segment.RuneCount);
    }

    [Fact]
    public void Segment_pureUppercaseLetters_isOneAlphanumericSegment()
    {
        var segments = Segment("HELLO WORLD");
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Alphanumeric, segment.Mode);
    }

    [Fact]
    public void Segment_lowercaseLetters_isOneByteSegment()
    {
        var segments = Segment("hello");
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Byte, segment.Mode);
    }

    [Fact]
    public void Segment_emptyString_isEmpty() => Assert.Empty(Segment(""));

    [Fact]
    public void Segment_shortDigitRunInsideLetters_staysInOneAlphanumericSegment()
    {
        // "AB12345CD": splitting off the 5 digits as their own numeric segment would need two
        // extra 13-bit headers (26 bits) to save only 11 bits of data (17 numeric vs 28
        // alphanumeric for those digits), so the optimum keeps everything in one segment.
        var segments = Segment("AB12345CD");
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Alphanumeric, segment.Mode);
    }

    [Fact]
    public void Segment_longDigitRunInsideLetters_splitsIntoThreeSegments()
    {
        // "AB" + 20 digits + "CD": here switching mode twice (26 header bits) saves far more than
        // that in data bits, so the DP should split alphanumeric/numeric/alphanumeric.
        const string content = "AB12345678901234567890CD";
        var segments = Segment(content);

        Assert.Equal(3, segments.Count);
        Assert.Equal(QrSegmentMode.Alphanumeric, segments[0].Mode);
        Assert.Equal("AB", content.Substring(segments[0].CharStart, segments[0].CharLength));
        Assert.Equal(QrSegmentMode.Numeric, segments[1].Mode);
        Assert.Equal(20, segments[1].RuneCount);
        Assert.Equal(QrSegmentMode.Alphanumeric, segments[2].Mode);
        Assert.Equal("CD", content.Substring(segments[2].CharStart, segments[2].CharLength));

        // The split must actually be cheaper than the single-segment alternative it beats.
        var splitBits = segments.Sum(s => HeaderBitsV1(s.Mode) + QrEncoder.SegmentDataBits(content, s, Encoding.Latin1));
        var singleSegment = new QrSegment(QrSegmentMode.Alphanumeric, 0, content.Length, content.Length);
        var singleBits = HeaderBitsV1(singleSegment.Mode) + QrEncoder.SegmentDataBits(content, singleSegment, Encoding.Latin1);
        Assert.True(splitBits < singleBits, $"split ({splitBits} bits) should beat one alphanumeric segment ({singleBits} bits).");
    }

    [Fact]
    public void Segment_disallowAlphanumeric_forcesLettersIntoByteMode()
    {
        var segments = Segment("AB", allowAlphanumeric: false);
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Byte, segment.Mode);
    }

    [Fact]
    public void Segment_disallowByte_andUnencodableCharacter_throwsFormatException() =>
        Assert.Throws<FormatException>(() => Segment("ab", allowAlphanumeric: false, allowByte: false));

    [Fact]
    public void Segment_disallowByte_digitsStillWork()
    {
        var segments = Segment("123", allowAlphanumeric: false, allowByte: false);
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Numeric, segment.Mode);
    }

    [Fact]
    public void Segment_surrogatePairEmoji_isOneByteSegmentCoveringBothChars()
    {
        var segments = Segment("😀");
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Byte, segment.Mode);
        Assert.Equal(1, segment.RuneCount);
        Assert.Equal(2, segment.CharLength); // one rune, two UTF-16 code units
    }

    // ── Kanji mode ───────────────────────────────────────────────────────

    [Fact]
    public void Segment_pureKanji_isOneKanjiSegment()
    {
        var segments = SegmentUtf8("点荷茗");
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Kanji, segment.Mode);
        Assert.Equal(3, segment.RuneCount);
    }

    [Fact]
    public void Segment_mixedLatinAndKanji_splitsIntoByteThenKanjiThenByte()
    {
        // "ABC" and "DEF" (not Shift-JIS Kanji code points, so Kanji is ineligible for them) are
        // cheaper in Byte mode; "点荷茗" in the middle is representable in Kanji or Byte mode.
        // One run of three characters is long enough for Kanji's per-character saving (24 UTF-8
        // bits vs. 13) to outweigh the extra 12-bit header a second mode switch costs: splitting
        // saves 3*(24-13) = 33 bits at a cost of one extra header (12 bits), a net win, so the
        // optimum is Byte/Kanji/Byte. (A single Kanji character wouldn't clear that bar; see
        // Segment_shortKanjiRun_isNotWorthASeparateSegment below.)
        var segments = SegmentUtf8("ABC点荷茗DEF", allowAlphanumeric: false);

        Assert.Equal(3, segments.Count);
        Assert.Equal(QrSegmentMode.Byte, segments[0].Mode);
        Assert.Equal(QrSegmentMode.Kanji, segments[1].Mode);
        Assert.Equal(3, segments[1].RuneCount);
        Assert.Equal(QrSegmentMode.Byte, segments[2].Mode);
    }

    [Fact]
    public void Segment_shortKanjiRun_isNotWorthASeparateSegment()
    {
        // A single Kanji character surrounded by Latin text doesn't clear the mode-switch bar:
        // splitting it into its own Kanji segment costs 24 header bits (one extra 12-bit header
        // on each side) to save only 11 data bits (24 UTF-8 bits vs. 13 Kanji bits), so the DP
        // keeps everything in one Byte-mode segment. This is a regression guard on the DP's cost
        // math, not just its mode-eligibility wiring.
        var segments = SegmentUtf8("ABC点DEF", allowAlphanumeric: false);
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Byte, segment.Mode);
    }

    [Fact]
    public void Segment_kanjiEligibleCharacter_disallowKanji_fallsBackToByteMode()
    {
        var segments = SegmentUtf8("点", allowKanji: false);
        var segment = Assert.Single(segments);
        Assert.Equal(QrSegmentMode.Byte, segment.Mode);
    }

    [Fact]
    public void Segment_nonJapaneseContent_isUnaffectedByKanjiModeBeingAvailable()
    {
        // Regression guard for widening the DP from 3 to 4 modes: content with no Kanji-eligible
        // characters must segment identically whether or not Kanji mode is offered.
        const string content = "AB12345678901234567890CD";
        Assert.Equal(Segment(content, allowKanji: true), Segment(content, allowKanji: false));
    }
}
