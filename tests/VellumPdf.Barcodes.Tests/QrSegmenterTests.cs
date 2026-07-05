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
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static IReadOnlyList<QrSegment> Segment(string content, bool allowAlphanumeric = true, bool allowByte = true) =>
        QrSegmenter.Segment(content, HeaderBitsV1, Encoding.Latin1, allowAlphanumeric, allowByte);

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
}
