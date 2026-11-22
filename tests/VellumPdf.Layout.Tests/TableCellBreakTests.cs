// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering.Table;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A tab, a line break or a form feed in a table cell or a running band no longer reaches glyph
/// lookup (#611, #613 item 34). A carriage return, a line feed and a pair of them each end a
/// line in a cell, and every other white-space control character is a space.
///
/// The fixtures with a short page are 300x400pt with 10pt margins and two 100pt columns, in
/// Helvetica 12pt: a line is 14.4pt and the default padding adds 8pt, so a cell of n lines is
/// 14.4 n + 8 pt tall. The wide fixtures are 2000x600pt in Helvetica 10pt, where a line is 12pt, a
/// cell of n lines is 12 n + 8 pt tall, and a page holds 580pt.
/// </summary>
public sealed class TableCellBreakTests : IDisposable
{
    private static readonly string DejaVu = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    private readonly string _tempDir;

    public TableCellBreakTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumcellbreak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static TextStyle Helvetica(double size) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = size,
    };

    private static Document ShortPage() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 400),
        Margins = new EdgeInsets(10),
    };

    private static Document WidePage() => new()
    {
        PageSize = new PdfRectangle(0, 0, 2000, 600),
        Margins = new EdgeInsets(10),
    };

    private static TableElement Table12() =>
        new TableElement { DefaultCellStyle = Helvetica(12) }.SetColumnWidths(100, 100);

    private static TableElement Wide10(params double[] widths) =>
        new TableElement { DefaultCellStyle = Helvetica(10) }.SetColumnWidths(widths);

    /// <summary>n two-letter words, joined by <paramref name="separator"/>.</summary>
    private static string Words(int n, string separator = "\n") =>
        string.Join(separator, Enumerable.Repeat("ab", n));

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Content(byte[] pdf) => PdfTestUtil.DecompressAllFlatStreams(pdf);

    private static int PageCount(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b").Count;

    private static readonly Regex Rect = new(
        @"(?<r>\S+ \S+ \S+ \S+) re\s+(?<op>[fS])\b", RegexOptions.CultureInvariant);

    /// <summary>Every cell border a page paints, as "x y w h".</summary>
    private static string[] Borders(string content) =>
        [.. Rect.Matches(content).Where(m => m.Groups["op"].Value == "S").Select(m => m.Groups["r"].Value)];

    private static double[] Heights(string content) =>
        [.. Borders(content).Select(b => double.Parse(b.Split(' ')[3], CultureInfo.InvariantCulture))];

    private static double[] Widths(string content) =>
        [.. Borders(content).Select(b => double.Parse(b.Split(' ')[2], CultureInfo.InvariantCulture))];

    private static List<ContentStreamReadback.TextPlacement> Placements(string content) =>
        [.. ContentStreamReadback.TextPlacements(content).Where(p => p.Text.Length > 0)];

    private static string[] Texts(string content) => [.. Placements(content).Select(p => p.Text)];

    private static List<string> HexShows(string content) =>
        [.. Regex.Matches(content, @"(?m)^<([0-9A-Fa-f]*)> Tj$").Select(m => m.Groups[1].Value.ToLowerInvariant())];

    // ── Standard 14: no control byte is written ──────────────────────────────

    /// <summary>
    /// A tab is one space, so the cell draws "A B" and not a string with a raw 0x09 in it. The row
    /// is one line tall.
    /// </summary>
    [Fact]
    public void Standard14_aTabIsOneSpace()
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell("A\tB").AddCell("x");
        doc.Add(t);

        var content = Content(Save(doc));

        Assert.Equal(["A B", "x"], Texts(content));
        Assert.Equal([22.4, 22.4], Heights(content).Select(h => Math.Round(h, 4)));
        Assert.DoesNotContain('\t', content);
    }

    /// <summary>
    /// A line feed breaks the line: two show strings, the second baseline one leading (14.4pt)
    /// lower, and a row of 2 x 14.4 + 8 = 36.8pt. No string holds the 0x0A byte or its escape.
    /// </summary>
    [Fact]
    public void Standard14_aLineFeedBreaksTheLine()
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell("line1\nline2").AddCell("x");
        doc.Add(t);

        var content = Content(Save(doc));
        var placements = Placements(content);

        Assert.Equal(["line1", "line2", "x"], placements.Select(p => p.Text));
        Assert.Equal(14.4, placements[0].Y - placements[1].Y, 4);
        Assert.Equal(placements[0].X, placements[1].X);
        Assert.Equal([36.8, 36.8], Heights(content).Select(h => Math.Round(h, 4)));
        AssertNoControlCharacter(placements);
    }

    /// <summary>A carriage return and line feed pair is one break, and a lone carriage return is one.</summary>
    [Theory]
    [InlineData("cr1\r\ncr2")]
    [InlineData("cr1\rcr2")]
    [InlineData("cr1\ncr2")]
    public void Standard14_everyLineBreakFormIsOneBreak(string text)
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell(text).AddCell("x");
        doc.Add(t);

        var content = Content(Save(doc));

        Assert.Equal(["cr1", "cr2", "x"], Texts(content));
        Assert.Equal(36.8, Heights(content)[0], 4);
        AssertNoControlCharacter(Placements(content));
    }

    /// <summary>
    /// A trailing break leaves a blank last line, and two breaks in a row a blank line between:
    /// "a\n" is two lines tall and "a\n\nb" three. A paragraph drops the trailing one.
    /// </summary>
    [Theory]
    [InlineData("a\n", 36.8, new[] { "a" })]
    [InlineData("a\n\nb", 51.2, new[] { "a", "b" })]
    [InlineData("\nb", 36.8, new[] { "b" })]
    public void Standard14_anEmptyHardLineIsABlankLine(string text, double height, string[] drawn)
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell(text).AddCell("x");
        doc.Add(t);

        var content = Content(Save(doc));

        Assert.Equal([.. drawn, "x"], Texts(content));
        Assert.Equal(height, Heights(content)[0], 4);
    }

    /// <summary>
    /// A form feed, a line tabulation and a next-line character are spaces: neither a break nor a
    /// raw control byte. The text between them stays one word run, so the cell is one line.
    /// </summary>
    [Fact]
    public void Standard14_aFormFeedIsOneSpace()
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell("cell\fx").AddCell("a\vb\u0085c\u2028d\u2029e");
        doc.Add(t);

        var content = Content(Save(doc));

        Assert.Equal(["cell x", "a b c d e"], Texts(content));
        Assert.Equal(22.4, Heights(content)[0], 4);
        Assert.DoesNotContain('\f', content);
    }

    /// <summary>
    /// A cell and a paragraph draw the same lines for one text that holds a tab, a form feed and
    /// each form of line break. They differ on a trailing break, which a cell keeps as a blank
    /// line, and on a run of spaces, which a paragraph collapses; the tests above and below pin
    /// the cell's side of both.
    /// </summary>
    [Fact]
    public void ACellAndAParagraphDrawOneMixedTextAsTheSameLines()
    {
        const string text = "x\tb\fc\r\nd\re\nf";

        using var cellDoc = ShortPage();
        var t = new TableElement { DefaultCellStyle = Helvetica(12) }.SetColumnWidths(250);
        t.AddRow().AddCell(text);
        cellDoc.Add(t);

        using var paragraphDoc = ShortPage();
        paragraphDoc.Add(new Paragraph(text, Helvetica(12)));

        var inCell = Texts(Content(Save(cellDoc)));
        var inParagraph = Texts(Content(Save(paragraphDoc)));

        Assert.Equal(["x b c", "d", "e", "f"], inParagraph);
        Assert.Equal(inParagraph, inCell);
    }

    /// <summary>
    /// A space separator that a font draws, such as U+2003, is left as written, so a cell that held
    /// one is drawn as it was: Helvetica has no glyph for it and the canvas writes a question
    /// mark, and the cell is still one line. The no-break spaces keep their meaning, and a run of
    /// spaces is kept as written.
    /// </summary>
    [Fact]
    public void Standard14_aSpaceSeparatorIsLeftAlone()
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell("a\u2003b").AddCell("c\u00A0d");
        t.AddRow().AddCell("j  k").AddCell("l");
        doc.Add(t);

        var content = Content(Save(doc));

        Assert.Equal(["a?b", "c\u00A0d", "j  k", "l"], Texts(content));
        Assert.Equal(22.4, Heights(content)[0], 4);
    }

    /// <summary>
    /// Every character that is white space, written alone into a cell between two letters, leaves
    /// no control character in a show string, and a control character that is not white space is
    /// not touched.
    /// </summary>
    [Fact]
    public void Standard14_noWhiteSpaceCharacterReachesTheFontAsAControlCharacter()
    {
        var whiteSpace = Enumerable.Range(0, 0x10000).Select(i => (char)i).Where(char.IsWhiteSpace).ToList();
        Assert.Contains('\t', whiteSpace);
        Assert.Contains('\f', whiteSpace);
        Assert.Contains('\u0085', whiteSpace);
        Assert.Contains('\u2028', whiteSpace);

        foreach (var c in whiteSpace)
        {
            using var doc = ShortPage();
            var t = Table12();
            t.AddRow().AddCell("x" + c + "y").AddCell("z");
            doc.Add(t);

            var placements = Placements(Content(Save(doc)));
            foreach (var p in placements)
                Assert.True(
                    p.Text.All(ch => ch >= 0x20),
                    $"U+{(int)c:X4} reached the font as a control character in \"{p.Text}\".");
        }
    }

    // ── Column sizing reads the same lines ───────────────────────────────────

    /// <summary>
    /// An automatic column is sized from the widest line, not from the whole text: two columns
    /// whose widest lines are the same word are the same width, though one of them holds a second
    /// line as well.
    /// </summary>
    [Fact]
    public void AnAutoColumn_isSizedFromItsWidestLine()
    {
        using var doc = ShortPage();
        var t = new TableElement { DefaultCellStyle = Helvetica(12) };
        t.AddRow().AddCell("aa\nbbbbbbbb").AddCell("bbbbbbbb");
        doc.Add(t);

        var widths = Widths(Content(Save(doc)));

        Assert.Equal(2, widths.Length);
        Assert.Equal(widths[0], widths[1], 6);
        Assert.Equal(140, widths[0], 6);
    }

    /// <summary>
    /// The minimum an automatic column is squeezed to is the widest word of any line, not of the
    /// whole text. On a page too narrow for both columns, two columns whose widest word is the
    /// same "bbbbbbbb" get the same width, though one cell holds a shorter first line.
    /// </summary>
    [Fact]
    public void AnAutoColumn_hasItsMinimumFromTheWordsOfEachLine()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 120, 400), Margins = new EdgeInsets(10) };
        var t = new TableElement { DefaultCellStyle = Helvetica(12) };
        t.AddRow().AddCell("aa\nbbbbbbbb").AddCell("bbbbbbbb");
        doc.Add(t);

        var widths = Widths(Content(Save(doc)));

        Assert.Equal(2, widths.Length);
        Assert.Equal(widths[0], widths[1], 6);
    }

    // ── Null text ────────────────────────────────────────────────────────────

    /// <summary>A null cell text with explicit widths still saves and draws nothing, as before.</summary>
    [Fact]
    public void ANullText_withExplicitWidths_drawsNothing()
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell(new Cell(null!)).AddCell("x");
        doc.Add(t);

        var content = Content(Save(doc));

        Assert.Equal(["x"], Texts(content));
    }

    /// <summary>A null cell text in an automatically sized column is still refused, naming <c>content</c>.</summary>
    [Fact]
    public void ANullText_inAnAutoColumn_isStillRefused()
    {
        using var doc = ShortPage();
        var t = new TableElement { DefaultCellStyle = Helvetica(12) };
        t.AddRow().AddCell(new Cell(null!)).AddCell("x");
        doc.Add(t);

        var ex = Assert.Throws<ArgumentNullException>(() => Save(doc));

        Assert.Equal("content", ex.ParamName);
    }

    // ── Embedded font: no glyph 0 ────────────────────────────────────────────

    private static string GlyphHex(EmbeddedFontHandle font, string text)
    {
        var ids = new ushort[text.Length];
        var n = font.GetGlyphIds(text, ids);
        return string.Concat(ids.Take(n).Select(id => id.ToString("x4", CultureInfo.InvariantCulture)));
    }

    private static Document EmbeddedDoc(out EmbeddedFontHandle font, PdfConformance conformance = PdfConformance.None)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 400),
            Margins = new EdgeInsets(10),
            Conformance = conformance,
        };
        doc.Info.Title = "Cell breaks";
        font = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        return doc;
    }

    private static void RequireDejaVu()
    {
        if (!File.Exists(DejaVu))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");
    }

    /// <summary>
    /// The three cells of the report, in an embedded font: each show string is the font's glyph ids
    /// for the text as a paragraph reads it, with glyph 3 (the space) where the tab was and one
    /// string per line, and the font's ToUnicode map has no entry for glyph 0 as U+0009, U+000A or
    /// U+000D.
    /// </summary>
    [Fact]
    public void Embedded_aTabAndALineBreakAreNotGlyphZero()
    {
        RequireDejaVu();
        using var doc = EmbeddedDoc(out var font, PdfConformance.PdfA2u);
        var t = new TableElement { DefaultCellStyle = new TextStyle { FontRef = font, FontSize = 12 } }
            .SetColumnWidths(100, 100);
        t.AddRow().AddCell("A\tB").AddCell("line1\nline2");
        t.AddRow().AddCell("cr1\r\ncr2").AddCell("tail");
        doc.Add(t);

        var content = Content(Save(doc));
        var shows = HexShows(content);

        Assert.Equal(
            [
                GlyphHex(font, "A B"),
                GlyphHex(font, "line1"),
                GlyphHex(font, "line2"),
                GlyphHex(font, "cr1"),
                GlyphHex(font, "cr2"),
                GlyphHex(font, "tail"),
            ],
            shows);
        Assert.Equal("0003", GlyphHex(font, "A B")[4..8]);
        foreach (var show in shows)
            for (var i = 0; i < show.Length; i += 4)
                Assert.NotEqual("0000", show.Substring(i, 4));

        Assert.DoesNotContain("<0000> <0009>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<0000> <000A>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<0000> <000D>", content, StringComparison.Ordinal);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.NotdefGlyph);
    }

    /// <summary>
    /// A white-space character, between two letters in an embedded font, never produces glyph 0
    /// unless it is a space separator the font has no glyph for, which is written as it always was.
    /// A control character that is not white space is not touched: U+0001 still reaches glyph 0.
    /// </summary>
    [Fact]
    public void Embedded_noControlWhiteSpaceIsGlyphZero_andOtherControlsStillAre()
    {
        RequireDejaVu();

        foreach (var c in Enumerable.Range(0, 0x10000).Select(i => (char)i).Where(char.IsWhiteSpace))
        {
            using var doc = EmbeddedDoc(out var font);
            var t = new TableElement { DefaultCellStyle = new TextStyle { FontRef = font, FontSize = 12 } }
                .SetColumnWidths(250);
            t.AddRow().AddCell("x" + c + "y");
            doc.Add(t);

            var shows = HexShows(Content(Save(doc)));

            if (char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator)
            {
                // Written as the font maps it, exactly as before this change.
                Assert.Equal([GlyphHex(font, "x" + c + "y")], shows);
            }
            else
            {
                // A line feed and a carriage return break the line; every other one is a space.
                var expected = c is '\n' or '\r'
                    ? new[] { GlyphHex(font, "x"), GlyphHex(font, "y") }
                    : [GlyphHex(font, "x y")];
                Assert.True(
                    shows.SequenceEqual(expected),
                    $"U+{(int)c:X4}: {string.Join(",", shows)} against {string.Join(",", expected)}.");
                Assert.DoesNotContain("0000", shows.SelectMany(s => Enumerable.Range(0, s.Length / 4).Select(i => s.Substring(i * 4, 4))));
            }
        }

        using var control = EmbeddedDoc(out var controlFont);
        var ct = new TableElement { DefaultCellStyle = new TextStyle { FontRef = controlFont, FontSize = 12 } }
            .SetColumnWidths(250);
        ct.AddRow().AddCell("x\u0001y");
        control.Add(ct);

        var controlShows = HexShows(Content(Save(control)));

        Assert.Equal([GlyphHex(controlFont, "x\u0001y")], controlShows);
        Assert.Contains("0000", Enumerable.Range(0, controlShows[0].Length / 4).Select(i => controlShows[0].Substring(i * 4, 4)));
    }

    /// <summary>
    /// The embedded file passes veraPDF at PDF/A-2u and PDF/A-2u preflight finds no glyph 0, after
    /// the fixture is asserted to hold the construct: a tab, a line feed and a carriage return line
    /// feed in cells, with the second lines drawn.
    /// </summary>
    [Fact]
    public void Embedded_theFileIsPdfA2uClean()
    {
        RequireDejaVu();
        using var doc = EmbeddedDoc(out var font, PdfConformance.PdfA2u);
        var t = new TableElement { DefaultCellStyle = new TextStyle { FontRef = font, FontSize = 12 } }
            .SetColumnWidths(100, 100);
        t.AddRow().AddCell("A\tB").AddCell("line1\nline2");
        t.AddRow().AddCell("cr1\r\ncr2").AddCell("end\ffeed");
        doc.Add(t);
        var bytes = Save(doc);
        var path = Path.Combine(_tempDir, "cells-a2u.pdf");
        File.WriteAllBytes(path, bytes);

        var shows = HexShows(Content(bytes));
        Assert.Contains(GlyphHex(font, "line2"), shows);
        Assert.Contains(GlyphHex(font, "cr2"), shows);
        Assert.Contains(GlyphHex(font, "end feed"), shows);

        ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
            out var vexit, out var report, out var verr, out var vtimed, timeoutMs: 120_000);
        Assert.False(vtimed);
        Assert.True(vexit is 0 or 1, $"veraPDF exited {vexit}\n{report}\n{verr}");
        Assert.True(report.Contains("isCompliant=\"true\"", StringComparison.Ordinal), report);

        var rules = VellumPdf.Conformance.PdfPreflight
            .Validate(bytes, VellumPdf.Conformance.PdfConformance.PdfA2U)
            .Assertions.Select(a => a.RuleId).ToList();
        Assert.DoesNotContain("ISO19005-2:6.2.11.8-notdef", rules);

        ExternalTool.TryRun("pdftotext", ["-layout", path, "-"], out var texit, out var text, out var terr, out _);
        Assert.True(texit == 0, terr);
        Assert.Contains("end feed", text, StringComparison.Ordinal);
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains(lines, l => l.Contains("line1", StringComparison.Ordinal) && !l.Contains("line2", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("line2", StringComparison.Ordinal));
    }

    // ── Running bands ────────────────────────────────────────────────────────

    /// <summary>
    /// A form feed in a running band's template is a space, as it is in a paragraph, and so is a
    /// tab or a line break: the band draws one line, and none of them reaches the font.
    /// </summary>
    [Theory]
    [InlineData("H\fX", "H X")]
    [InlineData("H\tX", "H X")]
    [InlineData("H\nX", "H X")]
    [InlineData("H\r\nX", "H  X")]
    [InlineData("H\u0085X", "H X")]
    [InlineData("H\u2028X", "H X")]
    [InlineData("H\u2029X", "H X")]
    public void ABandDrawsAControlSpaceAsASpace(string template, string drawn)
    {
        using var doc = ShortPage();
        doc.SetHeader(template, Helvetica(10));
        doc.Add(new Paragraph("body", Helvetica(10)));

        var content = Content(Save(doc));

        Assert.Contains(drawn, Texts(content));
        Assert.DoesNotContain('\f', content);
        Assert.DoesNotContain('\t', content);
    }

    /// <summary>
    /// The band's width is measured on the text it draws, so a template cut to fit is cut at the
    /// space a form feed became: only "aaa" fits beside the form feed's neighbour.
    /// </summary>
    [Fact]
    public void ABandCutsAtTheSpaceAFormFeedBecame()
    {
        var style = Helvetica(10);
        var width = style.MeasureString("aaa bb") - 0.1;
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, width + 20, 400),
            Margins = new EdgeInsets(10),
        };
        doc.SetHeader("aaa\fbbbb", style);
        doc.Add(new Paragraph("body", style));

        var content = Content(Save(doc));

        Assert.Contains("aaa", Texts(content));
        Assert.Single(doc.BandTruncations);
    }

    /// <summary>
    /// The text a page label prints reaches a band's <c>{page}</c> as a value, not as template text,
    /// so it is read the same way: a tab and a line break in the label prefix are spaces, in the
    /// drawing and in the width the band is cut to.
    /// </summary>
    [Fact]
    public void ABandDrawsAControlSpaceInAPageLabelAsASpace()
    {
        using var doc = ShortPage();
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal, "A\tB\n", 1);
        doc.SetFooter("p {page} of {pages}", Helvetica(10));
        doc.Add(new Paragraph("body", Helvetica(10)));

        var content = Content(Save(doc));

        Assert.Contains("p A B 1 of 1", Texts(content));
        AssertNoControlCharacter(Placements(content));
    }

    /// <summary>
    /// The same label in an embedded font under PDF/A-2u: no glyph 0, so veraPDF finds no
    /// clause 6.2.11.8 failure and the preflight no <c>notdef</c> assertion.
    /// </summary>
    [Fact]
    public void ABandWithAControlSpaceInAPageLabelIsPdfA2uClean()
    {
        RequireDejaVu();
        using var doc = EmbeddedDoc(out var font, PdfConformance.PdfA2u);
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal, "A\tB\n", 1);
        doc.SetFooter("p {page} of {pages}", new TextStyle { FontRef = font, FontSize = 10 });
        doc.Add(new Paragraph("body", new TextStyle { FontRef = font, FontSize = 10 }));
        var bytes = Save(doc);
        var path = Path.Combine(_tempDir, "label-a2u.pdf");
        File.WriteAllBytes(path, bytes);

        Assert.Contains(GlyphHex(font, "p A B 1 of 1"), HexShows(Content(bytes)));

        ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
            out var vexit, out var report, out var verr, out var vtimed, timeoutMs: 120_000);
        Assert.False(vtimed);
        Assert.True(vexit is 0 or 1, $"veraPDF exited {vexit}\n{report}\n{verr}");
        Assert.True(report.Contains("isCompliant=\"true\"", StringComparison.Ordinal), report);

        var rules = VellumPdf.Conformance.PdfPreflight
            .Validate(bytes, VellumPdf.Conformance.PdfConformance.PdfA2U)
            .Assertions.Select(a => a.RuleId).ToList();
        Assert.DoesNotContain("ISO19005-2:6.2.11.8-notdef", rules);
    }

    // ── Rows that cannot be cut ──────────────────────────────────────────────

    /// <summary>The diagnostic a cell that is laid out unbroken is reported with, or null.</summary>
    private static LayoutDiagnostic? CellReport(Document doc) =>
        doc.Diagnostics.SingleOrDefault(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);

    /// <summary>
    /// A header row whose breaks, honoured, leave no room under it for the row below: 2.3.3 drew
    /// the cell on one line. The cell is drawn as one line, without its breaks, and reported as a warning.
    /// A header of 46 lines is 560pt and leaves 20pt, which holds the 20pt row below, so its
    /// breaks are kept.
    /// </summary>
    [Theory]
    [InlineData(46, false)]
    [InlineData(47, true)]
    [InlineData(48, true)]
    [InlineData(100, true)]
    public void AHeaderThatWouldLeaveNoRoomForTheRowBelowIsLaidOutUnbroken(int n, bool asSpaces)
    {
        using var doc = WidePage();
        var t = Wide10(1900);
        t.AddRow(isHeader: true).AddCell(Words(n));
        t.AddRow().AddCell("x");
        doc.Add(t);

        var bytes = Save(doc);
        var texts = Texts(Content(bytes));
        var report = CellReport(doc);

        Assert.Equal(1, PageCount(bytes));
        if (asSpaces)
        {
            Assert.Equal([Words(n, ""), "x"], texts);
            Assert.NotNull(report);
            Assert.Equal(LayoutDiagnosticSeverity.Warning, report.Severity);
            Assert.Equal(1, report.Count);
            Assert.Contains("row 0, cell 0", report.Message, StringComparison.Ordinal);
            Assert.Equal(LayoutElementKind.Table, report.ElementKind);
        }
        else
        {
            Assert.Equal([.. Enumerable.Repeat("ab", n), "x"], texts);
            Assert.Null(report);
        }
    }

    /// <summary>
    /// A header row whose own height, honoured, is more than a page: the same fallback, and a
    /// table that 2.3.3 saved still saves with nothing refused.
    /// </summary>
    [Fact]
    public void AHeaderTallerThanAPageIsLaidOutUnbroken()
    {
        using var doc = WidePage();
        var t = Wide10(1900);
        t.AddRow(isHeader: true).AddCell(Words(100));
        t.AddRow().AddCell("x");
        doc.Add(t);

        var texts = Texts(Content(Save(doc)));

        Assert.Equal([Words(100, ""), "x"], texts);
        Assert.Equal(LayoutDiagnosticSeverity.Warning, CellReport(doc)!.Severity);
    }

    /// <summary>
    /// Rows joined by a RowSpan are never cut, so when their breaks, honoured, make the group taller
    /// than a page, the cells that hold them are laid out unbroken. A group of 46 lines plus a
    /// 20pt row is 580pt, which fits; 47 lines does not.
    /// </summary>
    [Theory]
    [InlineData(46, false)]
    [InlineData(47, true)]
    [InlineData(100, true)]
    public void ARowSpanGroupThatWouldBeTallerThanAPageIsLaidOutUnbroken(int n, bool asSpaces)
    {
        using var doc = WidePage();
        var t = Wide10(1500, 400);
        t.AddRow().AddCell(new Cell(Words(n)) { RowSpan = 2 }).AddCell("b");
        t.AddRow().AddCell("c");
        doc.Add(t);

        var bytes = Save(doc);
        var texts = Texts(Content(bytes));

        Assert.Equal(1, PageCount(bytes));
        if (asSpaces)
        {
            Assert.Contains(Words(n, ""), texts);
            Assert.NotNull(CellReport(doc));
        }
        else
        {
            Assert.Equal(n, texts.Count(x => x == "ab"));
            Assert.Null(CellReport(doc));
        }
    }

    /// <summary>
    /// Two rows that are each short enough, joined by a RowSpan, are too tall together: both
    /// cells that hold breaks are laid out unbroken, and each is reported. 23 lines in each row is
    /// 2 x 284 = 568pt, which fits; 24 lines is 592pt.
    /// </summary>
    [Theory]
    [InlineData(23, 0)]
    [InlineData(24, 2)]
    public void ARowSpanGroupOfTwoRowsLaysEachBreakingCellOutUnbroken(int n, int reported)
    {
        using var doc = WidePage();
        var t = Wide10(1000, 900);
        t.AddRow().AddCell(new Cell("a") { RowSpan = 2 }).AddCell(Words(n));
        t.AddRow().AddCell(Words(n));
        doc.Add(t);

        var bytes = Save(doc);

        Assert.Equal(1, PageCount(bytes));
        Assert.Equal(reported, doc.Diagnostics.Count(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped));
        if (reported > 0)
            Assert.Equal(2, Texts(Content(bytes)).Count(x => x == Words(n, "")));
    }

    /// <summary>
    /// A row that can be cut keeps its breaks however tall they make it: 48 lines are 584pt, which
    /// no page holds, so the row is cut between its lines and nothing is reported.
    /// </summary>
    [Fact]
    public void AnOrdinaryRowKeepsItsBreaksAndIsCut()
    {
        using var doc = WidePage();
        var t = Wide10(1900);
        t.AddRow().AddCell(Words(48));
        doc.Add(t);

        var bytes = Save(doc);

        Assert.Equal(2, PageCount(bytes));
        Assert.Equal(48, Texts(Content(bytes)).Count(x => x == "ab"));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// A header laid out unbroken is drawn the same way on every page, including the
    /// pages a cut row continues on: the long header string is shown once per page, each page
    /// reports it, and the report's count is the number of pages.
    /// </summary>
    [Fact]
    public void AHeaderLaidOutUnbrokenIsDrawnSoOnEveryPage()
    {
        using var doc = WidePage();
        var t = Wide10(1900);
        t.AddRow(isHeader: true).AddCell(Words(60));
        t.AddRow().AddCell(Words(100));
        doc.Add(t);

        var bytes = Save(doc);
        var texts = Texts(Content(bytes));
        var pages = PageCount(bytes);

        Assert.True(pages >= 2, "the row was not cut");
        Assert.Equal(pages, texts.Count(x => x == Words(60, "")));
        Assert.Equal(100, texts.Count(x => x == "ab"));
        Assert.Equal(pages, CellReport(doc)!.Count);
    }

    /// <summary>
    /// A cell laid out unbroken is measured and wrapped as a cell with no break is, so
    /// the table's rectangles are exactly those the same table had when a line feed was one more
    /// character of the line: the four borders below are what 2.3.3 drew for it, though 2.3.3 wrote
    /// the line feeds into the string.
    /// </summary>
    [Fact]
    public void AFallbackHeaderKeepsTheGeometryOfAnUnbrokenLine()
    {
        using var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica(10) };
        t.AddRow(isHeader: true).AddCell(Words(60)).AddCell("h");
        t.AddRow().AddCell("x").AddCell("y");
        doc.Add(t);

        var content = Content(Save(doc));

        Assert.NotNull(CellReport(doc));
        Assert.Equal(
            ["10 570 1930.09932 20", "1940.09932 570 49.90068 20", "10 550 1930.09932 20", "1940.09932 550 49.90068 20"],
            Borders(content));
        Assert.Equal([Words(60, ""), "h", "x", "y"], Texts(content));
    }

    /// <summary>
    /// A header whose breaks, honoured, take the room a RowSpan group needs under it: the group has
    /// no break of its own to give up, so the header cell is laid out unbroken. The header
    /// of 30 lines is 368pt and the group 268pt, which do not fit 580pt together; with the header
    /// on one line they do.
    /// </summary>
    [Fact]
    public void AHeaderThatTakesTheRoomOfARowSpanGroupIsLaidOutUnbroken()
    {
        using var doc = WidePage();
        var t = Wide10(30, 1870);
        t.AddRow(isHeader: true).AddCell("h").AddCell(Words(30));
        t.AddRow().AddCell(new Cell(Words(20, " ")) { RowSpan = 2 }).AddCell("b");
        t.AddRow().AddCell("c");
        doc.Add(t);

        var bytes = Save(doc);
        var texts = Texts(Content(bytes));
        var report = CellReport(doc);

        Assert.Equal(1, PageCount(bytes));
        Assert.Contains(Words(30, ""), texts);
        Assert.Equal(20, texts.Count(x => x == "ab"));
        Assert.NotNull(report);
        Assert.Contains("row 0, cell 1", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tab is one space wide where the raw character was measured at its own width, which can
    /// wrap a cell one line taller and push a RowSpan group over the page. "ab" and a tab and "ab"
    /// fits a 36pt column on one line as one word, and as two words does not; the group of 46 plain
    /// lines under it is exactly 580pt with the one line and 592pt with two. The cell is laid out
    /// as it was measured before, with the tab left out of the drawing and the report naming the
    /// cell. The borders are 2.3.3's.
    /// </summary>
    [Fact]
    public void ATabThatWouldTipARowSpanGroupOverThePageIsLaidOutUnbroken()
    {
        using var doc = WidePage();
        var t = Wide10(36, 36);
        t.AddRow().AddCell(new Cell("a") { RowSpan = 2 }).AddCell("ab\tab");
        t.AddRow().AddCell(Words(46, " "));
        doc.Add(t);

        var bytes = Save(doc);
        var content = Content(bytes);

        Assert.Equal(1, PageCount(bytes));
        Assert.Equal(["10 10 36 580", "46 570 36 20", "46 10 36 560"], Borders(content));
        Assert.Contains("abab", Texts(content));
        Assert.Contains("row 0, cell 1", CellReport(doc)!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A renderer laid out without the document, through a context built with the public
    /// constructor, knows no page: it keeps every break, and a header that is then too tall is
    /// the same refusal as before. The document supplies the page and lays the cell out unbroken.
    /// </summary>
    [Fact]
    public void WithoutThePageTheBreaksAreKept()
    {
        var t = Wide10(1900);
        t.AddRow(isHeader: true).AddCell(Words(60));
        t.AddRow().AddCell("x");

        var result = new TableRenderer(t).Layout(new LayoutContext(new LayoutBox(10, 10, 1980, 580)));

        Assert.Equal(LayoutResult.Outcome.Nothing, result.Status);
    }

    /// <summary>
    /// Strict diagnostics saves a table whose cell is laid out unbroken: the report is a warning,
    /// because the text stays and only the separation of its words is lost.
    /// </summary>
    [Fact]
    public void StrictDiagnosticsSavesTheWarning()
    {
        using var doc = WidePage();
        doc.StrictDiagnostics = true;
        var t = Wide10(1900);
        t.AddRow(isHeader: true).AddCell(Words(60));
        t.AddRow().AddCell("x");
        doc.Add(t);

        var bytes = Save(doc);

        Assert.Equal(1, PageCount(bytes));
        Assert.Equal(LayoutDiagnosticSeverity.Warning, CellReport(doc)!.Severity);
    }

    /// <summary>
    /// A tab in a header cell that fits a page does not send the table to the fallback: the tab is
    /// one space and nothing is reported.
    /// </summary>
    [Fact]
    public void ATabInAHeaderThatFitsIsOneSpaceAndIsNotReported()
    {
        using var doc = WidePage();
        var t = Wide10(1900);
        t.AddRow(isHeader: true).AddCell(Words(60, "\t"));
        t.AddRow().AddCell("x");
        doc.Add(t);

        var texts = Texts(Content(Save(doc)));

        Assert.Equal([Words(60, " "), "x"], texts);
        Assert.Empty(doc.Diagnostics);
    }

    // ── The fallback draws no wider than it measured ─────────────────────────

    private static double WidthOf(string text, double size) =>
        new FontReference(Standard14.Helvetica).MeasureString(text, size);

    /// <summary>
    /// A cell laid out unbroken is wrapped by its width with each control character at its own
    /// width, which is zero in Helvetica. Drawn with each one as a space it would be wider than it
    /// was measured and cross its border or the page edge: 60 two-letter lines joined by a line
    /// feed drew 102.86pt in an 88pt box. With the characters left out, no line of the cell is
    /// wider than the box it was wrapped to, and every letter is still drawn, in order.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFallbackCellIsDrawnNoWiderThanItWasMeasured(bool fixedColumns)
    {
        using var doc = fixedColumns
            ? WidePage()
            : new Document { PageSize = new PdfRectangle(0, 0, 400, 600), Margins = new EdgeInsets(10) };
        var t = fixedColumns
            ? Wide10(100, 100)
            : new TableElement { DefaultCellStyle = Helvetica(10) };
        t.AddRow(isHeader: true).AddCell(Words(60)).AddCell("h");
        t.AddRow().AddCell("x").AddCell("y");
        doc.Add(t);

        var content = Content(Save(doc));
        var placements = Placements(content);
        var header = placements.Where(p => p.Text.All(c => c is 'a' or 'b')).ToList();
        var right = fixedColumns ? 10 + 100 - 4 : 400 - 10;

        Assert.NotNull(CellReport(doc));
        Assert.Equal(Words(60, ""), string.Concat(header.Select(p => p.Text)));
        Assert.True(header.Count > 1, "the cell was not wrapped");
        foreach (var p in header)
            Assert.True(p.X + WidthOf(p.Text, 10) <= right + 0.001, $"\"{p.Text}\" at x={p.X} ends past {right}.");
    }

    /// <summary>
    /// A header that is too tall because of its line breaks gives up those breaks and nothing else:
    /// a RowSpan group below it that does not fit with the tall header, and fits with the short
    /// one, is not read as a reason to lay out the cells of every other row unbroken. The ordinary
    /// row "x" and "y" keeps its two lines, and only the header cell is reported. With automatic
    /// columns the row shares a column with the group, so a pass that went on to judge the group
    /// by the header's old height would reach it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFallbackHeaderLeavesAnUnrelatedRowItsBreaks(bool autoColumns)
    {
        using var doc = WidePage();
        var t = autoColumns ? new TableElement { DefaultCellStyle = Helvetica(10) } : Wide10(1000, 900);
        t.AddRow(isHeader: true).AddCell(Words(60)).AddCell("h");
        t.AddRow().AddCell(new Cell("a") { RowSpan = 2 }).AddCell("b");
        t.AddRow().AddCell("c");
        t.AddRow().AddCell("x\ny");
        doc.Add(t);

        var bytes = Save(doc);
        var texts = Texts(Content(bytes));
        var reports = doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped).ToList();

        Assert.Equal(1, PageCount(bytes));
        Assert.Contains(Words(60, ""), texts);
        Assert.Contains("x", texts);
        Assert.Contains("y", texts);
        var report = Assert.Single(reports);
        Assert.Contains("row 0, cell 0", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row that can be cut between its lines keeps them when a tab tips a group over the page
    /// elsewhere. The columns are fixed, so the row's cell cannot change the group's height: the
    /// tab cell of the group is laid out unbroken and is the only cell reported, and "x" and "y"
    /// are two lines.
    /// </summary>
    [Fact]
    public void ARowThatCanBeCutKeepsItsBreaksWhenATabTipsAGroup()
    {
        using var doc = WidePage();
        var t = Wide10(36, 36);
        t.AddRow().AddCell(new Cell("a") { RowSpan = 2 }).AddCell("ab\tab");
        t.AddRow().AddCell(Words(46, " "));
        t.AddRow().AddCell("x\ny");
        doc.Add(t);

        var texts = Texts(Content(Save(doc)));
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);

        Assert.Contains("abab", texts);
        Assert.Contains("x", texts);
        Assert.Contains("y", texts);
        Assert.Contains("row 0, cell 1", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// When a header's line breaks take the room a group needs, the header gives up its breaks
    /// and no more: a tab in another header cell stays one space and is not reported.
    /// </summary>
    [Fact]
    public void AHeaderThatTakesTheRoomOfAGroupGivesUpOnlyItsBreaks()
    {
        using var doc = WidePage();
        var t = Wide10(30, 1800, 70);
        t.AddRow(isHeader: true).AddCell("h").AddCell(Words(30)).AddCell("x\ty");
        t.AddRow().AddCell(new Cell(Words(20, " ")) { RowSpan = 2 }).AddCell("b");
        t.AddRow().AddCell("c");
        doc.Add(t);

        var texts = Texts(Content(Save(doc)));
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);

        Assert.Contains(Words(30, ""), texts);
        Assert.Contains("x y", texts);
        Assert.Contains("row 0, cell 1", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A group that does not fit and is relieved changes the width of an automatic column, so the
    /// next group is judged after the table is measured again, not by the heights taken before.
    /// Group A, 30 lines tall, gives up its breaks; group B, 12 lines of three words, fits once
    /// the column it shares with the long text below has been measured again, and keeps its
    /// breaks. Only the cell of group A is reported. A column given the width 0 is an automatic
    /// column, so setting both widths to 0 reads the same.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARelievedGroupIsMeasuredAgainBeforeTheNextGroupIsJudged(bool zeroWidths)
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 600, 300), Margins = new EdgeInsets(10) };
        var t = new TableElement { DefaultCellStyle = Helvetica(10) };
        if (zeroWidths) t.SetColumnWidths(0, 0);
        t.AddRow().AddCell(new Cell(Words(30)) { RowSpan = 2 }).AddCell("x");
        t.AddRow().AddCell("y");
        t.AddRow().AddCell(new Cell(Words(12).Replace("ab", "word word word")) { RowSpan = 2 }).AddCell("z");
        t.AddRow().AddCell("y");
        t.AddRow().AddCell("o").AddCell(string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit amet", 16)));
        doc.Add(t);

        var texts = Texts(Content(Save(doc)));
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);

        Assert.Contains("row 0, cell 0", report.Message, StringComparison.Ordinal);
        Assert.Equal(12, texts.Count(x => x == "word word word"));
    }

    /// <summary>
    /// Header rows that a tab tips over the page, with no line break among them, are laid out
    /// unbroken at the third step: "ab", a tab and "ab" fit a 36pt column as one word and as two
    /// words wrap one line taller, and the second header row is 44 plain lines. The two rows are
    /// 556pt with the one line and 568pt with two, and the 20pt row below fits under the first and
    /// not under the second. The tab is in the first header row or in the last one: the last row
    /// of the header is one of the rows that step reaches.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHeaderThatATabTipsIsLaidOutUnbroken(bool tabInFirstRow)
    {
        using var doc = WidePage();
        var t = Wide10(36, 36);
        t.AddRow(isHeader: true).AddCell(tabInFirstRow ? "ab\tab" : Words(44, " "));
        t.AddRow(isHeader: true).AddCell(tabInFirstRow ? Words(44, " ") : "ab\tab");
        t.AddRow().AddCell("x");
        doc.Add(t);

        var bytes = Save(doc);
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);

        Assert.Equal(1, PageCount(bytes));
        Assert.Contains("abab", Texts(Content(bytes)));
        Assert.Contains(tabInFirstRow ? "row 0, cell 0" : "row 1, cell 0", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tab in the last header row that tips a RowSpan group over the page, while the header fits
    /// and leaves its rows room: the group's third step reaches the header rows, the last one
    /// included. The header cell is one line, 20pt, as one word and two, 32pt, as two; under it
    /// the group is 556pt, which is 576pt with the one line and 588pt with two.
    /// </summary>
    [Fact]
    public void ATabInTheLastHeaderRowThatTipsAGroupIsLaidOutUnbroken()
    {
        using var doc = WidePage();
        var t = Wide10(36, 36);
        t.AddRow(isHeader: true).AddCell("ab	ab");
        t.AddRow().AddCell(new Cell("a") { RowSpan = 2 }).AddCell("b");
        t.AddRow().AddCell(Words(44, " "));
        doc.Add(t);

        var bytes = Save(doc);
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);

        Assert.Equal(1, PageCount(bytes));
        Assert.Contains("abab", Texts(Content(bytes)));
        Assert.Contains("row 0, cell 0", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A header that gives up its tab changes the room every group under it has, so the groups
    /// after it are judged on the new header. The tab in the header tips the first group over the
    /// page, and the header is 20pt once it is given up. The second group, 576pt with that header
    /// and 588pt with the tab as a space, holds line feeds and keeps them: only the header cell is
    /// reported.
    /// </summary>
    [Fact]
    public void AGroupAfterARelievedHeaderIsJudgedByTheHeaderThatRemains()
    {
        using var doc = WidePage();
        var t = Wide10(36, 36);
        t.AddRow(isHeader: true).AddCell("ab	ab");
        t.AddRow().AddCell(new Cell("a") { RowSpan = 2 }).AddCell("b");
        t.AddRow().AddCell(Words(44, " "));
        t.AddRow().AddCell(new Cell("a") { RowSpan = 2 }).AddCell("b");
        t.AddRow().AddCell(Words(44));
        doc.Add(t);

        Save(doc);
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);

        Assert.Contains("row 0, cell 0", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line separator, U+2028, is a space and not a break, so it is not the first step: a group
    /// that its cell does not tip is left as written, and only the cell with the line feeds is laid
    /// out unbroken.
    /// </summary>
    [Fact]
    public void AControlSpaceThatIsNotABreakIsNotTheFirstStep()
    {
        using var doc = WidePage();
        var t = Wide10(1000, 900);
        t.AddRow().AddCell(new Cell(Words(60)) { RowSpan = 2 }).AddCell("a\u2028b");
        t.AddRow().AddCell("c");
        doc.Add(t);

        var texts = Texts(Content(Save(doc)));
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);

        Assert.Contains("row 0, cell 0", report.Message, StringComparison.Ordinal);
        Assert.Contains("a b", texts);
    }

    /// <summary>
    /// A row below the header can be the one with no room for a line: the first row under the
    /// header holds a cell with no padding, which fits the 20pt a header of 46 lines leaves, and
    /// the second holds a cell with 10pt of padding above and below, which does not. The header
    /// gives up its breaks, and the table saves on one page.
    /// </summary>
    [Fact]
    public void AHeaderIsRelievedWhenALaterRowHasNoRoomForALine()
    {
        using var doc = WidePage();
        var t = Wide10(1900);
        t.AddRow(isHeader: true).AddCell(Words(46));
        t.AddRow().AddCell(new Cell("x") { Padding = new EdgeInsets(0) });
        t.AddRow().AddCell(new Cell("y") { Padding = new EdgeInsets(10) });
        doc.Add(t);

        var bytes = Save(doc);

        Assert.Equal(1, PageCount(bytes));
        Assert.Contains(Words(46, ""), Texts(Content(bytes)));
        Assert.Contains("row 0, cell 0", CellReport(doc)!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table of nothing but header rows repeats none of them, so a row of 48 lines, 584pt on a
    /// 580pt page, is an ordinary row: it is cut between its lines over two pages and nothing is
    /// laid out unbroken.
    /// </summary>
    [Fact]
    public void ATableOfNothingButHeaderRowsIsCutLikeAnyOther()
    {
        using var doc = WidePage();
        var t = Wide10(1900);
        t.AddRow(isHeader: true).AddCell(Words(48));
        doc.Add(t);

        var bytes = Save(doc);

        Assert.Equal(2, PageCount(bytes));
        Assert.Equal(48, Texts(Content(bytes)).Count(x => x == "ab"));
        Assert.Null(CellReport(doc));
    }

    /// <summary>
    /// A cell that holds the line breaks is found by its place among the row's cells, not by the
    /// column it starts in: here it is the second cell of its row and starts in the third column,
    /// after a cell that spans two. It is laid out unbroken, measured and drawn alike, so the rows
    /// are one line tall and the table saves on one page.
    /// </summary>
    [Fact]
    public void AFallbackCellAfterAColSpanIsMeasuredAsItIsDrawn()
    {
        using var doc = WidePage();
        var t = Wide10(400, 400, 1100);
        t.AddRow().AddCell(new Cell("a") { ColSpan = 2 }).AddCell(new Cell(Words(60)) { RowSpan = 2 });
        t.AddRow().AddCell(new Cell("c") { ColSpan = 2 });
        doc.Add(t);

        var bytes = Save(doc);
        var content = Content(bytes);

        Assert.Equal(1, PageCount(bytes));
        Assert.Equal(3, Borders(content).Length);
        Assert.All(Heights(content), h => Assert.True(h < 60, $"a border {h}pt tall"));
        Assert.Contains(Words(60, ""), Texts(content));
        Assert.Contains("row 0, cell 1", CellReport(doc)!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every group that has no room is judged and relieved, and one that has room keeps its
    /// breaks: the first and the third group are 60 lines tall and the second 10. The columns are
    /// fixed, so no group's height depends on another's.
    /// </summary>
    [Fact]
    public void EveryGroupWithNoRoomInFixedColumnsIsRelievedAndAFittingOneIsNot()
    {
        using var doc = WidePage();
        var t = Wide10(1500, 400);
        foreach (var lines in new[] { 60, 10, 60 })
        {
            t.AddRow().AddCell(new Cell(Words(lines)) { RowSpan = 2 }).AddCell("x");
            t.AddRow().AddCell("y");
        }
        doc.Add(t);

        var texts = Texts(Content(Save(doc)));
        var reports = doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped).ToList();

        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, r => r.Message.Contains("row 0, cell 0", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.Message.Contains("row 4, cell 0", StringComparison.Ordinal));
        Assert.Equal(10, texts.Count(x => x == "ab"));
    }

    /// <summary>
    /// Giving up the breaks of the groups in fixed columns costs a fixed number of measurements
    /// of the table, not one per group. The allocations of a table of 400 groups that each give up
    /// their breaks stay within a small multiple of those of the same table with the lines
    /// separated by spaces, which has nothing to give up. Allocations are counted, not time,
    /// because the same run on a loaded machine takes several times as long.
    /// </summary>
    [Fact]
    public void TheCostOfGivingUpBreaksInFixedColumnsDoesNotGrowWithEachGroup()
    {
        static (long Bytes, int Reported) Allocated(int groups, string separator)
        {
            using var doc = WidePage();
            var t = Wide10(1500, 400);
            for (var i = 0; i < groups; i++)
            {
                t.AddRow().AddCell(new Cell(Words(60, separator)) { RowSpan = 2 }).AddCell("x");
                t.AddRow().AddCell("y");
            }
            doc.Add(t);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Save(doc);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            return (bytes, doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped).Sum(d => d.Count));
        }

        Allocated(10, "\n");
        Allocated(10, " ");
        var withBreaks = Allocated(400, "\n");
        var withSpaces = Allocated(400, " ");

        Assert.Equal(400, withBreaks.Reported);
        Assert.Equal(0, withSpaces.Reported);
        Assert.True(withBreaks.Bytes < 5 * withSpaces.Bytes, $"{withBreaks.Bytes} bytes against {withSpaces.Bytes} without breaks");
    }

    /// <summary>
    /// The same holds when a tab tips the groups over the page, the third step: each group is a
    /// span cell, a tab cell that wraps one line taller as two words than as one, and 46 plain
    /// lines. The tab cell gives up its tab in every group, in one pass.
    /// </summary>
    [Fact]
    public void TheCostOfGivingUpTabsInFixedColumnsDoesNotGrowWithEachGroup()
    {
        static (long Bytes, int Reported) Allocated(int groups, string tabCell)
        {
            using var doc = WidePage();
            var t = Wide10(36, 36);
            for (var i = 0; i < groups; i++)
            {
                t.AddRow().AddCell(new Cell("a") { RowSpan = 2 }).AddCell(tabCell);
                t.AddRow().AddCell(Words(46, " "));
            }
            doc.Add(t);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Save(doc);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            return (bytes, doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped).Sum(d => d.Count));
        }

        Allocated(10, "ab	ab");
        Allocated(10, "abab");
        var withTabs = Allocated(400, "ab	ab");
        var withoutTabs = Allocated(400, "abab");

        Assert.Equal(400, withTabs.Reported);
        Assert.Equal(0, withoutTabs.Reported);
        Assert.True(withTabs.Bytes < 5 * withoutTabs.Bytes, $"{withTabs.Bytes} bytes against {withoutTabs.Bytes} without tabs");
    }

    /// <summary>
    /// Three rows chained by RowSpans, each one's span reaching into the next, are one group: 3 x
    /// 200pt is 600pt, which is more than a page, though each pair is 400pt. All three cells that
    /// hold breaks are laid out unbroken and reported, and the table saves.
    /// </summary>
    [Fact]
    public void ARowSpanChainIsOneGroup()
    {
        using var doc = WidePage();
        var t = Wide10(1000, 900);
        t.AddRow().AddCell(new Cell(Words(16)) { RowSpan = 2 }).AddCell("b");
        t.AddRow().AddCell(new Cell(Words(16)) { RowSpan = 2 });
        t.AddRow().AddCell(Words(16));
        doc.Add(t);

        var bytes = Save(doc);

        Assert.Equal(1, PageCount(bytes));
        Assert.Equal(3, doc.Diagnostics.Count(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped));
    }

    /// <summary>
    /// A cell that is laid out unbroken is reported where it is first drawn, so a cut row whose
    /// cell is one of them is reported once, not once per page it spans. Row 2 is cut across
    /// pages, and its two cells, which are not in the RowSpan group in the last row, are laid out
    /// unbroken because their columns are automatic and the group's cell is in them.
    /// </summary>
    [Fact]
    public void ACutRowWithAFallbackCellIsReportedOnce()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 200), Margins = new EdgeInsets(10) };
        var seventy = string.Join(" ", Enumerable.Repeat("wd", 70)) + "\tx";
        var t = new TableElement { DefaultCellStyle = Helvetica(12) };
        t.AddRow().AddCell(new Cell("q") { ColSpan = 2 }).AddCell("wd wd wd wd wd");
        t.AddRow().AddCell(Words(40).Replace("ab", "wd")).AddCell("q");
        t.AddRow().AddCell("t\t").AddCell(seventy);
        t.AddRow().AddCell(seventy).AddCell(new Cell("aaaa") { RowSpan = 2, ColSpan = 2 });
        doc.Add(t);

        var bytes = Save(doc);
        var cut = doc.Diagnostics.Single(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped
            && d.Message.Contains("row 2, cell 1", StringComparison.Ordinal));

        Assert.True(PageCount(bytes) > 2, "the row was not cut");
        Assert.Equal(1, cut.Count);

        // The slices of the cut row are wrapped from the same unbroken text as the cell in row 3
        // that is not cut: in both the tab before the last "x" is left out, not a space.
        Assert.Equal(2, Texts(Content(bytes)).Count(x => x.EndsWith("wdx", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The report is kept per table: two tables whose first header cell is laid out unbroken give
    /// two reports, though both name row 0, cell 0.
    /// </summary>
    [Fact]
    public void TwoTablesReportTheirOwnFallbackCells()
    {
        using var doc = WidePage();
        for (var i = 0; i < 2; i++)
        {
            var t = Wide10(1900);
            t.AddRow(isHeader: true).AddCell(Words(60));
            t.AddRow().AddCell("x");
            doc.Add(t);
        }

        Save(doc);

        Assert.Equal(2, doc.Diagnostics.Count(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped));
    }

    private static void AssertNoControlCharacter(IEnumerable<ContentStreamReadback.TextPlacement> placements)
    {
        foreach (var p in placements)
            Assert.DoesNotContain(p.Text, ch => ch < 0x20);
    }
}
