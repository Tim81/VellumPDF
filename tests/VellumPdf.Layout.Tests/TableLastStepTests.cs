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
/// The last step of the decision that reads some table cells unbroken: a header or a group that
/// still has no room takes every control-white-space cell in an automatically sized column, so a
/// table that fits when every cell is measured as one line is not refused for what the breaks do
/// to the widths. The fixtures are 400x300pt with 10pt margins in Helvetica 10pt, where a line is
/// 12pt and a "W" is 9.44pt wide.
/// </summary>
public sealed class TableLastStepTests
{
    private const string Lorem = "lorem ipsum dolor sit amet consectetur";

    private static readonly string s_twoLines = W(37) + "\n" + W(4);

    private static TextStyle Helvetica(double size = 10) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = size,
    };

    private static Document SmallPage() => new()
    {
        PageSize = new PdfRectangle(0, 0, 400, 300),
        Margins = new EdgeInsets(10),
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

    /// <summary>
    /// Two fixed columns of 100pt and an automatic one. Rows 0 and 1 are the group, or the header
    /// and a row, and the cell of row 0 in the first column is tall in 100pt. Row 2's cell in the
    /// automatic column holds the given text, in a column that no cell of the group is in.
    /// </summary>
    private static TableElement Table(bool header, string third, string[]? extraRow = null)
    {
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(100, 100);
        var first = t.AddRow(header);
        first.AddCell(header ? new Cell(Lorem) : new Cell(Lorem) { RowSpan = 2 });
        first.AddCell("a");
        t.AddRow().AddCell("b");
        t.AddRow().AddCell("c").AddCell("d").AddCell(third);
        if (extraRow is not null)
        {
            var row = t.AddRow();
            foreach (var cell in extraRow) row.AddCell(cell);
        }
        return t;
    }

    /// <summary>
    /// The group of rows 0 and 1 is 424pt tall when the line feed is a break, and a page leaves
    /// 280pt. The cell that does that is in row 2, and the group's own cells hold no control white
    /// space, so only the last step reaches it. Measured as one line the automatic column is 253pt
    /// wide, the fixed ones are scaled to 63.4pt each and the group is 100pt tall.
    /// </summary>
    [Fact]
    public void AGroupThatACellOutsideItNarrowedIsRelievedByThatCell()
    {
        using var doc = SmallPage();
        doc.Add(Table(header: false, s_twoLines));

        var bytes = Save(doc);

        Assert.Equal(["row 2, cell 2"], Dropped(doc));
        Assert.Equal(1, PageCount(bytes));
    }

    /// <summary>The same with a repeated header row in place of the group.</summary>
    [Fact]
    public void AHeaderThatACellOutsideItNarrowedIsRelievedByThatCell()
    {
        using var doc = SmallPage();
        doc.Add(Table(header: true, s_twoLines));

        var bytes = Save(doc);

        Assert.Equal(["row 2, cell 2"], Dropped(doc));
        Assert.Equal(1, PageCount(bytes));
    }

    /// <summary>
    /// The last step takes the cells that the widths follow from and no others: a break in a fixed
    /// column of another row is kept, and so is a break in an automatic column's cell that holds
    /// none.
    /// </summary>
    [Fact]
    public void TheLastStepLeavesTheOtherCellsTheirBreaks()
    {
        using var doc = SmallPage();
        doc.Add(Table(header: false, s_twoLines, ["p\nq", "r\ns", "t"]));

        Save(doc);

        Assert.Equal(["row 2, cell 2"], Dropped(doc));
    }

    /// <summary>
    /// A table that the one-line measurement refuses too is still refused: with a space where the
    /// line feed was, the cell is one 387pt line in either measurement.
    /// </summary>
    [Fact]
    public void ATableThatTheOneLineMeasurementRefusesIsStillRefused()
    {
        using var doc = SmallPage();
        doc.Add(Table(header: false, W(37) + " " + W(4)));

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("RowSpan", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The seeds of the long random tables whose group or header the first three steps cannot
    /// relieve: each saves when every cell is measured as one line, and none is refused.
    /// </summary>
    [Theory]
    [InlineData(366)]
    [InlineData(1482)]
    [InlineData(2461)]
    public void ALongRandomTableThatTheThreeStepsCouldNotRelieveIsSaved(int seed)
    {
        RequireDejaVu();
        using var doc = TableRandomDocuments.Long(seed);

        var bytes = Save(doc);

        Assert.True(PageCount(bytes) > 1);
    }

    // ── Property: a table that fits measured as one line is saved ───────────────

    /// <summary>
    /// A table in the shape of the fixtures above with random line widths, a random column layout
    /// and a random place for the tall cell, and a tab, a line feed or a carriage return and line
    /// feed between the lines of the cell in the automatic column.
    /// </summary>
    private static Document Shaped(int seed)
    {
        var rng = new Random(seed);
        var doc = SmallPage();
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

    private static bool Saves(Func<Document> build, bool measuredAsOneLine)
    {
        using var doc = build();
        TableMeasureProbe.Current = new TableMeasureProbe { ReadEveryCellUnbroken = measuredAsOneLine };
        try
        {
            Save(doc);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            TableMeasureProbe.Current = null;
        }
    }

    /// <summary>
    /// A document that saves with every cell measured as one line, as it was measured before breaks
    /// were honoured, saves. The shaped tables come out of the first three steps refused often
    /// enough that the property fails without the last one; the long random tables are the wider
    /// net, over fixed, automatic, zero, negative and NaN widths, spans and header rows.
    /// </summary>
    [Fact]
    public void ADocumentThatSavesMeasuredAsOneLineSaves()
    {
        RequireDejaVu();
        var checkedShaped = 0;
        for (var seed = 0; seed < 600; seed++)
        {
            var s = seed;
            if (!Saves(() => Shaped(s), measuredAsOneLine: true)) continue;
            checkedShaped++;
            Assert.True(Saves(() => Shaped(s), measuredAsOneLine: false), $"shaped table {seed} is refused");
        }

        var checkedLong = 0;
        for (var seed = 0; seed < 250; seed++)
        {
            var s = seed;
            if (!Saves(() => TableRandomDocuments.Long(s), measuredAsOneLine: true)) continue;
            checkedLong++;
            Assert.True(Saves(() => TableRandomDocuments.Long(s), measuredAsOneLine: false), $"long table {seed} is refused");
        }

        // The property is not vacuous: most of each family saves measured as one line.
        Assert.True(checkedShaped > 300, $"only {checkedShaped} shaped tables save measured as one line");
        Assert.True(checkedLong > 40, $"only {checkedLong} long tables save measured as one line");
    }
}
