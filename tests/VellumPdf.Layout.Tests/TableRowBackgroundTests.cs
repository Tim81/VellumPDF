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
/// <c>TableElement.Add(Row)</c> is the one way to give a row a <c>Background</c>; the rows
/// <c>AddRow</c> and <c>AddHeaderRow</c> create have none (#543).
///
/// Every fixture is a 300x600 page with 10pt margins, so the top edge is at y = 590 in PDF space,
/// and a table with explicit column widths of 100 and 80 starts at x = 10 and ends at x = 190. A
/// Helvetica 10pt cell is 20pt tall (12pt leading plus 4pt of padding above and below), so row i
/// occupies y from 570 - 20 i to 590 - 20 i.
/// </summary>
public sealed class TableRowBackgroundTests : IDisposable
{
    private static readonly string DejaVu = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    private static readonly ColorRgb Grey = new(0.9, 0.8, 0.7);
    private static readonly ColorRgb Blue = new(0.1, 0.2, 0.3);
    private static readonly ColorRgb Green = new(0.4, 0.5, 0.6);

    private readonly string _tempDir;

    public TableRowBackgroundTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumrowbg_{Guid.NewGuid():N}");
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

    private static TableElement Table() =>
        new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(100, 80);

    private static Document Page(PdfConformance conformance = PdfConformance.None, bool tagged = false) => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 600),
        Margins = new EdgeInsets(10),
        Conformance = conformance,
        Tagged = tagged,
        Language = tagged ? "en-US" : null,
        Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        DocumentId = new byte[16],
    };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Content(TableElement table, bool tagged = false)
    {
        using var doc = Page(tagged: tagged);
        doc.Add(table);
        return PdfTestUtil.DecompressAllFlatStreams(Save(doc));
    }

    private static List<string> PageContents(TableElement table)
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 600), new EdgeInsets(10));
        renderer.Add(new TableRenderer(table));
        using var ms = new MemoryStream();
        renderer.Render(ms);
        return [.. pdf.Pages.Select(p => Encoding.Latin1.GetString(p.ContentBytes!))];
    }

    private static string Num(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string Fill(ColorRgb c) => $"{Num(c.R)} {Num(c.G)} {Num(c.B)} rg";

    private static Row Coloured(ColorRgb? background, params string[] texts)
    {
        var row = new Row { Background = background };
        foreach (var text in texts) row.AddCell(text);
        return row;
    }

    private static readonly Regex FillRect = new(
        @"(?<c>\S+ \S+ \S+) rg\s+(?<r>\S+ \S+ \S+ \S+) re\s+f\b", RegexOptions.CultureInvariant);

    /// <summary>Every filled rectangle, as "r g b rg|x y w h", in drawing order.</summary>
    private static string[] Fills(string content) =>
        [.. FillRect.Matches(content).Select(m => m.Groups["c"].Value + " rg|" + m.Groups["r"].Value)];

    // ── What is drawn ────────────────────────────────────────────────────────

    /// <summary>
    /// A two-cell row with a background fills each cell's own rectangle with it, as three
    /// operators in a fixed order: the colour, the rectangle, the fill. The fixture first proves it
    /// holds the colour at all, because "0.9 0.8 0.7 rg" is also the form a text colour takes.
    /// </summary>
    [Fact]
    public void RowBackground_fillsEachCellRectangle_withTheRowColour()
    {
        var table = Table();
        table.Add(Coloured(Grey, "a", "b"));

        var content = Content(table);

        Assert.Equal(
            ["0.9 0.8 0.7 rg|10 570 100 20", "0.9 0.8 0.7 rg|110 570 80 20"],
            Fills(content));
        Assert.Contains("0.9 0.8 0.7 rg\n10 570 100 20 re\nf\n", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Per cell the order is fill, border, text: the border and the text are painted over the fill.
    /// </summary>
    [Fact]
    public void RowBackground_isDrawnBeforeTheCellBorderAndText()
    {
        var table = Table();
        table.Add(Coloured(Grey, "a"));

        var content = Content(table);

        var fill = content.IndexOf("0.9 0.8 0.7 rg\n10 570 100 20 re\nf", StringComparison.Ordinal);
        var border = content.IndexOf("10 570 100 20 re\nS", StringComparison.Ordinal);
        var text = content.IndexOf("(a) Tj", StringComparison.Ordinal);
        Assert.True(fill >= 0 && border > fill && text > border, $"{fill} {border} {text}\n{content}");
    }

    /// <summary>
    /// A cell's own background is drawn instead of the row's, for that cell only: the row colour
    /// appears once, for the other cell, and nothing is filled twice.
    /// </summary>
    [Fact]
    public void CellBackground_winsOverRowBackground_forThatCellOnly()
    {
        var table = Table();
        var row = new Row { Background = Grey };
        row.AddCell(new Cell("a") { Background = Blue });
        row.AddCell("b");
        table.Add(row);

        Assert.Equal(
            ["0.1 0.2 0.3 rg|10 570 100 20", "0.9 0.8 0.7 rg|110 570 80 20"],
            Fills(Content(table)));
    }

    /// <summary>
    /// Rows are independent: each fill is at its own row's position, in row order, and a row with
    /// no background in between gets none.
    /// </summary>
    [Fact]
    public void RowBackground_isPerRow_atEachRowsPosition()
    {
        var table = Table();
        table.Add(Coloured(Grey, "a", "b"));
        table.AddRow().AddCell("c").AddCell("d");
        table.Add(Coloured(Green, "e", "f"));

        Assert.Equal(
            [
                "0.9 0.8 0.7 rg|10 570 100 20", "0.9 0.8 0.7 rg|110 570 80 20",
                "0.4 0.5 0.6 rg|10 530 100 20", "0.4 0.5 0.6 rg|110 530 80 20",
            ],
            Fills(Content(table)));
    }

    /// <summary>
    /// A row shorter than the table fills only the cells it has; the column it has no cell for is
    /// left empty.
    /// </summary>
    [Fact]
    public void RowBackground_inAShortRow_fillsOnlyTheCellsTheRowHas()
    {
        var table = Table();
        table.AddRow().AddCell("a").AddCell("b");
        table.Add(Coloured(Grey, "c"));

        Assert.Equal(["0.9 0.8 0.7 rg|10 550 100 20"], Fills(Content(table)));
    }

    /// <summary>
    /// A cell spanning two rows is filled over both rows with the colour of the row it starts in;
    /// the row below keeps its own colour for the cell it holds, and gets no fill for the column
    /// the
    /// span covers.
    /// </summary>
    [Fact]
    public void RowBackground_withARowSpan_fillsTheSpanWithTheOriginRowsColour()
    {
        var table = Table();
        var first = new Row { Background = Grey };
        first.AddCell(new Cell("a") { RowSpan = 2 });
        first.AddCell("b");
        table.Add(first);
        table.Add(Coloured(Green, "c"));

        Assert.Equal(
            [
                "0.9 0.8 0.7 rg|10 550 100 40",
                "0.9 0.8 0.7 rg|110 570 80 20",
                "0.4 0.5 0.6 rg|110 550 80 20",
            ],
            Fills(Content(table)));
    }

    /// <summary>
    /// A cell that spans both columns and has no background of its own is filled over its whole
    /// width, 100 + 80 = 180pt, not over one column.
    /// </summary>
    [Fact]
    public void RowBackground_withAColSpan_fillsTheSpannedWidth()
    {
        var table = Table();
        table.Add(new Row { Background = Grey }.AddCell(new Cell("wide") { ColSpan = 2 }));

        Assert.Equal(["0.9 0.8 0.7 rg|10 570 180 20"], Fills(Content(table)));
    }

    /// <summary>
    /// A tagged table writes the fill as an artifact, like the cell fill, so the fill is not
    /// content
    /// the structure tree has to account for.
    /// </summary>
    [Fact]
    public void RowBackground_inATaggedTable_isWrittenAsAnArtifact()
    {
        var table = Table();
        table.Add(Coloured(Grey, "a"));

        var content = Content(table, tagged: true);

        Assert.Contains("/Artifact BMC\n0.9 0.8 0.7 rg\n10 570 100 20 re\nf\nEMC\n", content, StringComparison.Ordinal);
    }

    // ── Pages ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A leading header row with a background repeats with it on every continuation page, at the
    /// same position as on page 1, ahead of that page's first data row.
    /// </summary>
    [Fact]
    public void RowBackground_onARepeatedHeaderRow_isDrawnOnEveryPage()
    {
        var table = Table();
        var header = new Row { IsHeader = true, Background = Grey };
        header.AddCell("h1").AddCell("h2");
        table.Add(header);
        for (var i = 0; i < 60; i++) table.AddRow().AddCell("d" + i).AddCell("x");

        var pages = PageContents(table);

        Assert.True(pages.Count >= 3, pages.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var page in pages)
        {
            Assert.Equal(
                ["0.9 0.8 0.7 rg|10 570 100 20", "0.9 0.8 0.7 rg|110 570 80 20"],
                Fills(page));
            Assert.True(
                page.IndexOf("(h1) Tj", StringComparison.Ordinal) > page.IndexOf("0.9 0.8 0.7 rg", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A data row's background follows the row across a page break: the rows that do not fit move
    /// to the next page, and their fill goes with them.
    /// </summary>
    [Fact]
    public void RowBackground_onADataRowAfterAPageBreak_isDrawnOnThePageTheRowIsOn()
    {
        var table = Table();
        for (var i = 0; i < 29; i++) table.AddRow().AddCell("d" + i);
        table.Add(Coloured(Grey, "last"));

        var pages = PageContents(table);

        Assert.Equal(2, pages.Count);
        Assert.Empty(Fills(pages[0]));
        Assert.Equal(["0.9 0.8 0.7 rg|10 570 100 20"], Fills(pages[1]));
    }

    // ── What Add(Row) accepts ────────────────────────────────────────────────

    /// <summary>A null background gives the content of <c>AddRow()</c>, byte for byte.</summary>
    [Fact]
    public void Add_aRowWithNoBackground_savesTheBytesOfAddRow()
    {
        var viaAdd = Table();
        viaAdd.Add(Coloured(null, "a", "b"));
        var viaAddRow = Table();
        viaAddRow.AddRow().AddCell("a").AddCell("b");

        using var d1 = Page();
        d1.Add(viaAdd);
        using var d2 = Page();
        d2.Add(viaAddRow);

        Assert.Equal(Save(d2), Save(d1));
        Assert.Empty(Fills(Content(viaAdd)));
    }

    /// <summary>A header row built by hand and added keeps its flag and its fill.</summary>
    [Fact]
    public void Add_aHeaderRow_keepsTheFlagAndTheFill()
    {
        var table = Table();
        var row = table.Add(new Row { IsHeader = true, Background = Grey }.AddCell("h"));

        Assert.True(row.IsHeader);
        Assert.Equal(["0.9 0.8 0.7 rg|10 570 100 20"], Fills(Content(table)));
    }

    /// <summary>
    /// The table holds the row itself: it returns the instance, a cell added afterwards is drawn,
    /// and adding the same instance twice draws it twice.
    /// </summary>
    [Fact]
    public void Add_holdsTheRowByReference()
    {
        var table = Table();
        var row = new Row { Background = Grey };
        var returned = table.Add(row);
        row.AddCell("late");
        table.Add(row);

        Assert.Same(row, returned);
        Assert.Same(row, table.Rows[0]);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(
            ["0.9 0.8 0.7 rg|10 570 100 20", "0.9 0.8 0.7 rg|10 550 100 20"],
            Fills(Content(table)));
    }

    /// <summary>A row with no cells is accepted, as <c>AddRow()</c> accepts one.</summary>
    [Fact]
    public void Add_anEmptyRow_isAccepted_andDrawsNothing()
    {
        var table = Table();
        table.Add(new Row { Background = Grey });
        table.AddRow().AddCell("a");

        Assert.Equal(2, table.Rows.Count);
        Assert.Empty(Fills(Content(table)));
    }

    /// <summary>
    /// Channels of exactly 0 and exactly 1 are in the DeviceRGB range, in every channel and
    /// together.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(1.0, 1.0, 1.0)]
    [InlineData(1.0, 0.0, 0.0)]
    [InlineData(0.0, 1.0, 0.0)]
    [InlineData(0.0, 0.0, 1.0)]
    public void Add_acceptsChannelsAtTheRangeEnds(double r, double g, double b)
    {
        var table = Table();
        table.Add(Coloured(new ColorRgb(r, g, b), "a"));

        Assert.Single(table.Rows);
        Assert.Equal(
            [$"{Num(r)} {Num(g)} {Num(b)} rg|10 570 100 20"],
            Fills(Content(table)));
    }

    /// <summary>The overloads of <c>AddRow</c> still bind to the flag, and still add a row with no
    /// fill.</summary>
    [Fact]
    public void AddRow_overloads_areUnchanged()
    {
        var table = Table();
        var plain = table.AddRow();
        var header = table.AddRow(true);
        var named = table.AddRow(isHeader: true);
        var dedicated = table.AddHeaderRow();

        Assert.False(plain.IsHeader);
        Assert.True(header.IsHeader && named.IsHeader && dedicated.IsHeader);
        Assert.All(table.Rows, r => Assert.Null(r.Background));
    }

    // ── What Add(Row) refuses ────────────────────────────────────────────────

    [Fact]
    public void Add_aNullRow_throwsArgumentNullException_andLeavesTheTable()
    {
        var table = Table();
        table.AddRow().AddCell("a");

        var ex = Assert.Throws<ArgumentNullException>(() => table.Add(null!));

        Assert.Equal("row", ex.ParamName);
        Assert.Single(table.Rows);
    }

    public static TheoryData<int, double, string, string> Refused()
    {
        var data = new TheoryData<int, double, string, string>();
        double[] values =
        [
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1e-9, 1.000001,
        ];
        string[] names = ["red", "green", "blue"];
        string[] texts = ["NaN", "positive infinity", "negative infinity", "-1E-09", "1.000001"];
        for (var channel = 0; channel < 3; channel++)
            for (var i = 0; i < values.Length; i++)
                data.Add(channel, values[i], names[channel], texts[i]);
        return data;
    }

    /// <summary>
    /// Each refused value, in each channel: <c>ArgumentOutOfRangeException</c> from <c>Add</c>
    /// itself,
    /// <c>ParamName</c> <c>row</c>, <c>ActualValue</c> the channel's value, a message naming the
    /// channel,
    /// the value and the clause, and a table that is exactly as it was before the call, so a save
    /// afterwards is the save of the table without the row.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public void Add_aBackgroundOutsideTheRange_throwsAtTheCall_andLeavesTheTable(
        int channel, double value, string channelName, string valueText)
    {
        double[] rgb = [0.5, 0.5, 0.5];
        rgb[channel] = value;
        var table = Table();
        table.Add(Coloured(Grey, "kept"));
        var before = Content(table);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => table.Add(Coloured(new ColorRgb(rgb[0], rgb[1], rgb[2]), "refused")));

        Assert.Equal("row", ex.ParamName);
        Assert.Equal(value, Assert.IsType<double>(ex.ActualValue));
        Assert.StartsWith(
            $"The row's background has a {channelName} channel of {valueText}. " +
            "ISO 32000-2:2020, 8.6.4.3 says each DeviceRGB component is a number from 0.0 to 1.0.",
            ex.Message, StringComparison.Ordinal);
        Assert.Single(table.Rows);
        Assert.Equal(before, Content(table));
    }

    /// <summary>
    /// With two channels out of range the first in red, green, blue order is the one named, so the
    /// message does not depend on which was set last.
    /// </summary>
    [Fact]
    public void Add_withTwoBadChannels_namesTheFirst()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Table().Add(Coloured(new ColorRgb(0.5, 2, double.NaN), "x")));

        Assert.Contains("green channel of 2", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bad colour on a row never passed to <c>Add</c> is not checked: the row is not part of any
    /// table, so nothing writes it.
    /// </summary>
    [Fact]
    public void Row_withAnOutOfRangeBackground_isNotRefusedUntilItIsAdded()
    {
        var row = new Row { Background = new ColorRgb(double.NaN, 0, 0) };
        row.AddCell("x");
        Assert.Equal(double.NaN, row.Background!.Value.R);
    }

    // ── Oracles ──────────────────────────────────────────────────────────────

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// pdftoppm paints the fill: a pixel inside each filled cell, clear of the border and the text,
    /// has the colour, and a pixel in a cell of a row with no background is white. The fixture is
    /// asserted to hold the fill operators first.
    /// </summary>
    [Fact]
    public void RowBackground_isVisible_inTheRenderedPage()
    {
        var table = Table();
        table.Add(Coloured(new ColorRgb(1, 0, 0), "a", "b"));
        table.AddRow().AddCell("c").AddCell("d");
        using var doc = Page();
        doc.Add(table);
        var bytes = Save(doc);
        Assert.Equal(
            ["1 0 0 rg|10 570 100 20", "1 0 0 rg|110 570 80 20"],
            Fills(PdfTestUtil.DecompressAllFlatStreams(bytes)));
        var pdf = Write("render.pdf", bytes);
        var outBase = Path.Combine(_tempDir, "render");

        ExternalTool.TryRun("pdftoppm", ["-r", "72", "-singlefile", pdf, outBase],
            out var exit, out _, out var err, out var timedOut);

        Assert.True(exit == 0 && !timedOut, err);
        var (width, pixels) = ReadPpm(File.ReadAllBytes(outBase + ".ppm"));
        // The page is 300x600 at 72 dpi, so one pixel is one point and the top edge is row 10.
        // Row 0 spans pixel rows 10 to 30, row 1 spans 30 to 50. Probe 2pt inside the corner:
        // clear of the text, which starts 6pt in and 4pt down, and of the 0.5pt border.
        Assert.Equal(300, width);
        Assert.Equal((255, 0, 0), Pixel(pixels, width, 12, 12));
        Assert.Equal((255, 0, 0), Pixel(pixels, width, 112, 12));
        Assert.Equal((255, 255, 255), Pixel(pixels, width, 12, 32));
        Assert.Equal((255, 255, 255), Pixel(pixels, width, 112, 32));
    }

    private static (int Width, byte[] Pixels) ReadPpm(byte[] ppm)
    {
        var pos = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)ppm[pos])) pos++;
            var start = pos;
            while (!char.IsWhiteSpace((char)ppm[pos])) pos++;
            return Encoding.ASCII.GetString(ppm, start, pos - start);
        }
        Assert.Equal("P6", Token());
        var w = int.Parse(Token(), CultureInfo.InvariantCulture);
        var h = int.Parse(Token(), CultureInfo.InvariantCulture);
        Assert.Equal("255", Token());
        pos++;
        Assert.Equal(w * h * 3, ppm.Length - pos);
        return (w, ppm[pos..]);
    }

    private static (int R, int G, int B) Pixel(byte[] pixels, int width, int x, int y)
    {
        var i = ((y * width) + x) * 3;
        return (pixels[i], pixels[i + 1], pixels[i + 2]);
    }

    /// <summary>
    /// pdftotext reads the text of a table whose rows are coloured, one word per cell and in order,
    /// so the fills did not take any text out of the page.
    /// </summary>
    [Fact]
    public void RowBackground_pdftotextStillReadsEveryCell()
    {
        var table = Table();
        table.Add(Coloured(Grey, "alpha", "beta"));
        table.Add(Coloured(Green, "gamma", "delta"));
        using var doc = Page();
        doc.Add(table);
        var bytes = Save(doc);
        Assert.Equal(2, Fills(PdfTestUtil.DecompressAllFlatStreams(bytes)).Count(f => f.StartsWith("0.9", StringComparison.Ordinal)));
        var path = Write("text.pdf", bytes);

        ExternalTool.TryRun("pdftotext", [path, "-"], out var exit, out var text, out var err, out _);

        Assert.True(exit == 0, err);
        Assert.Equal(["alpha", "beta", "gamma", "delta"], text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A tagged PDF/UA-1 table whose header and body rows are coloured: qpdf finds no syntax or
    /// stream errors and veraPDF reports it compliant, which holds the fills to the artifact rule.
    /// The fixture is asserted to hold both fills, inside artifacts, first.
    /// </summary>
    [Fact]
    public void RowBackground_inATaggedPdfUa1Table_passesQpdfAndVeraPdf()
    {
        if (!File.Exists(DejaVu))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");

        using var doc = Page(PdfConformance.PdfUA1, tagged: true);
        doc.Info.Title = "Row background";
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        var table = new TableElement { DefaultCellStyle = new TextStyle { FontRef = handle, FontSize = 10 } }
            .SetColumnWidths(100, 80);
        table.Add(new Row { IsHeader = true, Background = Grey }.AddCell("Name").AddCell("Value"));
        table.Add(Coloured(Green, "one", "1"));
        doc.Add(table);
        var bytes = Save(doc);
        var content = PdfTestUtil.DecompressAllFlatStreams(bytes);
        Assert.Contains("/Artifact BMC\n0.9 0.8 0.7 rg\n10 570 100 20 re\nf\nEMC", content, StringComparison.Ordinal);
        Assert.Contains("/Artifact BMC\n0.4 0.5 0.6 rg\n10 550 100 20 re\nf\nEMC", content, StringComparison.Ordinal);
        var path = Write("rowbg-ua1.pdf", bytes);

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

    /// <summary>
    /// A tagged PDF/A-2u table in an embedded font whose row is coloured passes veraPDF. The
    /// fixture
    /// is asserted to hold the fill first.
    /// </summary>
    [Fact]
    public void RowBackground_inAPdfA2uTable_passesVeraPdf()
    {
        if (!File.Exists(DejaVu))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");

        using var doc = Page(PdfConformance.PdfA2u, tagged: true);
        doc.Info.Title = "Row background";
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        var table = new TableElement { DefaultCellStyle = new TextStyle { FontRef = handle, FontSize = 10 } }
            .SetColumnWidths(100, 80);
        table.Add(Coloured(Grey, "one", "1"));
        doc.Add(table);
        var bytes = Save(doc);
        Assert.Contains("0.9 0.8 0.7 rg\n10 570 100 20 re\nf", PdfTestUtil.DecompressAllFlatStreams(bytes), StringComparison.Ordinal);
        var path = Write("rowbg-2u.pdf", bytes);

        ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
            out var vexit, out var report, out var verr, out var vtimed, timeoutMs: 120_000);
        Assert.False(vtimed);
        Assert.True(vexit is 0 or 1, $"veraPDF exited {vexit}\n{report}\n{verr}");
        Assert.True(report.Contains("isCompliant=\"true\"", StringComparison.Ordinal), report);
    }
}
