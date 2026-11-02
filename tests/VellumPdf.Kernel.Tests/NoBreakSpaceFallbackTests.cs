// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #562: Layout now keeps U+202F NARROW NO-BREAK SPACE and U+2007 FIGURE SPACE in a paragraph's text
/// instead of folding them into a plain space, so the Kernel has to be able to draw them. WinAnsiEncoding,
/// which the Standard-14 text fonts use, has U+00A0 but neither of those two, and a font loaded from a
/// file may lack their glyphs. Without a fallback the text would have been drawn as <c>?</c> (Standard-14)
/// or the missing-glyph box (embedded), where the folded text used to show a space.
///
/// The code points are written as numbers rather than as escapes or literals, so the test pins which
/// characters these are independently of the constants the code under test defines.
/// </summary>
public sealed class NoBreakSpaceFallbackTests
{
    private const int NoBreakSpace = 0x00A0;
    private const int NarrowNoBreakSpace = 0x202F;
    private const int FigureSpace = 0x2007;

    // ── The set ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Exactly three characters, and every one is white space to .NET. The set was derived by
    /// intersecting <c>char.IsWhiteSpace</c> with line-breaking class GL in Unicode 15.1's
    /// LineBreak.txt: U+180E is GL but not white space in .NET, and U+2060 and U+FEFF are class WJ.
    /// </summary>
    [Fact]
    public void IsNoBreakSpace_isExactlyTheThreeGlWhiteSpaceCharacters()
    {
        var members = Enumerable.Range(0, 0x10000)
            .Where(cp => NoBreakSpaces.IsNoBreakSpace((char)cp))
            .ToArray();

        Assert.Equal([NoBreakSpace, FigureSpace, NarrowNoBreakSpace], members);
        Assert.All(members, cp => Assert.True(char.IsWhiteSpace((char)cp)));
    }

    [Theory]
    [InlineData(0x0009)]   // tab
    [InlineData(0x0020)]   // space
    [InlineData(0x2002)]   // en space
    [InlineData(0x2003)]   // em space
    [InlineData(0x2009)]   // thin space
    [InlineData(0x3000)]   // ideographic space
    [InlineData(0x180E)]   // class GL, but not white space in .NET
    [InlineData(0x2060)]   // word joiner, class WJ
    [InlineData(0xFEFF)]   // class WJ
    public void IsNoBreakSpace_isFalseForBreakingWhiteSpaceAndForNonWhiteSpace(int codePoint)
    {
        Assert.False(NoBreakSpaces.IsNoBreakSpace((char)codePoint));
    }

    [Fact]
    public void NeedsSpaceFallback_isTheTwoCharactersWinAnsiLacks()
    {
        var members = Enumerable.Range(0, 0x110000).Where(NoBreakSpaces.NeedsSpaceFallback).ToArray();

        Assert.Equal([FigureSpace, NarrowNoBreakSpace], members);
    }

    // ── Standard-14: encoded as 0xA0, measured as 0xA0 ───────────────────────

    private static string ContentOf(Action<PdfCanvas, PdfDocument> draw, out IReadOnlyList<TextEncodingWarning> warnings)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(doc.UseFont(Standard14.Helvetica), 12);
        draw(canvas, doc);
        canvas.EndText();
        canvas.Finish();
        warnings = canvas.TextEncodingWarnings;
        return Encoding.Latin1.GetString(page.ContentBytes!);
    }

    [Theory]
    [InlineData(NarrowNoBreakSpace)]
    [InlineData(FigureSpace)]
    public void ShowText_encodesTheTwoSpacesAsU00A0_withNoWarning(int codePoint)
    {
        var text = "a" + (char)codePoint + "b";

        var content = ContentOf((c, _) => c.ShowText(text), out var warnings);

        Assert.Contains("(a" + (char)0xA0 + "b) Tj", content, StringComparison.Ordinal);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ShowText_aCharacterWinAnsiLacks_isStillQuestionMarkWithAWarning()
    {
        var content = ContentOf((c, _) => c.ShowText("a" + (char)0x0416 + "b"), out var warnings);

        Assert.Contains("(a?b) Tj", content, StringComparison.Ordinal);
        Assert.Equal(0x0416, Assert.Single(warnings).CodePoint);
    }

    /// <summary>
    /// What is measured is what is drawn. The fallback character is measured as U+00A0, and U+00A0 is
    /// the width of a space in all twelve text faces, so a no-break space never stretches or
    /// shrinks a line against a plain one.
    /// </summary>
    [Fact]
    public void Standard14Metrics_measuresTheTwoSpacesAsU00A0_whichIsTheWidthOfASpace()
    {
        var textFonts = Enum.GetValues<Standard14>()
            .Where(f => f is not (Standard14.Symbol or Standard14.ZapfDingbats))
            .ToArray();
        Assert.Equal(12, textFonts.Length);

        foreach (var font in textFonts)
        {
            var nbsp = Standard14Metrics.GetWidth(font, (char)NoBreakSpace);
            Assert.True(nbsp > 0, font.ToString());
            Assert.Equal(Standard14Metrics.GetWidth(font, ' '), nbsp);
            Assert.Equal(nbsp, Standard14Metrics.GetWidth(font, (char)NarrowNoBreakSpace));
            Assert.Equal(nbsp, Standard14Metrics.GetWidth(font, (char)FigureSpace));
        }
    }

    [Fact]
    public void Standard14Metrics_aCharacterWinAnsiLacks_stillMeasuresAsQuestionMark()
    {
        Assert.Equal(
            Standard14Metrics.GetWidth(Standard14.Helvetica, '?'),
            Standard14Metrics.GetWidth(Standard14.Helvetica, (char)0x0416));
    }

    // ── Embedded fonts: fall back to U+00A0, then U+0020, then .notdef ───────

    /// <summary>A pure lookup over a dictionary, so the fallback is tested without a font that lacks the glyphs.</summary>
    private static Func<int, ushort> Font(params (int CodePoint, ushort Glyph)[] glyphs)
    {
        var map = glyphs.ToDictionary(g => g.CodePoint, g => g.Glyph);
        return cp => map.TryGetValue(cp, out var gid) ? gid : (ushort)0;
    }

    [Theory]
    [InlineData(NarrowNoBreakSpace)]
    [InlineData(FigureSpace)]
    public void ResolveGlyph_aFontThatHasTheCharacter_keepsItsOwnGlyph(int codePoint)
    {
        var font = Font((codePoint, 41), (NoBreakSpace, 7), (0x20, 3));

        Assert.Equal(41, NoBreakSpaces.ResolveGlyph(codePoint, font));
    }

    [Theory]
    [InlineData(NarrowNoBreakSpace)]
    [InlineData(FigureSpace)]
    public void ResolveGlyph_aFontWithoutIt_fallsBackToU00A0(int codePoint)
    {
        var font = Font((NoBreakSpace, 7), (0x20, 3));

        Assert.Equal(7, NoBreakSpaces.ResolveGlyph(codePoint, font));
    }

    [Theory]
    [InlineData(NarrowNoBreakSpace)]
    [InlineData(FigureSpace)]
    public void ResolveGlyph_aFontWithoutItOrU00A0_fallsBackToSpace(int codePoint)
    {
        var font = Font((0x20, 3));

        Assert.Equal(3, NoBreakSpaces.ResolveGlyph(codePoint, font));
    }

    [Fact]
    public void ResolveGlyph_aFontWithNoSpacesAtAll_isNotdef()
    {
        Assert.Equal(0, NoBreakSpaces.ResolveGlyph(NarrowNoBreakSpace, Font((0x41, 9))));
    }

    /// <summary>The fallback is for the two spaces only. Every other missing character stays .notdef.</summary>
    [Theory]
    [InlineData(0x0416)]
    [InlineData(NoBreakSpace)]     // not in the fallback set: it is the fallback
    [InlineData(0x2009)]
    public void ResolveGlyph_anyOtherMissingCharacter_isNotdef(int codePoint)
    {
        // A font that has a space but neither U+00A0 nor the character asked for.
        Assert.Equal(0, NoBreakSpaces.ResolveGlyph(codePoint, Font((0x20, 3))));
    }

    private const string DejaVu = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";

    /// <summary>A real font with the glyphs keeps them: the fallback must not replace a glyph the font has.</summary>
    [Fact]
    public void EmbeddedFont_thatHasTheGlyphs_drawsTheirOwn()
    {
        if (!File.Exists(DejaVu))
            Assert.Skip("DejaVu Sans is needed for an embedded font.");
        using var doc = new PdfDocument();
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));

        var narrow = handle.GetGlyphId(NarrowNoBreakSpace);
        var figure = handle.GetGlyphId(FigureSpace);

        Assert.NotEqual(0, narrow);
        Assert.NotEqual(0, figure);
        Assert.NotEqual(handle.GetGlyphId(NoBreakSpace), narrow);
        Assert.NotEqual(handle.GetGlyphId(0x20), narrow);
    }
}
