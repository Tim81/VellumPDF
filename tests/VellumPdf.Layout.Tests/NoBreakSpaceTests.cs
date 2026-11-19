// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>U+202F NARROW NO-BREAK SPACE and U+2007 FIGURE SPACE are no-break spaces (UAX #14 class GL), so a paragraph does not wrap at them; each test sizes the page from the text's own measured width.</summary>
public sealed class NoBreakSpaceTests
{
    private const string Prefix = "pppp";
    private const string First = "AAAAAA";
    private const string Second = "BBBBBB";

    // The three white-space characters in line-breaking class GL.
    public static TheoryData<char> NoBreakSpaces() => ['\u00A0', '\u202F', '\u2007'];

    // White space that is a break point: tab, en space, em space, thin space.
    public static TheoryData<char> BreakingSpaces() => ['\t', '\u2002', '\u2003', '\u2009'];

    private static TextStyle Helvetica() =>
        new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    private static string Render(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    /// <summary>A page just wide enough for <c>AAAAAA{no-break space}BBBBBB</c> on its own, so the token fits a line by itself but not beside the prefix word.</summary>
    private static double ContentWidth(TextStyle style)
    {
        var width = style.MeasureString(First + '\u00A0' + Second) + 0.5;
        Assert.True(style.MeasureString(Prefix + " " + First) <= width);
        Assert.True(style.MeasureString(Prefix + " " + First + '\u00A0' + Second) > width);
        return width;
    }

    private static Document Page(double contentWidth) => new()
    {
        PageSize = new PdfRectangle(0, 0, contentWidth, 400),
        Margins = EdgeInsets.Zero,
    };

    // ── Standard-14: the lines are readable ──────────────────────────────────

    /// <summary>The token stays whole on its own line; Standard-14 draws U+202F and U+2007 as the no-break space (0xA0), because WinAnsiEncoding has no narrower one.</summary>
    [Theory]
    [MemberData(nameof(NoBreakSpaces))]
    public void Standard14_aNoBreakSpaceNeverBreaksTheLine(char space)
    {
        var style = Helvetica();
        using var doc = Page(ContentWidth(style));
        doc.Add(new Paragraph(Prefix + " " + First + space + Second, style));

        var lines = ContentStreamReadback.TextPlacements(Render(doc)).Select(p => p.Text).ToArray();

        Assert.Equal([Prefix, First + '\u00A0' + Second], lines);
    }

    /// <summary>The same geometry with white space that does break: the line ends at it.</summary>
    [Theory]
    [MemberData(nameof(BreakingSpaces))]
    public void Standard14_ordinaryWhiteSpaceStillBreaksTheLine(char space)
    {
        var style = Helvetica();
        using var doc = Page(ContentWidth(style));
        doc.Add(new Paragraph(Prefix + " " + First + space + Second, style));

        var lines = ContentStreamReadback.TextPlacements(Render(doc)).Select(p => p.Text).ToArray();

        Assert.Equal([Prefix + " " + First, Second], lines);
    }

    /// <summary>The French case, written as it would be.</summary>
    [Fact]
    public void Standard14_pourquoiWithANarrowNoBreakSpace_doesNotWrapBeforeTheQuestionMark()
    {
        var style = Helvetica();
        var token = "Pourquoi\u202F?";
        var width = style.MeasureString(token) + 0.5;
        using var doc = Page(width);
        doc.Add(new Paragraph("Dites " + token, style));

        var lines = ContentStreamReadback.TextPlacements(Render(doc)).Select(p => p.Text).ToArray();

        Assert.Equal(["Dites", "Pourquoi\u00A0?"], lines);
    }

    // ── U+202F and U+2007 are drawn as a space, not as '?' ───────────────────

    [Theory]
    [InlineData('\u202F')]
    [InlineData('\u2007')]
    public void Standard14_isDrawnAsASpace_andReportsNoEncodingWarning(char space)
    {
        var style = Helvetica();
        using var doc = Page(500);
        doc.Add(new Paragraph("a" + space + "b", style));

        var stream = Render(doc);

        Assert.Equal(["a\u00A0b"], ContentStreamReadback.TextPlacements(stream).Select(p => p.Text));
        Assert.Empty(doc.TextEncodingWarnings);
    }

    /// <summary>The substitution is for the two no-break spaces only; other missing characters still warn.</summary>
    [Fact]
    public void Standard14_aCharacterWinAnsiLacks_isStillDrawnAsQuestionMarkWithAWarning()
    {
        using var doc = Page(500);
        doc.Add(new Paragraph("aЖb", Helvetica()));

        var stream = Render(doc);

        Assert.Equal(["a?b"], ContentStreamReadback.TextPlacements(stream).Select(p => p.Text));
        Assert.Equal(0x416, Assert.Single(doc.TextEncodingWarnings).CodePoint);
    }

    /// <summary>A table cell draws these characters as a space, not as '?'.</summary>
    [Fact]
    public void Standard14_tableCell_drawsANarrowNoBreakSpaceAsASpace()
    {
        var style = Helvetica();
        using var doc = Page(500);
        var table = new TableElement { DefaultCellStyle = style };
        table.AddRow().AddCell("a\u202Fb");
        doc.Add(table);

        var stream = Render(doc);

        Assert.Contains("a\u00A0b", ContentStreamReadback.TextPlacements(stream).Select(p => p.Text));
        Assert.Empty(doc.TextEncodingWarnings);
    }

    // ── The list indent bound reads the same set ─────────────────────────────

    /// <summary><c>ListRenderer.WidestWord</c> must split words as the wrap does: 38 OrderedRoman items of "AAAA{space}AAAA" (the geometry of the U+00A0 test in OffPagePlacementTests), whose widths match because U+202F and U+2007 measure as U+00A0 in Standard-14.</summary>
    [Theory]
    [MemberData(nameof(NoBreakSpaces))]
    public void ListIndentBound_treatsEachNoBreakSpaceAsPartOfTheWord(char space)
    {
        var style = Helvetica();
        var text = "AAAA" + space + "AAAA";

        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 80, 6000),
            Margins = EdgeInsets.Zero,
        };
        var list = new ListElement(ListStyle.OrderedRoman) { DefaultStyle = style, Indent = 20 };
        for (var i = 1; i <= 38; i++)
            list.Add(new ListItem(text, style));
        doc.Add(list);

        var placements = ContentStreamReadback.TextPlacements(Render(doc));
        var whole = "AAAA\u00A0AAAA";

        Assert.Equal(38, placements.Count(p => p.Text == whole));
        Assert.DoesNotContain(placements, p => p.Text == "A");
        Assert.DoesNotContain(placements, p => p.Text == "AAAA\u00A0AAA");
    }

    // ── Justification does not stretch them ──────────────────────────────────

    /// <summary><c>Tw</c> acts on the space byte (32) alone, so the slack is divided over plain spaces only; the first line's end position shows it.</summary>
    [Theory]
    [MemberData(nameof(NoBreakSpaces))]
    public void Justified_aNoBreakSpaceIsNotCountedAsAStretchableSpace(char space)
    {
        var style = Helvetica();
        const double width = 120;
        using var doc = Page(width);
        doc.Add(new Paragraph("aa bb cc" + space + "dd ee ff gg hh ii jj kk ll mm nn oo", style)
        {
            Alignment = HorizontalAlignment.Justify,
        });

        var stream = Render(doc);
        var firstLine = ContentStreamReadback.TextPlacements(stream)[0].Text;
        var tw = double.Parse(
            Regex.Match(stream, @"([-\d.]+) Tw").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        var stretchable = firstLine.Count(c => c == ' ');

        Assert.Contains("cc\u00A0dd", firstLine, StringComparison.Ordinal);
        Assert.Equal(width, style.MeasureString(firstLine) + (stretchable * tw), 3);
    }

    // ── Embedded font: lines are compared by glyph count ─────────────────────

    private static readonly string DejaVu = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    /// <summary>The number of glyphs on each text line an embedded font drew, in order.</summary>
    private static int[] GlyphCountsPerLine(string stream) =>
        Regex.Matches(stream, @"(?m)^<([0-9A-Fa-f]+)> Tj$")
            .Select(m => m.Groups[1].Value.Length / 4)
            .ToArray();

    /// <summary>A font handle belongs to the document that embedded it, so each measurement and rendering embeds the font in its own document.</summary>
    private static double MeasureEmbedded(byte[] font, string text)
    {
        using var doc = new Document();
        var handle = doc.UseTrueTypeFont(font);
        return new TextStyle { FontRef = handle, FontSize = 10 }.MeasureString(text);
    }

    private static int[] EmbeddedLines(byte[] font, double width, string text)
    {
        using var doc = Page(width);
        var handle = doc.UseTrueTypeFont(font);
        doc.Add(new Paragraph(text, new TextStyle { FontRef = handle, FontSize = 10 }));
        return GlyphCountsPerLine(Render(doc));
    }

    /// <summary>With an embedded font the lines hold glyph ids, so lines are compared by length: each no-break space must break exactly where U+00A0 does, and a plain space must not.</summary>
    [Theory]
    [MemberData(nameof(NoBreakSpaces))]
    public void Embedded_aNoBreakSpaceBreaksTheLineWhereU00A0Does_andOrdinaryWhiteSpaceDoesNot(char space)
    {
        if (!File.Exists(DejaVu))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");
        var font = File.ReadAllBytes(DejaVu);

        // Wide enough for the token with whichever no-break space it carries, since the font gives
        // them different advances, and still short of the prefix beside the token.
        var width = new[] { '\u00A0', space }
            .Max(c => MeasureEmbedded(font, First + c + Second)) + 0.5;
        Assert.True(MeasureEmbedded(font, Prefix + " " + First) <= width);
        Assert.True(MeasureEmbedded(font, Prefix + " " + First + space + Second) > width);

        var withSpace = EmbeddedLines(font, width, Prefix + " " + First + ' ' + Second);
        var withNbsp = EmbeddedLines(font, width, Prefix + " " + First + '\u00A0' + Second);
        var withCandidate = EmbeddedLines(font, width, Prefix + " " + First + space + Second);

        Assert.Equal(withNbsp, withCandidate);
        Assert.NotEqual(withSpace, withCandidate);
        Assert.Equal([Prefix.Length, First.Length + 1 + Second.Length], withCandidate);
    }
}
