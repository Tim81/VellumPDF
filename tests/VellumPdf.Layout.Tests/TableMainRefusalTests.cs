// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
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
