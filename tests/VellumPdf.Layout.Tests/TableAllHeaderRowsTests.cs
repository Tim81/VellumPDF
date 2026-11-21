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
/// A table whose rows are all headers has no repeatable header: the leading run of header rows
/// repeats only when a row that is not a header follows it, so an all-header table lays out and
/// draws as the table's body, each row once, and splits between rows like any other (#488).
///
/// Every fixture is a 300x600 page with 10pt margins and one Helvetica 10pt cell per row. A row
/// measures 20pt (12pt leading plus 4pt of padding above and below), so the 580pt content height
/// holds exactly 29 rows. A header row and a data row of the same text draw the same untagged
/// content, so the all-header and all-data tables are compared byte for byte.
/// </summary>
public sealed class TableAllHeaderRowsTests : IDisposable
{
    private const string TooTall =
        "An element is too tall to fit on a single page and cannot be rendered. " +
        "Reduce the element's content or increase the page size.";

    private static readonly string DejaVu = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    private readonly string _tempDir;

    public TableAllHeaderRowsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumallheader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private static TextStyle Style() => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = 10,
    };

    private static Document Page() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 600),
        Margins = new EdgeInsets(10),
    };

    /// <summary>
    /// A one-column table with one row per character of <paramref name="shape"/>: H for a header
    /// row, D for a data row. Row i holds <c>r{i}</c>.
    /// </summary>
    private static TableElement Shape(string shape, TextStyle? style = null)
    {
        var t = new TableElement { DefaultCellStyle = style ?? Style() };
        for (var i = 0; i < shape.Length; i++)
            t.AddRow(isHeader: shape[i] == 'H').AddCell("r" + i.ToString(CultureInfo.InvariantCulture));
        return t;
    }

    private static TableElement Rows(int count, bool header)
    {
        var t = new TableElement { DefaultCellStyle = Style() };
        for (var i = 0; i < count; i++)
            t.AddRow(isHeader: header).AddCell("r" + i.ToString(CultureInfo.InvariantCulture));
        return t;
    }

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The page content of a document holding only <paramref name="table"/>, all pages
    /// concatenated.
    /// </summary>
    private static string Content(TableElement table)
    {
        using var doc = Page();
        doc.Add(table);
        return PdfTestUtil.DecompressAllFlatStreams(Save(doc));
    }

    /// <summary>
    /// The shown text and position of every page, in page order, with an optional footer.
    /// </summary>
    private static List<List<ContentStreamReadback.TextPlacement>> PerPage(
        TableElement table, RunningBand? footer = null)
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 600), new EdgeInsets(10));
        if (footer is not null) renderer.Footer = footer;
        renderer.Add(new TableRenderer(table));
        using var ms = new MemoryStream();
        renderer.Render(ms);
        return [.. pdf.Pages.Select(p =>
            ContentStreamReadback.TextPlacements(Encoding.Latin1.GetString(p.ContentBytes!)))];
    }

    private static string[] Texts(IEnumerable<ContentStreamReadback.TextPlacement> placements) =>
        [.. placements.Select(p => p.Text)];

    private static string[] Range(int from, int to) =>
        [.. Enumerable.Range(from, to - from + 1).Select(i => "r" + i.ToString(CultureInfo.InvariantCulture))];

    private static int Occurrences(string haystack, string needle) =>
        PdfTestUtil.CountOccurrences(haystack, needle);

    private static string SaveFlattened(Document doc) =>
        Regex.Replace(Encoding.Latin1.GetString(Save(doc)), @"\s+", " ");

    // ── An all-header table that fits one page ────────────────────────────────

    /// <summary>
    /// H, HH and HHH threw "too tall" from the save, on any page size, because the header run
    /// covered every row and the fit loop never started. Each row is now drawn once, in order, at
    /// the positions data rows get: the first baseline is 576 (the 590 top edge less 4pt of padding
    /// and the 10pt font size) and each row is 20pt below the one before.
    /// </summary>
    [Theory]
    [InlineData("H")]
    [InlineData("HH")]
    [InlineData("HHH")]
    public void AllHeaderRows_fitOnePage_drawEachRowOnceInOrder(string shape)
    {
        var placements = Assert.Single(PerPage(Shape(shape)));

        Assert.Equal(Range(0, shape.Length - 1), Texts(placements));
        for (var i = 0; i < shape.Length; i++)
        {
            Assert.Equal(16.0, placements[i].X, 0.001);
            Assert.Equal(placements[0].Y - (20.0 * i), placements[i].Y, 0.001);
        }
        Assert.Equal(576.0, placements[0].Y, 0.001);
    }

    /// <summary>
    /// With no cell background, the content of an untagged all-header table equals that of the
    /// same rows as data rows, byte for byte: the flag changes nothing but the tag the cell gets.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(29)]
    public void AllHeaderRows_untagged_drawTheContentOfTheSameRowsAsDataRows(int count)
    {
        Assert.Equal(Content(Rows(count, header: false)), Content(Rows(count, header: true)));
    }

    // ── An all-header table taller than one page ─────────────────────────────

    /// <summary>
    /// 30 header rows of 20pt, 29 of which fit: page 1 holds r0 to r28 and page 2 holds r29 alone,
    /// at the position page 1's first row has. No row is repeated, because there is no header run.
    /// </summary>
    [Fact]
    public void AllHeaderRows_taller_thanAPage_splitBetweenRows_andRepeatNothing()
    {
        var pages = PerPage(Rows(30, header: true));

        Assert.Equal(2, pages.Count);
        Assert.Equal(Range(0, 28), Texts(pages[0]));
        Assert.Equal(["r29"], Texts(pages[1]));
        Assert.Equal(pages[0][0].Y, pages[1][0].Y, 0.001);
        Assert.Equal(pages[0][0].X, pages[1][0].X, 0.001);
    }

    /// <summary>
    /// The same rows as data rows split identically, so the split is the ordinary one.
    /// </summary>
    [Fact]
    public void AllHeaderRows_taller_thanAPage_splitWhereTheSameDataRowsDo()
    {
        var header = PerPage(Rows(61, header: true));
        var data = PerPage(Rows(61, header: false));

        Assert.Equal(3, header.Count);
        Assert.Equal(data.Select(Texts), header.Select(Texts));
        Assert.Equal(Range(58, 60), Texts(header[2]));
    }

    /// <summary>
    /// A footer makes the renderer count pages in a first pass, which reads the same row-fit
    /// result. {pages} must equal the page count the drawing pass produces, and the footer takes
    /// room from each page, so 28 rows fit rather than 29.
    /// </summary>
    [Fact]
    public void AllHeaderRows_withAFooter_countPagesInTheFirstPass()
    {
        var footer = new RunningBand("{page}/{pages}", style: new TextStyle
        {
            FontRef = new FontReference(Standard14.Helvetica),
            FontSize = 8,
        });

        var pages = PerPage(Rows(30, header: true), footer);

        // The footer takes room from each page, so 28 rows fit and r28 starts page 2.
        Assert.Equal(2, pages.Count);
        Assert.Equal(Range(0, 27), Texts(pages[0]).Where(t => t.StartsWith('r')));
        Assert.Equal(["1/2"], Texts(pages[0]).Where(t => !t.StartsWith('r')));
        Assert.Equal(["r28", "r29"], Texts(pages[1]).Where(t => t.StartsWith('r')));
        Assert.Equal(["2/2"], Texts(pages[1]).Where(t => !t.StartsWith('r')));
    }

    // ── Spans in an all-header table ─────────────────────────────────────────

    /// <summary>
    /// A row-spanning group in an all-header table is held together like one in the data rows: the
    /// break moves before it. Rows 28 and 29 form the group, so page 1 stops after r27 although a
    /// 29th row would fit, and page 2 draws the group once.
    /// </summary>
    [Fact]
    public void AllHeaderRows_aRowSpanGroup_isNotSplitAcrossPages()
    {
        var t = new TableElement { DefaultCellStyle = Style() };
        for (var i = 0; i < 28; i++)
            t.AddHeaderRow().AddCell("r" + i.ToString(CultureInfo.InvariantCulture));
        var first = t.AddHeaderRow();
        first.AddCell(new Cell("s28") { RowSpan = 2 });
        first.AddCell("r28");
        t.AddHeaderRow().AddCell("r29");

        var pages = PerPage(t);

        Assert.Equal(2, pages.Count);
        Assert.Equal(Range(0, 27), Texts(pages[0]));
        Assert.Equal(["s28", "r28", "r29"], Texts(pages[1]));
    }

    /// <summary>
    /// A group taller than the page stays refused, with the same message a data-row group gets,
    /// naming its rows: there is no page it could be drawn on whole.
    /// </summary>
    [Fact]
    public void AllHeaderRows_aRowSpanGroupTallerThanThePage_isStillRefused()
    {
        var t = new TableElement { DefaultCellStyle = Style() };
        var first = t.AddHeaderRow();
        first.AddCell(new Cell("s") { RowSpan = 40 });
        first.AddCell("r0");
        for (var i = 1; i < 40; i++)
            t.AddHeaderRow().AddCell("r" + i.ToString(CultureInfo.InvariantCulture));
        using var doc = Page();
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Equal(
            TooTall + " Table rows 0 to 39 are joined by a RowSpan, so they are never split between pages. " +
            "Together they are 800.0pt tall, but a page leaves 580.0pt.",
            ex.Message);
    }

    // ── What does not change ─────────────────────────────────────────────────

    /// <summary>
    /// A header run followed by data rows repeats on each page, as before: three rows (H D D) on
    /// one page draw as three data rows would, and 30 rows (H then 29 D) put the header at the top
    /// of page 2 above the one row that did not fit.
    /// </summary>
    [Fact]
    public void HeaderRunFollowedByDataRows_stillRepeats_andDrawsOnePageAsDataRowsDo()
    {
        Assert.Equal(Content(Rows(3, header: false)), Content(Shape("HDD")));

        var pages = PerPage(Shape("H" + new string('D', 29)));

        Assert.Equal(2, pages.Count);
        Assert.Equal(Range(0, 28), Texts(pages[0]));
        Assert.Equal(["r0", "r29"], Texts(pages[1]));
    }

    /// <summary>
    /// A header run followed by data rows and then a header row: the run still repeats, and the
    /// trailing header row is a body row that draws once. 32 rows put r0 to r28 on page 1 and the
    /// repeated r0 above r29 to r31 on page 2. A repeatable header found by "the last row is not a
    /// header" would find none here and draw r29 at the top of page 2 with no r0 above it.
    /// </summary>
    [Fact]
    public void HeaderRunWithATrailingHeaderRow_stillRepeats_acrossPages()
    {
        var pages = PerPage(Shape("H" + new string('D', 30) + "H"));

        Assert.Equal(2, pages.Count);
        Assert.Equal(Range(0, 28), Texts(pages[0]));
        Assert.Equal(["r0", "r29", "r30", "r31"], Texts(pages[1]));
        Assert.Equal(pages[0][0].Y, pages[1][0].Y, 0.001);
        Assert.Equal(pages[0][0].Y - 20.0, pages[1][1].Y, 0.001);
    }

    /// <summary>
    /// A header row after a data row draws once, where it is, and is not repeated.
    /// </summary>
    [Fact]
    public void HeaderRowAfterADataRow_drawsOnce()
    {
        var placements = ContentStreamReadback.TextPlacements(Content(Shape("HDH")));

        Assert.Equal(["r0", "r1", "r2"], Texts(placements));
        Assert.Equal(Content(Rows(3, header: false)), Content(Shape("HDH")));
    }

    private const string NoColumns =
        "A table resolved to no columns, so it has nothing to draw. " +
        "Add a row holding at least one cell.";

    /// <summary>
    /// A table with no rows, or with header rows that hold no cell, is still refused for having no
    /// columns, not for being too tall.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("H")]
    [InlineData("HHH")]
    public void NoCells_isStillRefusedForHavingNoColumns(string shape)
    {
        var t = new TableElement { DefaultCellStyle = Style() };
        foreach (var c in shape)
            t.AddRow(isHeader: c == 'H');
        using var doc = Page();
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Equal(NoColumns, ex.Message);
    }

    /// <summary>
    /// A start row past the last row of an all-header table still lays out as nothing, so a
    /// document saving it throws the "too tall" error.
    /// </summary>
    [Fact]
    public void StartRowPastTheLastRow_stillLaysOutAsNothing_andTheSaveThrows()
    {
        var t = Rows(3, header: true);
        var result = new TableRenderer(t, startRow: 3).Layout(
            new LayoutContext(new LayoutBox(10, 10, 280, 580)));

        Assert.Equal(LayoutResult.Outcome.Nothing, result.Status);

        using var doc = Page();
        doc.Add(new TableRenderer(Rows(3, header: true), startRow: 3));
        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Equal(TooTall, ex.Message);
    }

    /// <summary>
    /// <c>Draw</c> before <c>Layout</c> reads the row heights <c>Layout</c> computes. A table with
    /// a repeatable header indexes them for the header rows and throws
    /// <see cref="IndexOutOfRangeException"/>, as <c>Draw</c> documents; an all-header table has no
    /// header rows to index, and draws nothing because no area was laid out. The same holds for a
    /// table with no header row at all.
    /// </summary>
    [Theory]
    [InlineData("HDD", true)]
    [InlineData("HHH", false)]
    [InlineData("DDD", false)]
    public void Draw_beforeLayout_throwsOnlyWhenTheTableHasARepeatableHeader(
        string shape, bool throws)
    {
        using var pdf = new PdfDocument();
        var page = pdf.AddPage();
        var ctx = new DrawContext(new VellumPdf.Canvas.PdfCanvas(page), new LayoutBox(0, 0, 300, 600),
            new RendererContext(page, pdf), pdf, page);
        var renderer = new TableRenderer(Shape(shape));

        if (throws)
            Assert.Throws<IndexOutOfRangeException>(() => renderer.Draw(ctx));
        else
        {
            renderer.Draw(ctx);
            ctx.Canvas.Finish();
            Assert.Empty(page.ContentBytes!);
        }
    }

    // ── Tagging ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The rows are drawn as the body, but each still says it is a header: three TR, three TH with
    /// <c>/Scope /Column</c> and no TD.
    /// </summary>
    [Fact]
    public void AllHeaderRows_tagged_stillMarkEveryCellAsAColumnHeader()
    {
        using var doc = Page();
        doc.Tagged = true;
        doc.Language = "en-US";
        doc.Add(Shape("HHH"));

        var pdf = SaveFlattened(doc);

        Assert.Equal(1, Occurrences(pdf, "/S /Table"));
        Assert.Equal(3, Occurrences(pdf, "/S /TR"));
        Assert.Equal(3, Occurrences(pdf, "/S /TH"));
        Assert.Equal(0, Occurrences(pdf, "/S /TD"));
        Assert.Equal(3, Occurrences(pdf, "/Scope /Column"));
    }

    // ── A canvas refusal inside a table that is now drawn ────────────────────

    /// <summary>
    /// A non-finite cell background or border colour reaches the canvas once the table is drawn,
    /// and the canvas refuses it with <see cref="ArgumentException"/>, as it does for a table with
    /// data rows. Before the all-header table was drawable the save threw the "too tall"
    /// <see cref="InvalidOperationException"/> first.
    /// </summary>
    [Theory]
    [InlineData("backgroundNaN")]
    [InlineData("backgroundInfinity")]
    [InlineData("borderNaN")]
    public void AllHeaderRows_nonFiniteColour_isRefusedByTheCanvas(string input)
    {
        var t = new TableElement
        {
            DefaultCellStyle = Style(),
            BorderColor = input == "borderNaN" ? new ColorRgb(double.NaN, 0, 0) : ColorRgb.Black,
        };
        var cell = new Cell("r0");
        if (input == "backgroundNaN")
            cell = new Cell("r0") { Background = new ColorRgb(double.NaN, 0.5, 0.5) };
        else if (input == "backgroundInfinity")
            cell = new Cell("r0") { Background = new ColorRgb(0.5, double.PositiveInfinity, 0.5) };
        t.AddHeaderRow().AddCell(cell);
        using var doc = Page();
        doc.Add(t);

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));
        Assert.Null(ex.ParamName);
    }

    /// <summary>
    /// A NaN table margin reaches the canvas as a NaN coordinate once the table is drawn, so the
    /// canvas refuses it with <see cref="ArgumentException"/>, as it does for a table with data
    /// rows; an all-header table used to throw the "too tall" error instead.
    /// </summary>
    [Fact]
    public void AllHeaderRows_nanMargin_isRefusedByTheCanvas()
    {
        var t = new TableElement
        {
            DefaultCellStyle = Style(),
            Margins = new EdgeInsets(double.NaN),
        };
        t.AddHeaderRow().AddCell("r0");
        using var doc = Page();
        doc.Add(t);

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));
        Assert.Null(ex.ParamName);
    }

    // ── Oracles ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The 30-row table as pdftotext reads it: every row once and in order, with page 2 holding r29
    /// alone. The fixture is asserted to hold the change first, by counting the shown strings.
    /// </summary>
    [Fact]
    public void AllHeaderRows_untagged_pdftotextReadsEveryRowOnceInOrder()
    {
        using var doc = Page();
        doc.Add(Rows(30, header: true));
        var path = Path.Combine(_tempDir, "allheader30.pdf");
        File.WriteAllBytes(path, Save(doc));
        Assert.Equal(30, Occurrences(PdfTestUtil.DecompressAllFlatStreams(File.ReadAllBytes(path)), ") Tj"));

        ExternalTool.TryRun("pdftotext", ["-f", "1", "-l", "1", path, "-"],
            out var exit1, out var page1, out var err1, out _);
        ExternalTool.TryRun("pdftotext", ["-f", "2", "-l", "2", path, "-"],
            out var exit2, out var page2, out var err2, out _);

        Assert.True(exit1 == 0 && exit2 == 0, err1 + err2);
        Assert.Equal(Range(0, 28), Words(page1));
        Assert.Equal(["r29"], Words(page2));
    }

    private static string[] Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// A tagged PDF/UA-1 table of three header rows in an embedded font: qpdf finds no syntax or
    /// stream errors and veraPDF reports it compliant. The fixture is asserted to hold the three
    /// TH cells first.
    /// </summary>
    [Fact]
    public void AllHeaderRows_taggedPdfUa1_passesQpdfAndVeraPdf()
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
        doc.Info.Title = "All header rows";
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        doc.Add(Shape("HHH", new TextStyle { FontRef = handle, FontSize = 10 }));
        var bytes = Save(doc);
        var path = Path.Combine(_tempDir, "allheader-ua1.pdf");
        File.WriteAllBytes(path, bytes);
        Assert.Equal(3, Occurrences(Regex.Replace(Encoding.Latin1.GetString(bytes), @"\s+", " "), "/S /TH"));

        ExternalTool.TryRun("qpdf", ["--check", path], out var qexit, out var qout, out var qerr, out var qtimed);
        Assert.False(qtimed);
        Assert.True(qexit == 0, qout + qerr);
        Assert.Contains("No syntax or stream encoding errors found", qout, StringComparison.Ordinal);

        ExternalTool.TryRun("verapdf", ["--flavour", "ua1", path],
            out var vexit, out var report, out var verr, out var vtimed, timeoutMs: 120_000);
        Assert.False(vtimed);
        Assert.True(vexit is 0 or 1, $"veraPDF exited {vexit}\n{report}\n{verr}");
        Assert.True(report.Contains("isCompliant=\"true\"", StringComparison.Ordinal), report);
    }
}
