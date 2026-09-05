// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Fonts;

/// <summary>Pins <see cref="AdobeGlyphList.TryMapToUnicode"/> against the AGL Specification's own
/// algorithm and this reader's stated departures from it.</summary>
public sealed class AdobeGlyphListTests
{
    [Theory]
    [InlineData("A", "A")]
    [InlineData("ffi", "ﬃ")]
    [InlineData("f.alt", "f")]
    [InlineData("uni0041.sc", "A")]
    [InlineData("Alpha", "Α")]
    public void TryMapToUnicode_singleResult(string name, string expected)
    {
        Assert.True(AdobeGlyphList.TryMapToUnicode(name, out var unicode));
        Assert.Equal(expected, unicode);
    }

    [Fact]
    public void TryMapToUnicode_f_f_i_composesThreeChars()
    {
        Assert.True(AdobeGlyphList.TryMapToUnicode("f_f_i", out var unicode));
        Assert.Equal("ffi", unicode);
    }

    [Fact]
    public void TryMapToUnicode_uni00660066_composesFf()
    {
        Assert.True(AdobeGlyphList.TryMapToUnicode("uni00660066", out var unicode));
        Assert.Equal("ff", unicode);
    }

    [Fact]
    public void TryMapToUnicode_u1F600_givesTheSurrogatePair()
    {
        Assert.True(AdobeGlyphList.TryMapToUnicode("u1F600", out var unicode));
        Assert.Equal(char.ConvertFromUtf32(0x1F600), unicode);
        Assert.Equal(2, unicode.Length);
    }

    [Theory]
    [InlineData("g12")]
    [InlineData("cid5")]
    [InlineData("uni004")] // short group
    [InlineData("uni00410")] // 5 digits
    [InlineData("uni0041x")]
    [InlineData("uniD800")] // surrogate
    [InlineData("u110000")] // past U+10FFFF
    [InlineData("uni00e9")] // lowercase hex
    [InlineData("a__b")]
    [InlineData("_a")]
    [InlineData("a_")]
    [InlineData(".notdef")]
    [InlineData("uni0000")]
    [InlineData("f_g_nonexistent")]
    public void TryMapToUnicode_rejectsTheseNames(string name)
    {
        Assert.False(AdobeGlyphList.TryMapToUnicode(name, out _));
    }

    [Fact]
    public void TryMapToUnicode_uni00E9_true_lowercaseHexFalse()
    {
        Assert.True(AdobeGlyphList.TryMapToUnicode("uni00E9", out var unicode));
        Assert.Equal("é", unicode);
        Assert.False(AdobeGlyphList.TryMapToUnicode("uni00e9", out _));
    }

    [Fact]
    public void TryMapToUnicode_lengthBoundary_uniGroups()
    {
        // 31 groups: 3 + 31*4 = 127 characters (accepted). 32 groups: 131 characters (rejected).
        var accepted = "uni" + string.Concat(Enumerable.Repeat("0041", 31));
        var rejected = "uni" + string.Concat(Enumerable.Repeat("0041", 32));
        Assert.Equal(127, accepted.Length);
        Assert.Equal(131, rejected.Length);
        Assert.True(AdobeGlyphList.TryMapToUnicode(accepted, out _));
        Assert.False(AdobeGlyphList.TryMapToUnicode(rejected, out _));
    }

    [Fact]
    public void TryMapToUnicode_lengthBoundary_underscoreChain()
    {
        // 64 single-character components joined by 63 underscores: 64 + 63 = 127 characters.
        // 65 components: 65 + 64 = 129 characters.
        var accepted = string.Join('_', Enumerable.Repeat("a", 64));
        var rejected = string.Join('_', Enumerable.Repeat("a", 65));
        Assert.Equal(127, accepted.Length);
        Assert.Equal(129, rejected.Length);
        Assert.True(AdobeGlyphList.TryMapToUnicode(accepted, out var unicode));
        Assert.Equal(64, unicode.Length);
        Assert.False(AdobeGlyphList.TryMapToUnicode(rejected, out _));
    }

    [Fact]
    public void ListSize_is4282()
    {
        Assert.Equal(4282, AdobeGlyphList.Count);
    }

    [Fact]
    public void MultiCodePointEntryCount_is81()
    {
        // Read the embedded resource directly (the same file AdobeGlyphList.Count loads from) to
        // count lines whose value carries more than one code point: a fact about the data file,
        // not about the class's own lookup algorithm.
        using var stream = typeof(AdobeGlyphList).Assembly.GetManifestResourceStream("AdobeGlyphList.txt")!;
        using var reader = new StreamReader(stream);
        var multiCodePoint = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var space = line.IndexOf(' ');
            if (space <= 0)
                continue;
            var codes = line[(space + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (codes.Length > 1)
                multiCodePoint++;
        }
        Assert.Equal(81, multiCodePoint);
    }
}
