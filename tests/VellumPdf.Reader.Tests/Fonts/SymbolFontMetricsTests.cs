// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Fonts;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Fonts;

/// <summary>
/// Pins <see cref="SymbolFontMetrics"/>' generated widths against the AFM files (every number
/// here was read from the AFM file directly, via <c>grep N &lt;name&gt; ;</c>, not from the
/// generated file), for the two symbolic fonts and, through
/// <see cref="SymbolFontMetrics.TryGetTextFontWidths"/> and a live <see cref="SimpleFontReader"/>,
/// the twelve nonsymbolic text fonts' own name-keyed width tables.
/// </summary>
public sealed class SymbolFontMetricsTests
{
    [Fact]
    public void SymbolWidths_pinnedEntries()
    {
        Assert.Equal(631, SymbolFontMetrics.SymbolWidths["alpha"]);
        Assert.Equal(250, SymbolFontMetrics.SymbolWidths["space"]);
        Assert.Equal(190, SymbolFontMetrics.SymbolWidths.Count);
        Assert.True(SymbolFontMetrics.SymbolWidths.ContainsKey("apple"));
    }

    [Fact]
    public void ZapfDingbatsWidths_pinnedEntries()
    {
        Assert.Equal(974, SymbolFontMetrics.ZapfDingbatsWidths["a1"]);
        Assert.Equal(278, SymbolFontMetrics.ZapfDingbatsWidths["space"]);
        Assert.Equal(390, SymbolFontMetrics.ZapfDingbatsWidths["a89"]);
        Assert.Equal(918, SymbolFontMetrics.ZapfDingbatsWidths["a191"]);
        Assert.Equal(202, SymbolFontMetrics.ZapfDingbatsWidths.Count);
    }

    // ── Text-font width tables: name-keyed, no WinAnsi dependency ───────────────────────────────
    // "lslash" and "fraction" have no WinAnsi code point; each font's own AFM WX for both, plus
    // for "A" (inside WinAnsi), pins that the lookup is by glyph name, so a name outside WinAnsi
    // cannot fall back to another glyph's width.

    [Theory]
    [InlineData("Helvetica", "A", 667)]
    [InlineData("Helvetica", "fraction", 167)]
    [InlineData("Helvetica", "lslash", 222)]
    [InlineData("Helvetica-Bold", "A", 722)]
    [InlineData("Helvetica-Bold", "fraction", 167)]
    [InlineData("Helvetica-Bold", "lslash", 278)]
    [InlineData("Helvetica-Oblique", "A", 667)]
    [InlineData("Helvetica-Oblique", "fraction", 167)]
    [InlineData("Helvetica-Oblique", "lslash", 222)]
    [InlineData("Helvetica-BoldOblique", "A", 722)]
    [InlineData("Helvetica-BoldOblique", "fraction", 167)]
    [InlineData("Helvetica-BoldOblique", "lslash", 278)]
    [InlineData("Times-Roman", "A", 722)]
    [InlineData("Times-Roman", "fraction", 167)]
    [InlineData("Times-Roman", "lslash", 278)]
    [InlineData("Times-Bold", "A", 722)]
    [InlineData("Times-Bold", "fraction", 167)]
    [InlineData("Times-Bold", "lslash", 278)]
    [InlineData("Times-Italic", "A", 611)]
    [InlineData("Times-Italic", "fraction", 167)]
    [InlineData("Times-Italic", "lslash", 278)]
    [InlineData("Times-BoldItalic", "A", 667)]
    [InlineData("Times-BoldItalic", "fraction", 167)]
    [InlineData("Times-BoldItalic", "lslash", 278)]
    [InlineData("Courier", "A", 600)]
    [InlineData("Courier", "fraction", 600)]
    [InlineData("Courier", "lslash", 600)]
    [InlineData("Courier-Bold", "A", 600)]
    [InlineData("Courier-Bold", "fraction", 600)]
    [InlineData("Courier-Bold", "lslash", 600)]
    [InlineData("Courier-Oblique", "A", 600)]
    [InlineData("Courier-Oblique", "fraction", 600)]
    [InlineData("Courier-Oblique", "lslash", 600)]
    [InlineData("Courier-BoldOblique", "A", 600)]
    [InlineData("Courier-BoldOblique", "fraction", 600)]
    [InlineData("Courier-BoldOblique", "lslash", 600)]
    public void TextFontWidths_pinnedAgainstAfm(string afmName, string glyphName, int width)
    {
        Assert.True(SymbolFontMetrics.TryGetTextFontWidths(afmName, out var widths));
        Assert.Equal(width, widths[glyphName]);
    }

    [Fact]
    public void TextFontWidths_unknownAfmName_returnsFalse()
    {
        Assert.False(SymbolFontMetrics.TryGetTextFontWidths("Symbol", out _));
        Assert.False(SymbolFontMetrics.TryGetTextFontWidths("Helvetica-Narrow", out _));
    }

    // ── Standard 14 width route, through SimpleFontReader ───────────────────────────────────────

    private static PdfDictionary FontDict(string baseFont, PdfArray? differences = null)
    {
        var dict = new PdfDictionary()
            .Set(PdfName.Subtype, "Type1")
            .Set(PdfName.BaseFont, baseFont);
        if (differences is not null)
        {
            var encoding = new PdfDictionary().Set(new PdfName("Differences"), differences);
            dict.Set(PdfName.Encoding, encoding);
        }
        return dict;
    }

    private static PdfFontReader Build(PdfDictionary fontDict)
    {
        using var doc = FontTestSupport.OpenMinimal();
        var sink = new DiagnosticSink(cap: 50);
        return SimpleFontReader.Create(doc, fontDict, objectNumber: null, generation: null, sink, pageIndex: null);
    }

    private static double WidthOf(PdfFontReader reader, byte code)
    {
        ReadOnlySpan<byte> bytes = [code];
        var offset = 0;
        Assert.True(reader.TryDecodeNext(bytes, ref offset, out var glyph));
        return glyph.Width;
    }

    [Fact]
    public void Helvetica_codeA_width667()
    {
        var reader = Build(FontDict("Helvetica"));
        Assert.Equal(667, WidthOf(reader, 0x41));
    }

    [Fact]
    public void Helvetica_codeSpace_width278()
    {
        var reader = Build(FontDict("Helvetica"));
        Assert.Equal(278, WidthOf(reader, 0x20));
    }

    [Fact]
    public void FiOutsideWinAnsi_measuresAsItsOwnAfmWidth_500()
    {
        // "fi" has no WinAnsi code point; a code-point-keyed lookup would fall back to another
        // glyph's width (Helvetica's "?" is 556). The name-keyed table measures Helvetica.afm's
        // own "fi" at 500.
        var differences = new PdfArray().Add(new PdfInteger(65)).Add(new PdfName("fi"));
        var reader = Build(FontDict("Helvetica", differences));
        Assert.Equal(500, WidthOf(reader, 0x41));
    }

    [Fact]
    public void TimesRoman_codeA_width722()
    {
        var reader = Build(FontDict("Times-Roman"));
        Assert.Equal(722, WidthOf(reader, 0x41));
    }

    [Fact]
    public void Courier_anyCode_width600()
    {
        var reader = Build(FontDict("Courier"));
        Assert.Equal(600, WidthOf(reader, 0x41));
        Assert.Equal(600, WidthOf(reader, 0x7A));
    }

    // ── Transcription cross-check: Reader WinAnsi vs Kernel WinAnsiEncoding ─────────────────────

    [Fact]
    public void WinAnsi_roundTripsThroughKernelEncoding_for216Codes()
    {
        var table = SimpleFontEncodings.WinAnsi;
        var checkedCount = 0;
        for (var code = 0x20; code <= 0xFF; code++)
        {
            if (code is 0x7F or 0x81 or 0x8D or 0x8F or 0x90 or 0x9D or 0xA0 or 0xAD)
                continue; // the six bullet fills, plus 0xA0/0xAD, are checked separately below.

            var name = table[code];
            Assert.NotNull(name);
            Assert.True(AdobeGlyphList.TryMapToUnicode(name!, out var unicode));
            Assert.Equal(1, unicode.Length);
            Assert.True(WinAnsiEncoding.TryGetByte(unicode[0], out var b));
            Assert.Equal((byte)code, b);
            checkedCount++;
        }
        Assert.Equal(216, checkedCount);
    }

    [Fact]
    public void WinAnsi_footnoteCodes_mapToTheirLiteralCodepoints()
    {
        Assert.True(AdobeGlyphList.TryMapToUnicode(SimpleFontEncodings.WinAnsi[0xA0]!, out var space));
        Assert.Equal(" ", space);
        Assert.True(AdobeGlyphList.TryMapToUnicode(SimpleFontEncodings.WinAnsi[0xAD]!, out var hyphen));
        Assert.Equal("-", hyphen);
    }
}
