// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Fonts;

/// <summary>Pins <see cref="ZapfDingbatsGlyphList.TryMap"/> against the bundled
/// <c>zapfdingbats.txt</c> data.</summary>
public sealed class ZapfDingbatsGlyphListTests
{
    [Theory]
    [InlineData("a1", "✁")]
    [InlineData("a89", "❨")]
    // U+27BE, not U+275E: the two differ by one hex digit, so a transcription slip would be
    // easy to miss.
    [InlineData("a191", "➾")]
    public void TryMap_pinnedEntries(string name, string expected)
    {
        Assert.True(ZapfDingbatsGlyphList.TryMap(name, out var unicode));
        Assert.Equal(expected, unicode);
    }

    [Fact]
    public void TryMap_space_false()
    {
        // The bundled zapfdingbats.txt itself has no "space" entry (every one of its 201 lines is
        // an "aNN" name); a font's own space code still gets U+0020 through
        // SimpleFontReader's fallback to AdobeGlyphList, which does list "space".
        Assert.False(ZapfDingbatsGlyphList.TryMap("space", out _));
    }

    [Fact]
    public void TryMap_unknownName_false()
    {
        Assert.False(ZapfDingbatsGlyphList.TryMap("a999", out _));
    }

    [Fact]
    public void TryMap_unknownName_outParamIsEmptyStringNotNull()
    {
        // The out parameter is declared non-nullable; an unknown name must not hand the caller a
        // null string through it.
        Assert.False(ZapfDingbatsGlyphList.TryMap("a999", out var unicode));
        Assert.Equal("", unicode);
    }

    [Fact]
    public void EntryCount_is201()
    {
        // Not 188: the committed ZapfDingbatsGlyphList.txt is the Adobe AGL repository's own
        // zapfdingbats.txt normalised verbatim, and that file maps all 202 ZapfDingbats.afm glyph
        // names except "space" (which needs no lookup), including the 14 names assigned to codes
        // 0x80 through 0x8D (a85 through a96, a205, a206), which SymbolFontMetrics' own remarks
        // describe as ZapfDingbats.afm-only and Annex D.6 does not document. Those 14 names carry
        // ordinary Unicode mappings in Adobe's own zapfdingbats.txt, not in the Adobe Glyph List
        // proper. Trimming the list to the 188 names Annex D.6 documents would leave 0x80 ("a89")
        // with no Unicode route at all, contradicting the KAT SimpleFontReaderTests pins for that
        // exact code.
        Assert.Equal(201, ZapfDingbatsGlyphList.Count);
    }
}
