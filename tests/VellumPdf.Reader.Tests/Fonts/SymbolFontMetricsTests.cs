// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Fonts;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Fonts;

/// <summary>
/// Pins <see cref="SymbolFontMetrics"/>' generated widths against the AFM files (every number
/// here was read from <c>Symbol.afm</c>/<c>ZapfDingbats.afm</c> directly, via
/// <c>grep N &lt;name&gt; ;</c>, not from the generated file or the Kernel table), and the
/// standard-14 width route (<see cref="Standard14Metrics.GetWidth"/>) through a live
/// <see cref="SimpleFontReader"/>.
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

    // ── Kernel width route, through a real SimpleFontReader ─────────────────────────────────────

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
    public void FiOutsideWinAnsi_measuresAsQuestionMark_556()
    {
        var differences = new PdfArray().Add(new PdfInteger(65)).Add(new PdfName("fi"));
        var reader = Build(FontDict("Helvetica", differences));
        Assert.Equal(556, WidthOf(reader, 0x41));
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
