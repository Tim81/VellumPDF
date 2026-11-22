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
/// A table that 2.3.3 refused stays refused, and a table that would need more page continuations
/// than one element may take with its breaks is laid out as 2.3.3 laid it out. "As 2.3.3 laid it
/// out" is the table measured with every cell as one line, which the tests reach through
/// <see cref="TableMeasureProbe.ReadEveryCellUnbroken"/>.
/// </summary>
public sealed class TableMainRefusalTests
{
    private static TextStyle Helvetica(double size = 10) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = size,
    };

    private static string W(int n) => new('W', n);

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static int PageCount(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b").Count;

    private static string[] Dropped(Document doc) =>
        [.. doc.Diagnostics
            .Where(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped)
            .Select(d => Regex.Match(d.Message, @"row \d+, cell \d+").Value)
            .OrderBy(m => int.Parse(Regex.Match(m, @"\d+").Value, CultureInfo.InvariantCulture))
            .ThenBy(m => m, StringComparer.Ordinal)];

    private static void RequireDejaVu()
    {
        if (!File.Exists(TableRandomDocuments.DejaVu))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");
    }

    // ── A table that was refused stays refused ──────────────────────────────────

    /// <summary>
    /// Two fixed columns of 100pt and an automatic one on a 400x300pt page. The group of rows 0 and
    /// 1 holds the 100pt cell that is tall when its column is narrow, and the cell of the automatic
    /// column in row 2 is a line of the given width and a break and a short line. Read unbroken
    /// that cell is as wide as both lines together, which leaves the fixed columns narrower, so the
    /// group is taller than a page leaves; with the break read, the widest line is shorter and the
    /// group fits.
    /// </summary>
    private static Document GroupTippedByTheWidthOfAnotherRow(int width)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 300), Margins = new EdgeInsets(10) };
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(100, 100);
        t.AddRow().AddCell(new Cell("lorem ipsum dolor sit amet consectetur") { RowSpan = 2 }).AddCell("a");
        t.AddRow().AddCell("b");
        t.AddRow().AddCell("c").AddCell("d").AddCell(W(width) + "\n" + W(4));
        doc.Add(t);
        return doc;
    }

    private static string RefusalOf(Func<Document> build, bool measuredAsOneLine)
    {
        using var doc = build();
        TableMeasureProbe.Current = new TableMeasureProbe { ReadEveryCellUnbroken = measuredAsOneLine };
        try
        {
            Save(doc);
            return "saved";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        finally
        {
            TableMeasureProbe.Current = null;
        }
    }

    /// <summary>
    /// A table that is refused when every cell is measured as one line is refused with the same
    /// message, though its breaks, read, leave the group room: the cell of the automatic column is
    /// as wide as both its lines together measured as one, which leaves the fixed columns
    /// narrower and the group taller than the 280pt a page leaves.
    /// </summary>
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(34)]
    public void ATableThatIsRefusedMeasuredAsOneLineIsRefusedWithTheSameMessage(int width)
    {
        var oneLine = RefusalOf(() => GroupTippedByTheWidthOfAnotherRow(width), measuredAsOneLine: true);
        var decided = RefusalOf(() => GroupTippedByTheWidthOfAnotherRow(width), measuredAsOneLine: false);

        Assert.Contains("RowSpan", oneLine, StringComparison.Ordinal);
        Assert.Equal(oneLine, decided);
    }

    /// <summary>
    /// The tables next to those saved measured as one line, and a table with a cell that has to
    /// give up its breaks is saved with it reported.
    /// </summary>
    [Theory]
    [InlineData(30, 0)]
    [InlineData(35, 1)]
    [InlineData(38, 1)]
    [InlineData(39, 0)]
    public void ATableThatSavesMeasuredAsOneLineStillSaves(int width, int reported)
    {
        using var doc = GroupTippedByTheWidthOfAnotherRow(width);
        TableMeasureProbe.Current = new TableMeasureProbe { ReadEveryCellUnbroken = true };
        try
        {
            Save(doc);
        }
        finally
        {
            TableMeasureProbe.Current = null;
        }

        using var decided = GroupTippedByTheWidthOfAnotherRow(width);
        var bytes = Save(decided);

        Assert.Equal(1, PageCount(bytes));
        Assert.Equal(reported, Dropped(decided).Length);
    }

    /// <summary>
    /// The decision measures a table with every cell as one line to see whether it is refused, and
    /// drops that measurement when it is not. A tab measured in an embedded font would add glyph 0
    /// to its subset, and its ToUnicode map would list it, though no tab is drawn.
    /// </summary>
    [Fact]
    public void AMeasurementThatIsDroppedAddsNothingToAnEmbeddedFont()
    {
        RequireDejaVu();
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 400),
            Margins = new EdgeInsets(10),
        };
        var font = doc.UseTrueTypeFont(File.ReadAllBytes(TableRandomDocuments.DejaVu));
        var t = new TableElement { DefaultCellStyle = new TextStyle { FontRef = font, FontSize = 12 } }
            .SetColumnWidths(100, 100);
        t.AddRow().AddCell("A\tB").AddCell("line1\nline2");
        doc.Add(t);

        var content = PdfTestUtil.DecompressAllFlatStreams(Save(doc));

        Assert.DoesNotContain("<0000> <FFFD>", content, StringComparison.Ordinal);
        Assert.Empty(Dropped(doc));
    }

    // ── A table that would need more page continuations than one element may ────

    private static readonly DateTimeOffset s_pin = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly byte[] s_pinId = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];

    private static string Words(int n) => string.Join("\n", Enumerable.Repeat("ab", n));

    /// <summary>
    /// One cell of <paramref name="words"/> two-letter words joined by line feeds, on a page whose
    /// content is one line of the cell tall. Read with its breaks the cell is a line to a page, and
    /// a page is a continuation. Measured as one line the text is wrapped at the column's width,
    /// which is a few hundred words to a line, and the cell takes a few hundred pages.
    /// </summary>
    private static Document OnePageALine(int words, bool footer = false, bool paragraphFirst = false, double columnWidth = 1900)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 2000, footer ? 60 : 40),
            Margins = new EdgeInsets(10),
            Timestamp = s_pin,
            DocumentId = s_pinId,
        };
        if (footer) doc.SetFooter("{page}", new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 8 });
        if (paragraphFirst) doc.Add(new Paragraph("x", Helvetica()));
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(columnWidth);
        t.AddRow().AddCell(Words(words));
        doc.Add(t);
        return doc;
    }

    private static byte[] SaveMeasuredAsOneLine(Func<Document> build)
    {
        using var doc = build();
        TableMeasureProbe.Current = new TableMeasureProbe { ReadEveryCellUnbroken = true };
        try
        {
            return Save(doc);
        }
        finally
        {
            TableMeasureProbe.Current = null;
        }
    }

    /// <summary>
    /// A cell that takes the limit of 50,000 page continuations exactly keeps its breaks: a page
    /// to a line, 50,001 pages, and nothing is given up.
    /// </summary>
    [Fact]
    public void ATableThatTakesExactlyTheLimitKeepsItsBreaks()
    {
        using var doc = OnePageALine(50_001);

        var bytes = Save(doc);

        Assert.Equal(50_001, PageCount(bytes));
        Assert.Empty(Dropped(doc));
    }

    /// <summary>
    /// A cell that would take one more is laid out as it was when every cell was measured as one
    /// line: the same bytes that measurement saves, and the cell is reported with the reason.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ATableThatWouldTakeOneContinuationTooManyIsLaidOutAsOneLine(bool footer, bool paragraphFirst)
    {
        // After a paragraph that leaves no room for a line, the table moves to the next page,
        // which is a continuation of its own, so 50,001 lines are one too many there.
        var words = paragraphFirst ? 50_001 : 50_002;
        using var doc = OnePageALine(words, footer, paragraphFirst);

        var bytes = Save(doc);

        var expected = SaveMeasuredAsOneLine(() => OnePageALine(words, footer, paragraphFirst));
        Assert.Equal(expected, bytes);
        Assert.True(PageCount(bytes) < 1000, $"{PageCount(bytes)} pages");
        Assert.Equal(["row 0, cell 0"], Dropped(doc));
        var report = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped);
        Assert.Contains("more page continuations than one element may take", report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same cell measured as one line takes the page continuations too, and is refused with the
    /// message that names the limit, as it is when it is measured as one line.
    /// </summary>
    [Fact]
    public void ATableThatTakesTooManyContinuationsMeasuredAsOneLineIsRefusedWithThatMessage()
    {
        // A column 30pt wide wraps the text into lines of three characters.
        var oneLine = RefusalOf(() => OnePageALine(120_000, columnWidth: 30), measuredAsOneLine: true);
        var decided = RefusalOf(() => OnePageALine(120_000, columnWidth: 30), measuredAsOneLine: false);

        Assert.Contains("more than 50000 page continuations", oneLine, StringComparison.Ordinal);
        Assert.Equal(oneLine, decided);
    }

    /// <summary>
    /// The cell of 2,350,048 two-letter words in a 1900pt column on a 2000 x 600pt page, which
    /// saved in 295 pages as one line and takes more than 50,000 pages with its breaks.
    /// </summary>
    [Fact]
    public void ALargeCellThatWouldTakeMoreThanTheLimitIsLaidOutAsOneLine()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 2000, 600),
            Margins = new EdgeInsets(10),
            Timestamp = s_pin,
            DocumentId = s_pinId,
        };
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(1900);
        t.AddRow().AddCell(Words(2_350_048));
        doc.Add(t);

        var bytes = Save(doc);

        Assert.Equal(295, PageCount(bytes));
        Assert.Equal(["row 0, cell 0"], Dropped(doc));
    }

    /// <summary>
    /// The bound the document asks for is never below the continuations a table takes: over random
    /// tables on short pages, where rows are cut between their lines, and rows and groups move to
    /// the next page.
    /// </summary>
    [Fact]
    public void TheContinuationBoundIsNeverBelowTheContinuationsATableTakes()
    {
        var most = 0L;
        for (var seed = 0; seed < 400; seed++)
        {
            var rng = new Random(seed);
            var area = new LayoutBox(10, 10, rng.Next(150, 500), rng.Next(60, 200));
            var t = new TableElement { DefaultCellStyle = Helvetica() };
            var cols = rng.Next(1, 4);
            if (rng.Next(2) == 0) t.SetColumnWidths([.. Enumerable.Range(0, cols).Select(_ => (double)rng.Next(30, 120))]);
            for (var r = rng.Next(1, 30); r > 0; r--)
            {
                var row = t.AddRow(isHeader: r == 1 && rng.Next(4) == 0);
                for (var c = 0; c < cols; c++)
                {
                    var lines = rng.Next(1, rng.Next(6) == 0 ? 40 : 4);
                    var text = string.Join("\n", Enumerable.Range(0, lines).Select(_ => "w" + W(rng.Next(1, 12))));
                    row.AddCell(new Cell(text) { RowSpan = rng.Next(8) == 0 ? 2 : 1 });
                }
            }

            // Laid out the way the document does: a Partial each time, the overflow in the area of
            // the next page.
            var ctx = new LayoutContext(area) { FreshPageArea = area };
            IRenderer renderer = new TableRenderer(t);
            var continuations = 0L;
            long bound;
            try
            {
                var result = renderer.Layout(ctx);
                bound = ((Rendering.IContinuationGuard)renderer).ContinuationBound;
                while (result.Status == LayoutResult.Outcome.Partial)
                {
                    continuations++;
                    renderer = result.OverflowRenderer!;
                    result = renderer.Layout(ctx);
                }

                if (result.Status == LayoutResult.Outcome.Nothing) continue;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            Assert.True(bound >= continuations, $"seed {seed}: bound {bound}, continuations {continuations}");
            most = Math.Max(most, continuations);
        }

        // Not vacuous: tables here take many pages.
        Assert.True(most > 10, $"the most continuations over the tables was {most}");
    }

    private static Document Shaped(int seed)
    {
        var rng = new Random(seed);
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 300), Margins = new EdgeInsets(10) };
        var t = new TableElement { DefaultCellStyle = Helvetica() };
        _ = rng.Next(3) switch
        {
            0 => t.SetColumnWidths(100, 100),
            1 => t.SetColumnWidths(rng.Next(60, 140), 0, 0),
            _ => t.SetColumnWidths(rng.Next(60, 140), rng.Next(60, 140), 0),
        };
        var header = rng.Next(3) == 0;
        var spanText = string.Join(rng.Next(4) == 0 ? "\n" : " ", Enumerable.Repeat("lorem ipsum", rng.Next(1, 5)));
        var row0 = t.AddRow(header);
        row0.AddCell(header ? new Cell(spanText) : new Cell(spanText) { RowSpan = 2 }).AddCell("a");
        t.AddRow().AddCell("b");
        var sep = rng.Next(3) switch { 0 => "\n", 1 => "\t", _ => "\r\n" };
        t.AddRow().AddCell("c").AddCell("d").AddCell(W(rng.Next(0, 45)) + sep + W(rng.Next(0, 12)));
        if (rng.Next(2) == 0) t.AddRow().AddCell("e\nf").AddCell(W(rng.Next(0, 30)) + "\n" + W(rng.Next(0, 30)));
        doc.Add(t);
        return doc;
    }

    /// <summary>
    /// A document that is refused with every cell measured as one line is refused: over shaped
    /// tables, where most of one family is refused and the other saves, and over the long random
    /// tables of header rows, spans and fixed, automatic, zero, negative and NaN widths.
    /// </summary>
    [Fact]
    public void ADocumentThatIsRefusedMeasuredAsOneLineIsRefused()
    {
        RequireDejaVu();
        var refusedShaped = 0;
        for (var seed = 0; seed < 600; seed++)
        {
            var s = seed;
            var oneLine = RefusalOf(() => Shaped(s), measuredAsOneLine: true);
            if (oneLine == "saved") continue;
            refusedShaped++;
            Assert.Equal(oneLine, RefusalOf(() => Shaped(s), measuredAsOneLine: false));
        }

        var refusedLong = 0;
        for (var seed = 0; seed < 250; seed++)
        {
            var s = seed;
            var oneLine = RefusalOf(() => TableRandomDocuments.Long(s), measuredAsOneLine: true);
            if (oneLine == "saved") continue;
            refusedLong++;
            Assert.Equal(oneLine, RefusalOf(() => TableRandomDocuments.Long(s), measuredAsOneLine: false));
        }

        // The property is not vacuous: each family has tables that are refused measured as one line.
        Assert.True(refusedShaped > 20, $"only {refusedShaped} shaped tables are refused measured as one line");
        Assert.True(refusedLong > 20, $"only {refusedLong} long tables are refused measured as one line");
    }
}
