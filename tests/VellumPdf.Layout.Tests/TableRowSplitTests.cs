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
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A table row taller than a page is cut between its lines and continues on the next page,
/// instead of being refused as too tall to render (#516).
///
/// Every fixture is a 300x600 page with 10pt margins, so the content box is 580pt tall and its top
/// edge is at y = 590 in PDF space. The columns are 40pt and 100pt wide, in Helvetica 10pt with the
/// default padding, so a cell holds one "aaaa" word (22.24pt against a 28pt inner width) per line,
/// a line is 12pt, and a cell of n lines is 12 n + 8 pt tall. A 47-line row is 572pt, which a page
/// holds; a 48-line row is 584pt, which no page does.
/// </summary>
public sealed class TableRowSplitTests : IDisposable
{
    private const string TooTall =
        "An element is too tall to fit on a single page and cannot be rendered. " +
        "Reduce the element's content or increase the page size.";

    private static readonly string DejaVu = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    private static readonly ColorRgb Grey = new(0.9, 0.8, 0.7);
    private static readonly ColorRgb Blue = new(0.1, 0.2, 0.3);

    private readonly string _tempDir;

    public TableRowSplitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumrowsplit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static TextStyle Style(double size = 10) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = size,
    };

    private static TableElement Table() =>
        new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(40, 100);

    private static TableElement MarginTable() =>
        new TableElement { DefaultCellStyle = Style(), Margins = new EdgeInsets(10) }
            .SetColumnWidths(40, 100);

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("aaaa", count));

    /// <summary>The row the tests cut: <paramref name="lines"/> lines in the first cell, and "short" beside it.</summary>
    private static Row Tall(TableElement table, int lines, ColorRgb? background = null)
    {
        var row = new Row { Background = background };
        row.AddCell(Words(lines)).AddCell("short");
        return table.Add(row);
    }

    private static Document Page(RunningBand? footer = null) => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 600),
        Margins = new EdgeInsets(10),
        Footer = footer,
    };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>The decoded content of every page, with a footer when one is given.</summary>
    private static List<string> Pages(TableElement table, RunningBand? footer = null)
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 600), new EdgeInsets(10));
        if (footer is not null) renderer.Footer = footer;
        renderer.Add(new TableRenderer(table));
        using var ms = new MemoryStream();
        renderer.Render(ms);
        return [.. pdf.Pages.Select(p => Encoding.Latin1.GetString(p.ContentBytes!))];
    }

    private static readonly Regex Rect = new(
        @"(?<r>\S+ \S+ \S+ \S+) re\s+(?<op>[fS])\b", RegexOptions.CultureInvariant);

    /// <summary>Every rectangle a page paints, as "x y w h f" for a fill and "x y w h S" for a border.</summary>
    private static string[] Rects(string page) =>
        [.. Rect.Matches(page).Select(m => m.Groups["r"].Value + " " + m.Groups["op"].Value)];

    private static string[] Borders(string page) =>
        [.. Rects(page).Where(r => r.EndsWith('S')).Select(r => r[..^2])];

    private static string[] Texts(string page) =>
        [.. ContentStreamReadback.TextPlacements(page).Select(p => p.Text).Where(t => t.Length > 0)];

    private static string[] Run(string word, int count) => [.. Enumerable.Repeat(word, count)];

    private static string[] Concat(params string[][] parts) => [.. parts.SelectMany(p => p)];

    // ── Rows that fit a page are left alone ──────────────────────────────────

    /// <summary>
    /// 47 lines is 572pt, which a 580pt page holds whole, so nothing is cut: one page, the whole
    /// row in two cell rectangles 572pt tall.
    /// </summary>
    [Fact]
    public void ARowThatFitsAPage_isNotCut()
    {
        var t = Table();
        Tall(t, 47);

        var pages = Pages(t);

        var page = Assert.Single(pages);
        Assert.Equal(["10 18 40 572", "50 18 100 572"], Borders(page));
        Assert.Equal(Concat(Run("aaaa", 47), ["short"]), Texts(page));
    }

    /// <summary>
    /// A row that does not fit the room left under other rows, but does fit a page of its own,
    /// moves to the next page whole, as it always has: page 1 holds the 20pt row alone and page 2 the
    /// 47 lines, with no line of the row left behind.
    /// </summary>
    [Fact]
    public void ARowThatFitsAFreshPage_butNotTheRoomLeft_movesWhole()
    {
        var t = Table();
        t.AddRow().AddCell("x").AddCell("y");
        Tall(t, 47);

        var pages = Pages(t);

        Assert.Equal(2, pages.Count);
        Assert.Equal(["x", "y"], Texts(pages[0]));
        Assert.Equal(Concat(Run("aaaa", 47), ["short"]), Texts(pages[1]));
        Assert.Equal(["10 18 40 572", "50 18 100 572"], Borders(pages[1]));
    }

    /// <summary>
    /// With a header, 20pt + 46 lines (560pt) is exactly the 580pt page, and saves on one page.
    /// </summary>
    [Fact]
    public void AHeaderAndARowThatTogetherFillThePage_isNotCut()
    {
        var t = Table();
        t.AddHeaderRow().AddCell("H").AddCell("H2");
        Tall(t, 46);

        var page = Assert.Single(Pages(t));

        Assert.Equal(["10 570 40 20", "50 570 100 20", "10 10 40 560", "50 10 100 560"], Borders(page));
    }

    /// <summary>
    /// The table's own margins take room from the page a row has to fit. With a margin of 10 on
    /// every side the table has 560pt of a 580pt page, so a 46-line row (560pt) fits and is not
    /// cut.
    /// </summary>
    [Fact]
    public void ARowThatFillsThePageInsideTheTableMargins_isNotCut()
    {
        var t = MarginTable();
        Tall(t, 46);

        var page = Assert.Single(Pages(t));

        Assert.Equal(["20 20 40 560", "60 20 100 560"], Borders(page));
    }

    /// <summary>
    /// A 47-line row (572pt) fits the 580pt page, so a table without margins places it whole, but
    /// not the 560pt left inside a table margin of 10: it is cut, with 46 lines on page 1 and the
    /// 47th on page 2. 2.3.3 refused this table as too tall. The fresh page the row has to fit is
    /// deflated by the margins, so a row that fits the page alone is still cut.
    /// </summary>
    [Fact]
    public void ARowThatFitsThePageButNotInsideTheTableMargins_isCut()
    {
        var t = MarginTable();
        Tall(t, 47);

        var pages = Pages(t);

        Assert.Equal(2, pages.Count);
        Assert.Equal(["20 20 40 560", "60 20 100 560"], Borders(pages[0]));
        Assert.Equal(Concat(Run("aaaa", 46), ["short"]), Texts(pages[0]));
        Assert.Equal(["20 560 40 20", "60 560 100 20"], Borders(pages[1]));
        Assert.Equal(["aaaa"], Texts(pages[1]));
    }

    // ── Rows taller than a page are cut between lines ────────────────────────

    /// <summary>
    /// 48 lines is 584pt: page 1 takes 47 lines in a 572pt cell, and page 2 takes the 48th in a 20pt
    /// cell at the top of the content box. The "short" cell has no line left for page 2 and draws its
    /// rectangle without text.
    /// </summary>
    [Fact]
    public void ARowOneLineTooTall_isCutAfterTheLastLineThatFits()
    {
        var t = Table();
        Tall(t, 48);

        var pages = Pages(t);

        Assert.Equal(2, pages.Count);
        Assert.Equal(["10 18 40 572", "50 18 100 572"], Borders(pages[0]));
        Assert.Equal(Concat(Run("aaaa", 47), ["short"]), Texts(pages[0]));
        Assert.Equal(["10 570 40 20", "50 570 100 20"], Borders(pages[1]));
        Assert.Equal(["aaaa"], Texts(pages[1]));

        var second = ContentStreamReadback.TextPlacements(pages[1]).Single(p => p.Text.Length > 0);
        Assert.Equal(16.0, second.X, 0.001);
        Assert.Equal(576.0, second.Y, 0.001);

        // The 47th line of page 1 sits 46 leadings below the first, which is 576.
        var last = ContentStreamReadback.TextPlacements(pages[0]).Where(p => p.Text == "aaaa").Last();
        Assert.Equal(576.0 - (46 * 12), last.Y, 0.001);
    }

    /// <summary>
    /// A header and 47 lines is 592pt. The header repeats, page 1 holds 46 lines (560pt) under it,
    /// and page 2 holds the 47th under the repeated header at the same position.
    /// </summary>
    [Fact]
    public void AHeaderAndARowTooTall_repeatTheHeaderAndCutTheRow()
    {
        var t = Table();
        t.AddHeaderRow().AddCell("H").AddCell("H2");
        Tall(t, 47);

        var pages = Pages(t);

        Assert.Equal(2, pages.Count);
        Assert.Equal(["10 570 40 20", "50 570 100 20", "10 10 40 560", "50 10 100 560"], Borders(pages[0]));
        Assert.Equal(Concat(["H", "H2"], Run("aaaa", 46), ["short"]), Texts(pages[0]));
        Assert.Equal(["10 570 40 20", "50 570 100 20", "10 550 40 20", "50 550 100 20"], Borders(pages[1]));
        Assert.Equal(["H", "H2", "aaaa"], Texts(pages[1]));

        // The first baseline under the header: 10 + 20 + 4 padding + 10 font size from the top.
        var under = ContentStreamReadback.TextPlacements(pages[1]).Single(p => p.Text == "aaaa");
        Assert.Equal(600.0 - 44.0, under.Y, 0.001);
    }

    /// <summary>
    /// A 48-line row after a 20pt row starts on the page the first row is on, because 46 lines
    /// (20 + 560 = 580pt) fit below it: page 1 holds both, page 2 the last two lines in a 32pt cell.
    /// </summary>
    [Fact]
    public void ACutRow_startsOnTheCurrentPage_whenALineFitsThere()
    {
        var t = Table();
        t.AddRow().AddCell("x").AddCell("y");
        Tall(t, 48);

        var pages = Pages(t);

        Assert.Equal(2, pages.Count);
        Assert.Equal(["10 570 40 20", "50 570 100 20", "10 10 40 560", "50 10 100 560"], Borders(pages[0]));
        Assert.Equal(Concat(["x", "y"], Run("aaaa", 46), ["short"]), Texts(pages[0]));
        Assert.Equal(["10 558 40 32", "50 558 100 32"], Borders(pages[1]));
        Assert.Equal(["aaaa", "aaaa"], Texts(pages[1]));
    }

    /// <summary>
    /// When no line of the row fits under the rows already on the page, the table breaks before the
    /// row, as it does for a row that fits a fresh page, and the row is cut from the top of page 2.
    /// Thirty 20pt rows leave 0pt under them on a 580pt page less one row, so 29 rows fill it exactly.
    /// </summary>
    [Fact]
    public void ACutRow_startsOnTheNextPage_whenNoLineFitsOnTheCurrentOne()
    {
        var t = Table();
        for (var i = 0; i < 29; i++) t.AddRow().AddCell("r" + i.ToString(CultureInfo.InvariantCulture)).AddCell("-");
        Tall(t, 48);

        var pages = Pages(t);

        Assert.Equal(3, pages.Count);
        Assert.Equal(58, Texts(pages[0]).Length);
        Assert.DoesNotContain("aaaa", Texts(pages[0]));
        Assert.Equal(Concat(Run("aaaa", 47), ["short"]), Texts(pages[1]));
        Assert.Equal(["aaaa"], Texts(pages[2]));
    }

    /// <summary>
    /// Three pages: a 100-line row under a header is 46, 46 and 8 lines, the header on each page, and
    /// the last cell is 104pt tall.
    /// </summary>
    [Fact]
    public void ARowOfManyLines_isCutOnEveryPageItSpans()
    {
        var t = Table();
        t.AddHeaderRow().AddCell("H").AddCell("H2");
        Tall(t, 100);

        var pages = Pages(t);

        Assert.Equal(3, pages.Count);
        Assert.Equal(Concat(["H", "H2"], Run("aaaa", 46), ["short"]), Texts(pages[0]));
        Assert.Equal(Concat(["H", "H2"], Run("aaaa", 46)), Texts(pages[1]));
        Assert.Equal(Concat(["H", "H2"], Run("aaaa", 8)), Texts(pages[2]));
        Assert.Equal("10 10 40 560", Borders(pages[1])[2]);
        Assert.Equal("10 466 40 104", Borders(pages[2])[2]);
    }

    /// <summary>Rows after a cut row continue on the page the cut row ends on, at the height it left.</summary>
    [Fact]
    public void RowsAfterACutRow_followItsLastSlice()
    {
        var t = Table();
        Tall(t, 48);
        t.AddRow().AddCell("next").AddCell("-");

        var pages = Pages(t);

        Assert.Equal(2, pages.Count);
        Assert.Equal(["aaaa", "next", "-"], Texts(pages[1]));
        Assert.Equal(["10 570 40 20", "50 570 100 20", "10 550 40 20", "50 550 100 20"], Borders(pages[1]));
    }

    /// <summary>
    /// Two cells of different leading in one row are cut independently. Cell 0 is 10pt (12pt leading,
    /// 47 lines fit 572pt); cell 1 is 20pt (24pt leading, 23 lines fit 560pt). The slice is as tall
    /// as the taller of the two, here 572, and each cell continues on page 2 at its own line.
    /// </summary>
    [Fact]
    public void CellsOfDifferentLeading_areCutAtTheirOwnLine()
    {
        var t = new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(40, 60);
        var row = new Row();
        row.AddCell(Words(48));
        row.AddCell(new Cell(string.Join(' ', Enumerable.Repeat("bb", 30))) { Style = Style(20) });
        t.Add(row);

        var pages = Pages(t);

        Assert.Equal(2, pages.Count);
        Assert.Equal(Concat(Run("aaaa", 47), Run("bb", 23)), Texts(pages[0]));
        Assert.Equal(Concat(["aaaa"], Run("bb", 7)), Texts(pages[1]));
        Assert.Equal(["10 18 40 572", "50 18 60 572"], Borders(pages[0]));

        // Page 2: the 7 remaining 20pt lines are 7 * 24 + 8 = 176pt, taller than the 20pt line of cell 0.
        Assert.Equal(["10 414 40 176", "50 414 60 176"], Borders(pages[1]));
    }

    /// <summary>The same cut, with a footer: the content box is shorter and the pages still count.</summary>
    [Fact]
    public void ACutRow_withAFooter_countsItsPagesInBothPasses()
    {
        var footer = new RunningBand("{page}/{pages}", style: Style(8));
        var t = Table();
        Tall(t, 48);

        var pages = Pages(t, footer);

        // Content box 580 - 13.6 = 566.4pt: 46 lines (560pt) fit, then 2 lines.
        Assert.Equal(2, pages.Count);
        Assert.Equal(Concat(Run("aaaa", 46), ["short", "1/2"]), Texts(pages[0]));
        Assert.Equal(["aaaa", "aaaa", "2/2"], Texts(pages[1]));
    }

    /// <summary>
    /// The issue's own shape: one 6,000-character cell and a short one on A4 with the default margins
    /// and automatic columns. The words are drawn once each, in order, across the pages the cut makes,
    /// and the short cell is drawn once.
    /// </summary>
    [Fact]
    public void ASixThousandCharacterCell_paginatesAndKeepsAllItsText()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, PageSize.A4, new EdgeInsets(72));
        var t = new TableElement();
        var text = string.Join(' ', Enumerable.Repeat("lorem", 1200))[..6000];
        t.AddRow().AddCell(text).AddCell("second");
        renderer.Add(new TableRenderer(t));
        using var ms = new MemoryStream();
        renderer.Render(ms);
        var pages = pdf.Pages.Select(p => Encoding.Latin1.GetString(p.ContentBytes!)).ToList();

        Assert.Equal(2, pages.Count);

        // The short cell is narrow enough that "second" breaks inside the word, so it is told apart by
        // what it spells: every piece that is not a run of "lorem" belongs to it.
        var drawn = pages.SelectMany(Texts).ToList();
        var shortCell = drawn.Where(s => !s.StartsWith("lorem", StringComparison.Ordinal));
        Assert.Equal("second", string.Concat(shortCell));
        var words = string.Join(' ', drawn.Where(s => s.StartsWith("lorem", StringComparison.Ordinal))).Split(' ');
        Assert.Equal(text.Split(' '), words);
    }

    // ── What each slice draws ────────────────────────────────────────────────

    /// <summary>
    /// A row background fills each cell rectangle of every slice with the row colour, ahead of the
    /// border and the text, and the cell with no line on page 2 is still filled.
    /// </summary>
    [Fact]
    public void ARowBackground_fillsEveryCellOfEverySlice()
    {
        var t = Table();
        Tall(t, 48, Grey);

        var pages = Pages(t);

        Assert.Equal(["10 18 40 572 f", "50 18 100 572 f"], Rects(pages[0]).Where(r => r.EndsWith('f')).ToArray());
        Assert.Equal(["10 570 40 20 f", "50 570 100 20 f"], Rects(pages[1]).Where(r => r.EndsWith('f')).ToArray());
        Assert.Equal(2, Regex.Matches(pages[1], @"0\.9 0\.8 0\.7 rg").Count);

        var fill = pages[1].IndexOf("10 570 40 20 re\nf", StringComparison.Ordinal);
        var border = pages[1].IndexOf("10 570 40 20 re\nS", StringComparison.Ordinal);
        var text = pages[1].IndexOf("(aaaa) Tj", StringComparison.Ordinal);
        Assert.True(fill >= 0 && border > fill && text > border, $"{fill} {border} {text}");
    }

    /// <summary>A cell background beats the row background in each slice, for that cell only.</summary>
    [Fact]
    public void ACellBackground_winsOverTheRowBackground_inEverySlice()
    {
        var t = Table();
        var row = new Row { Background = Grey };
        row.AddCell(new Cell(Words(48)) { Background = Blue }).AddCell("short");
        t.Add(row);

        var pages = Pages(t);

        foreach (var page in pages)
        {
            var colours = Regex.Matches(page, @"(?<c>\S+ \S+ \S+) rg\s+\S+ \S+ \S+ \S+ re\s+f")
                .Select(m => m.Groups["c"].Value).ToArray();
            Assert.Equal(["0.1 0.2 0.3", "0.9 0.8 0.7"], colours);
        }
    }

    /// <summary>The repeated header keeps its own background on every page, above the slice's.</summary>
    [Fact]
    public void ARepeatedHeaderBackground_isDrawnOnEveryPageOfACutRow()
    {
        var t = Table();
        t.Add(new Row { IsHeader = true, Background = Blue }.AddCell("H").AddCell("H2"));
        Tall(t, 100, Grey);

        var pages = Pages(t);

        Assert.Equal(3, pages.Count);
        foreach (var page in pages)
        {
            var fills = Rects(page).Where(r => r.EndsWith('f')).ToArray();
            Assert.Equal("10 570 40 20 f", fills[0]);
            Assert.Equal("50 570 100 20 f", fills[1]);
            Assert.Equal(4, fills.Length);
        }
    }

    // ── Tagged structure ─────────────────────────────────────────────────────

    /// <summary>
    /// A tagged cut row is one Table per page fragment, one TR per row and slice, and one TD per cell
    /// of each slice: the first cell's text is in a TD on each page, and the second cell, which has no
    /// line on page 2, is an empty TD. Page 1 has two paragraphs and page 2 one.
    /// </summary>
    [Fact]
    public void ATaggedCutRow_isOneTablePerPage_withATdPerCellOfEachSlice()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 600),
            Margins = new EdgeInsets(10),
            Tagged = true,
            Language = "en-US",
        };
        var t = Table();
        Tall(t, 48);
        doc.Add(t);

        var flat = Regex.Replace(Encoding.Latin1.GetString(Save(doc)), @"\s+", " ");

        Assert.Equal(2, PdfTestUtil.CountOccurrences(flat, "/S /Table"));
        Assert.Equal(2, PdfTestUtil.CountOccurrences(flat, "/S /TR"));
        Assert.Equal(4, PdfTestUtil.CountOccurrences(flat, "/S /TD"));
        Assert.Equal(0, PdfTestUtil.CountOccurrences(flat, "/S /TH"));
        Assert.Equal(3, PdfTestUtil.CountOccurrences(flat, "/S /P "));
    }

    /// <summary>A header row's cells stay TH on every page of the cut row, and the slice's are TD.</summary>
    [Fact]
    public void ATaggedCutRow_underAHeader_repeatsTheThOnEveryPage()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 600),
            Margins = new EdgeInsets(10),
            Tagged = true,
            Language = "en-US",
        };
        var t = Table();
        t.AddHeaderRow().AddCell("H").AddCell("H2");
        Tall(t, 100);
        doc.Add(t);

        var flat = Regex.Replace(Encoding.Latin1.GetString(Save(doc)), @"\s+", " ");

        Assert.Equal(3, PdfTestUtil.CountOccurrences(flat, "/S /Table"));
        Assert.Equal(6, PdfTestUtil.CountOccurrences(flat, "/S /TH"));
        Assert.Equal(6, PdfTestUtil.CountOccurrences(flat, "/S /TD"));
    }

    /// <summary>
    /// In a table of nothing but header rows, a cut row's cell with no line on page 2 is an empty
    /// <c>TD</c>, not a <c>TH</c>: an empty cell is never a header (Tagged PDF Best Practice Guide,
    /// 4.2.6.2). The cells that hold text stay <c>TH</c> with a column scope: two on page 1, one on page 2.
    /// </summary>
    [Fact]
    public void ACutHeaderRow_givesItsEmptyCellATd()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 600),
            Margins = new EdgeInsets(10),
            Tagged = true,
            Language = "en-US",
        };
        var t = Table();
        t.Add(new Row { IsHeader = true }.AddCell(Words(48)).AddCell("short"));
        doc.Add(t);

        var flat = Regex.Replace(Encoding.Latin1.GetString(Save(doc)), @"\s+", " ");

        Assert.Equal(3, PdfTestUtil.CountOccurrences(flat, "/S /TH"));
        Assert.Equal(1, PdfTestUtil.CountOccurrences(flat, "/S /TD"));
        Assert.Equal(3, PdfTestUtil.CountOccurrences(flat, "/Scope /Column"));
    }

    // ── Rows that are never cut ──────────────────────────────────────────────

    /// <summary>A header row taller than the page is refused, and the message names the rows.</summary>
    [Fact]
    public void AHeaderTallerThanThePage_isRefused_namingTheRows()
    {
        using var doc = Page();
        var t = Table();
        t.AddHeaderRow().AddCell(Words(60)).AddCell("H2");
        t.AddRow().AddCell("a").AddCell("b");
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(
            TooTall + " The repeated header (table row 0) is 728.0pt tall, but a page leaves 580.0pt for the table. " +
            "Header rows repeat on every page and are never split.",
            ex.Message);
    }

    /// <summary>
    /// A RowSpan group taller than the page is refused with the rows of the group named, and is never
    /// cut, even though each of its rows is a row that could be.
    /// </summary>
    [Fact]
    public void ARowSpanGroupTallerThanThePage_isRefused_namingTheGroup()
    {
        using var doc = Page();
        var t = Table();
        var first = new Row();
        first.AddCell(new Cell(Words(30)) { RowSpan = 2 }).AddCell("b");
        t.Add(first);
        t.AddRow().AddCell(Words(30));
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(
            TooTall + " Table rows 0 to 1 are joined by a RowSpan, so they are never split between pages. " +
            "Together they are 736.0pt tall, but a page leaves 580.0pt.",
            ex.Message);
    }

    /// <summary>A lone row that declares a RowSpan is refused by name too, with the single-row wording.</summary>
    [Fact]
    public void ARowSpanRowTallerThanThePage_isRefused_namingTheRow()
    {
        using var doc = Page();
        var t = Table();
        t.AddRow().AddCell(new Cell(Words(60)) { RowSpan = 2 }).AddCell("b");
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(
            TooTall + " Table row 0 takes part in a RowSpan, so it is never split between pages. " +
            "It is 728.0pt tall, but a page leaves 580.0pt.",
            ex.Message);
    }

    /// <summary>
    /// A cell whose padding and one line exceed the page makes no progress however many pages it is
    /// given: refused, with the row and the cell named. Cell 1 is the one named, not cell 0.
    /// </summary>
    [Fact]
    public void ACellWhoseLineCannotFitAPage_isRefused_namingTheCell()
    {
        using var doc = Page();
        var t = Table();
        var row = new Row();
        row.AddCell("x").AddCell(new Cell("y") { Padding = new EdgeInsets(400) });
        t.Add(row);
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(
            TooTall + " Table row 0, cell 1 needs 812.0pt for its vertical padding and one line, " +
            "but a page leaves 580.0pt, so the row cannot be split between pages.",
            ex.Message);
    }

    /// <summary>A line taller than the page is refused by name, the same way.</summary>
    [Fact]
    public void ALineTallerThanThePage_isRefused_namingTheCell()
    {
        using var doc = Page();
        var t = Table();
        t.AddRow().AddCell(new Cell("x") { Style = Style(600) });
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(
            TooTall + " Table row 0, cell 0 needs 728.0pt for its vertical padding and one line, " +
            "but a page leaves 580.0pt, so the row cannot be split between pages.",
            ex.Message);
    }

    /// <summary>
    /// A line that fits the page, but not under the header rows the page repeats, is refused naming
    /// the header's share.
    /// </summary>
    [Fact]
    public void ALineThatFitsOnlyWithoutTheHeader_isRefused_namingTheHeader()
    {
        using var doc = Page();
        var t = Table();
        t.AddHeaderRow().AddCell(Words(44)).AddCell("H2");
        t.AddRow().AddCell(new Cell("x") { Style = Style(60) });
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(
            TooTall + " Table row 1, cell 0 needs 80.0pt for its vertical padding and one line, " +
            "but a page leaves 44.0pt under the 536.0pt of repeated header rows, so the row cannot be split between pages.",
            ex.Message);
    }

    /// <summary>The refusal keeps the band wording first when a footer shrinks the content box.</summary>
    [Fact]
    public void ARefusal_withAFooter_startsWithTheBandMessage()
    {
        using var doc = Page(new RunningBand("p {page}", style: Style(8)));
        var t = Table();
        t.AddRow().AddCell(new Cell("x") { Style = Style(600) });
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.StartsWith(
            "An element is too tall to fit on a single page and cannot be rendered. " +
            "On a 600.0pt page, 20.0pt of margins and 13.6pt of running bands (header 0.0pt, footer 13.6pt) " +
            "leave a content area 566.4pt tall. Reduce the element's content, lower the band heights, " +
            "reduce the margins, or increase the page size. Table row 0, cell 0 needs 728.0pt",
            ex.Message);
    }

    /// <summary>
    /// A row that is refused after others were placed still throws, at the same save: the pages before
    /// it do not change the verdict.
    /// </summary>
    [Fact]
    public void ARefusedRow_afterOtherRows_isRefusedAtTheSave()
    {
        using var doc = Page();
        var t = Table();
        t.AddRow().AddCell("x").AddCell("y");
        t.AddRow().AddCell(new Cell("z") { Style = Style(600) });
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.StartsWith(TooTall, ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("so the row cannot be split between pages.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A span declared in a header row that covers the data row below it holds that row together with
    /// the header, so a data row taller than the page is refused rather than cut, and the refusal
    /// names the row. The group walk that names it must terminate: a header-origin span starts the
    /// group at the first data row.
    /// </summary>
    [Fact]
    public void ARowUnderAHeaderRowSpan_isRefused_andTheRefusalTerminates()
    {
        using var doc = Page();
        var t = Table();
        var header = new Row { IsHeader = true };
        header.AddCell(new Cell("H") { RowSpan = 2 }).AddCell("H2");
        t.Add(header);
        Tall(t, 60);
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.StartsWith(TooTall + " Table row 1 takes part in a RowSpan, so it is never split between pages. ", ex.Message, StringComparison.Ordinal);
    }

    // ── Canvas refusals in a slice that is now drawn ─────────────────────────

    /// <summary>
    /// A row 2.3.3 refused as too tall was never drawn, so a non-finite cell background in it was never
    /// written. Now that its slices are drawn the canvas refuses it: <see cref="ArgumentException"/>
    /// with no parameter name, where the same input threw <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public void ACutRow_withANonFiniteBackground_isRefusedByTheCanvas()
    {
        using var doc = Page();
        var t = Table();
        var row = new Row();
        row.AddCell(new Cell(Words(48)) { Background = new ColorRgb(double.NaN, 0, 0) }).AddCell("short");
        t.Add(row);
        doc.Add(t);

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));

        Assert.Null(ex.ParamName);
        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A non-finite text colour is refused where it is drawn, as it always was, with the same type.</summary>
    [Fact]
    public void ACutRow_withANonFiniteTextColour_keepsTheInvalidOperationException()
    {
        using var doc = Page();
        var t = Table();
        var style = new TextStyle
        {
            FontRef = new FontReference(Standard14.Helvetica),
            FontSize = 10,
            Color = new ColorRgb(double.NaN, 0, 0),
        };
        var row = new Row();
        row.AddCell(new Cell(Words(48)) { Style = style }).AddCell("short");
        t.Add(row);
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("text colour", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table margin that is NaN gives the row a NaN width, which is the same width on every page.
    /// The cut row must resume on the next page rather than start again from its first line, or the
    /// save reaches the page-continuation limit. The canvas refuses the NaN where it is written, as it
    /// does for a short row in the same table.
    /// </summary>
    [Fact]
    public void ACutRow_withANaNWidth_isRefusedByTheCanvas_notByTheContinuationLimit()
    {
        using var doc = Page();
        var t = new TableElement { DefaultCellStyle = Style(), Margins = new EdgeInsets(0, double.NaN, 0, 0) };
        t.SetColumnWidths(40, 100);
        Tall(t, 48);
        doc.Add(t);

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));

        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value.", ex.Message, StringComparison.Ordinal);
    }

    // ── A context the document did not build ─────────────────────────────────

    /// <summary>
    /// A renderer laid out with a context built through the public constructor has no fresh-page box
    /// and does not start a cut: the 48-line row, first in its area, is <c>Nothing</c> with no
    /// reason. The same call with the box set cuts it.
    /// </summary>
    [Fact]
    public void ARendererWithoutAFreshPageBox_doesNotStartACut()
    {
        var t = Table();
        Tall(t, 48);
        var area = new LayoutBox(10, 10, 280, 580);

        var plain = new TableRenderer(t).Layout(new LayoutContext(area));
        var withBox = new TableRenderer(t).Layout(new LayoutContext(area) { FreshPageArea = area });

        Assert.Equal(LayoutResult.Outcome.Nothing, plain.Status);
        Assert.Null(plain.Reason);
        Assert.Equal(LayoutResult.Outcome.Partial, withBox.Status);
    }

    /// <summary>
    /// Without the box a row that follows rows that fit is still not cut, but the call is not
    /// <c>Nothing</c>: it returns the rows that fit, and the overflow renderer is <c>Nothing</c>
    /// for the row, which is then the first in its area.
    /// </summary>
    [Fact]
    public void ARendererWithoutAFreshPageBox_returnsTheRowsThatFit_thenNothingForTheTallRow()
    {
        var t = Table();
        t.AddRow().AddCell("x").AddCell("y");
        Tall(t, 48);
        var area = new LayoutBox(10, 10, 280, 580);

        var first = new TableRenderer(t).Layout(new LayoutContext(area));
        var overflow = first.OverflowRenderer!.Layout(new LayoutContext(area));

        Assert.Equal(LayoutResult.Outcome.Partial, first.Status);
        Assert.Equal(20.0, first.OccupiedArea!.Value.Height, 0.001);
        Assert.Equal(LayoutResult.Outcome.Nothing, overflow.Status);
    }

    /// <summary>
    /// A row the document began to cut keeps being cut when its overflow renderer is laid out under
    /// a context without the box: the first layout, with the box, cuts the 48-line row after 47
    /// lines, and the second, without it, places the 48th line.
    /// </summary>
    [Fact]
    public void ACutRow_thatBegan_isResumedUnderAContextWithoutTheBox()
    {
        var t = Table();
        Tall(t, 48);
        var area = new LayoutBox(10, 10, 280, 580);
        var first = new TableRenderer(t).Layout(new LayoutContext(area) { FreshPageArea = area });

        var resumed = first.OverflowRenderer!.Layout(new LayoutContext(area));

        Assert.Equal(LayoutResult.Outcome.Partial, first.Status);
        Assert.Equal(LayoutResult.Outcome.Full, resumed.Status);
        Assert.Equal(20.0, resumed.OccupiedArea!.Value.Height, 0.001);
    }

    /// <summary>
    /// A resumed row in an area that holds no line of it is <c>Nothing</c>, not a slice with no
    /// line: a zero-line slice would be <c>Partial</c> with an overflow renderer for the same row,
    /// and the save would turn pages until the continuation limit.
    /// </summary>
    [Fact]
    public void ACutRow_resumedInAnAreaThatHoldsNoLine_isNothing()
    {
        var t = Table();
        Tall(t, 48);
        var area = new LayoutBox(10, 10, 280, 580);
        var first = new TableRenderer(t).Layout(new LayoutContext(area) { FreshPageArea = area });
        var tooShort = new LayoutBox(10, 10, 280, 10);

        var resumed = first.OverflowRenderer!.Layout(
            new LayoutContext(tooShort) { FreshPageArea = area });

        Assert.Equal(LayoutResult.Outcome.Nothing, resumed.Status);
    }

    /// <summary><c>WithArea</c> keeps the fresh-page box, so a wrapper that narrows the area does not lose it.</summary>
    [Fact]
    public void WithArea_keepsTheFreshPageBox()
    {
        var page = new LayoutBox(0, 0, 100, 100);
        var ctx = new LayoutContext(page) { FreshPageArea = page };

        Assert.Equal(page, ctx.WithArea(new LayoutBox(0, 0, 10, 10)).FreshPageArea);
        Assert.Null(new LayoutContext(page).WithArea(page).FreshPageArea);
    }

    /// <summary>
    /// A cut row resumed at another width is laid out from its first line like any other row: the lines
    /// it was wrapped into at the first width do not describe the second, so none is skipped.
    /// </summary>
    [Fact]
    public void ACutRow_resumedAtAnotherWidth_startsAgainFromItsFirstLine()
    {
        var t = Table();
        Tall(t, 48);
        var area = new LayoutBox(10, 10, 280, 580);
        var first = new TableRenderer(t).Layout(new LayoutContext(area) { FreshPageArea = area });

        var resumedHere = first.OverflowRenderer!.Layout(new LayoutContext(area) { FreshPageArea = area });
        var narrower = first.OverflowRenderer!.Layout(new LayoutContext(area.Deflate(0, 0, 100, 0)) { FreshPageArea = area });

        Assert.Equal(LayoutResult.Outcome.Full, resumedHere.Status);
        Assert.Equal(20.0, resumedHere.OccupiedArea!.Value.Height, 0.001);
        Assert.Equal(LayoutResult.Outcome.Partial, narrower.Status);
    }

    // ── The cost of a cut ────────────────────────────────────────────────────

    private static long AllocatedBySaving(int lines)
    {
        using var doc = Page();
        var t = Table();
        t.AddRow().AddCell(Words(lines));
        doc.Add(t);
        using var ms = new MemoryStream();

        // Per thread, not process-wide: the test host runs classes in parallel, and Save lays out and
        // writes on the calling thread.
        var before = GC.GetAllocatedBytesForCurrentThread();
        doc.Save(ms);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// One cell of 20,000 lines allocates about four times what one of 5,000 does. A row that was
    /// wrapped again on every page it spans would allocate with lines times pages, about sixteen times.
    /// </summary>
    [Fact]
    public void ACellOfManyLines_allocatesLinearlyInItsLines()
    {
        _ = AllocatedBySaving(500);

        var small = AllocatedBySaving(5_000);
        var large = AllocatedBySaving(20_000);

        var ratio = (double)large / small;
        Assert.True(
            ratio < 6.0,
            $"5,000 lines allocated {small / 1048576.0:F1} MiB and 20,000 lines {large / 1048576.0:F1} MiB, ratio {ratio:F2}.");
    }

    // ── Oracles ──────────────────────────────────────────────────────────────

    /// <summary>
    /// qpdf finds no errors in a cut row's file, and pdftotext reads the pages in order with the text
    /// of the long cell split where the cut was made: 47 words on page 1, one on page 2.
    /// </summary>
    [Fact]
    public void ACutRow_passesQpdf_andReadsInOrderThroughPdftotext()
    {
        using var doc = Page();
        var t = Table();
        t.AddRow().AddCell(string.Join(' ', Enumerable.Range(0, 48).Select(i => "w" + i.ToString("D2", CultureInfo.InvariantCulture)))).AddCell("short");
        doc.Add(t);
        var path = Path.Combine(_tempDir, "cut.pdf");
        File.WriteAllBytes(path, Save(doc));

        ExternalTool.TryRun("qpdf", ["--check", path], out var qexit, out var qout, out var qerr, out var qtimed);
        Assert.False(qtimed);
        Assert.True(qexit == 0, qout + qerr);
        Assert.Contains("No syntax or stream encoding errors found", qout, StringComparison.Ordinal);

        ExternalTool.TryRun("pdftotext", ["-f", "1", "-l", "1", path, "-"], out var e1, out var page1, out var err1, out _);
        ExternalTool.TryRun("pdftotext", ["-f", "2", "-l", "2", path, "-"], out var e2, out var page2, out var err2, out _);
        Assert.True(e1 == 0 && e2 == 0, err1 + err2);

        var words1 = page1.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words2 = page2.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Enumerable.Range(0, 47).Select(i => "w" + i.ToString("D2", CultureInfo.InvariantCulture)).Append("short"), words1.OrderBy(w => w == "short" ? 1 : 0).ThenBy(w => w, StringComparer.Ordinal));
        Assert.Equal(["w47"], words2);
    }

    /// <summary>
    /// A tagged PDF/UA-1 cut row in an embedded font passes veraPDF, and so does the same cut row
    /// under a header. The fixtures are asserted to hold the cut first: two Table elements.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATaggedCutRow_passesVeraPdfUa1(bool withHeader)
    {
        if (!File.Exists(DejaVu))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");

        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 600),
            Margins = new EdgeInsets(10),
            Conformance = PdfConformance.PdfUA1,
            Tagged = true,
            Language = "en-US",
        };
        doc.Info.Title = "Cut row";
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        var style = new TextStyle { FontRef = handle, FontSize = 10 };
        var t = new TableElement { DefaultCellStyle = style }.SetColumnWidths(60, 100);
        if (withHeader) t.AddHeaderRow().AddCell("H").AddCell("H2");
        Tall(t, 60);
        doc.Add(t);
        var bytes = Save(doc);
        var path = Path.Combine(_tempDir, "cut-ua1.pdf");
        File.WriteAllBytes(path, bytes);
        var flat = Regex.Replace(Encoding.Latin1.GetString(bytes), @"\s+", " ");
        Assert.True(PdfTestUtil.CountOccurrences(flat, "/S /Table") >= 2, "the row was not cut");

        AssertVeraPdf(path, "ua1");
    }

    /// <summary>An untagged PDF/A-2u cut row in an embedded font passes veraPDF.</summary>
    [Fact]
    public void ACutRow_passesVeraPdfPdfA2u()
    {
        if (!File.Exists(DejaVu))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");

        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 600),
            Margins = new EdgeInsets(10),
            Conformance = PdfConformance.PdfA2u,
        };
        doc.Info.Title = "Cut row";
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        var style = new TextStyle { FontRef = handle, FontSize = 10 };
        var t = new TableElement { DefaultCellStyle = style }.SetColumnWidths(60, 100);
        Tall(t, 60, Grey);
        doc.Add(t);
        var bytes = Save(doc);
        var path = Path.Combine(_tempDir, "cut-a2u.pdf");
        File.WriteAllBytes(path, bytes);
        Assert.True(Regex.Matches(Encoding.Latin1.GetString(bytes), @"/Type\s*/Page\b").Count >= 2, "the row was not cut");

        AssertVeraPdf(path, "2u");
    }

    private static void AssertVeraPdf(string path, string flavour)
    {
        ExternalTool.TryRun("verapdf", ["--flavour", flavour, path],
            out var vexit, out var report, out var verr, out var vtimed, timeoutMs: 120_000);
        Assert.False(vtimed);
        Assert.True(vexit is 0 or 1, $"veraPDF exited {vexit}\n{report}\n{verr}");
        Assert.True(report.Contains("isCompliant=\"true\"", StringComparison.Ordinal), report);
    }
}
