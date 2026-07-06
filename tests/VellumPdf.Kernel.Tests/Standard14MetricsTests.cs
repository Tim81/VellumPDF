// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

public sealed class Standard14MetricsTests
{
    [Fact]
    public void Helvetica_space_is_278()
    {
        // Space (U+0020) in Helvetica: 278/1000 per AFM spec
        var w = Standard14Metrics.GetWidth(Standard14.Helvetica, ' ');
        Assert.Equal(278, w);
    }

    [Fact]
    public void Courier_allChars_are_600()
    {
        // Courier is monospaced — every glyph is 600 units
        foreach (var c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
        {
            var w = Standard14Metrics.GetWidth(Standard14.Courier, c);
            Assert.Equal(600, w);
        }
    }

    [Fact]
    public void MeasureString_returnsScaledWidth()
    {
        // "A" in Helvetica = 667/1000 pt at 1pt → at 12pt = 12*667/1000 = 8.004
        var w = Standard14Metrics.MeasureString(Standard14.Helvetica, "A", 12);
        Assert.Equal(12.0 * 667.0 / 1000.0, w, precision: 6);
    }

    // ── 0x80–0x9F punctuation widths (previously 0, now the AFM WX values) ──

    [Theory]
    [InlineData('•')] // bullet
    [InlineData('–')] // endash
    [InlineData('—')] // emdash
    [InlineData('…')] // ellipsis
    public void Helvetica_highPunctuation_hasNonZeroWidth(char c)
    {
        var w = Standard14Metrics.GetWidth(Standard14.Helvetica, c);
        Assert.True(w > 0, $"U+{(int)c:X4} returned width 0");
    }

    [Fact]
    public void Helvetica_bullet_matchesAfmValue()
    {
        // Adobe Core-14 AFM (Helvetica.afm): "C 183 ; WX 350 ; N bullet ;"
        var w = Standard14Metrics.GetWidth(Standard14.Helvetica, '•');
        Assert.Equal(350, w);
    }

    [Fact]
    public void HelveticaBold_oe_matchesAfmValue()
    {
        // Adobe Core-14 AFM (Helvetica-Bold.afm): "C 250 ; WX 944 ; N oe ;"
        var w = Standard14Metrics.GetWidth(Standard14.HelveticaBold, 'œ');
        Assert.Equal(944, w);
    }

    [Fact]
    public void TimesRoman_emdash_matchesAfmValue()
    {
        // Adobe Core-14 AFM (Times-Roman.afm): "C 208 ; WX 1000 ; N emdash ;"
        var w = Standard14Metrics.GetWidth(Standard14.TimesRoman, '—');
        Assert.Equal(1000, w);
    }

    [Fact]
    public void GetWidth_charOutsideWinAnsi_returnsZero()
    {
        var w = Standard14Metrics.GetWidth(Standard14.Helvetica, '★'); // U+2605, not in WinAnsi
        Assert.Equal(0, w);
    }

    // ── WinAnsi-mapped widths (previously StandardEncoding widths, now corrected) ──

    [Fact]
    public void TimesRoman_eacute_matchesAfmValue()
    {
        // Adobe Core-14 AFM (Times-Roman.afm): "N eacute ; WX 444". Previously read 278
        // (the StandardEncoding width of 'i', copy-pasted from the ASCII row).
        var w = Standard14Metrics.GetWidth(Standard14.TimesRoman, 'é');
        Assert.Equal(444, w);
    }

    [Fact]
    public void TimesRoman_AE_matchesAfmValue()
    {
        // Adobe Core-14 AFM (Times-Roman.afm): "N AE ; WX 889". Previously read 556.
        var w = Standard14Metrics.GetWidth(Standard14.TimesRoman, 'Æ');
        Assert.Equal(889, w);
    }

    [Fact]
    public void Helvetica_quotesingle_matchesAfmValue()
    {
        // Adobe Core-14 AFM (Helvetica.afm): "N quotesingle ; WX 191". Previously carried
        // the StandardEncoding quoteright width (222).
        var w = Standard14Metrics.GetWidth(Standard14.Helvetica, '\'');
        Assert.Equal(191, w);
    }

    [Fact]
    public void Helvetica_grave_matchesAfmValue()
    {
        // Adobe Core-14 AFM (Helvetica.afm): "N grave ; WX 333". Previously carried the
        // StandardEncoding quoteleft width (222).
        var w = Standard14Metrics.GetWidth(Standard14.Helvetica, '`');
        Assert.Equal(333, w);
    }

    [Theory]
    [InlineData(Standard14.Helvetica)]
    [InlineData(Standard14.HelveticaBold)]
    [InlineData(Standard14.TimesRoman)]
    [InlineData(Standard14.TimesBold)]
    [InlineData(Standard14.TimesItalic)]
    [InlineData(Standard14.TimesBoldItalic)]
    public void ProportionalFace_widthTable_covers224WinAnsiCodes(Standard14 font)
    {
        // Every proportional face must cover the full WinAnsi range (32-255); three Times
        // faces were previously two elements short, so 'þ' and 'ÿ' fell off the end and
        // measured 0 regardless of font.
        Assert.True(Standard14Metrics.GetWidth(font, 'a') > 0);
        Assert.True(Standard14Metrics.GetWidth(font, 'þ') > 0);
        Assert.True(Standard14Metrics.GetWidth(font, 'ÿ') > 0);
    }

    [Fact]
    public void TimesBold_thornAndYdieresis_areNonZero()
    {
        // TimesBold, TimesItalic, and TimesBoldItalic arrays were 222 elements instead of
        // 224, so codes 0xFE (þ) and 0xFF (ÿ) read past the end and measured 0.
        Assert.True(Standard14Metrics.GetWidth(Standard14.TimesBold, 'þ') > 0);
        Assert.True(Standard14Metrics.GetWidth(Standard14.TimesBold, 'ÿ') > 0);
    }
}
