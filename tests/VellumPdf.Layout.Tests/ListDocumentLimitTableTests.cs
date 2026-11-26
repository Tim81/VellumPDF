// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A table cell reads its line ends, so a cell of <c>n</c> characters can hold <c>n + 1</c> lines,
/// and a row is cut into pieces that each take a line of the cells. The count of an element near
/// the limit (ListDocumentLimitRuleTests) is two, one for each row and the characters of the
/// cells, and it must stay above the continuations a table takes, whichever way its cells are read.
/// </summary>
public sealed class ListDocumentLimitTableTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static T Pick<T>(Random rng, params T[] values) => values[rng.Next(values.Length)];

    // One row per entry, its cells holding the text and the size given.
    private static TableElement Rows(int rows, params (string Text, double Size)[] cells)
    {
        var table = new TableElement();
        for (var i = 0; i < rows; i++)
        {
            var row = table.AddRow();
            foreach (var (text, size) in cells)
                row.AddCell(new Cell(text) { Style = H(size), Padding = EdgeInsets.Zero });
        }

        return table;
    }

    private static long BoundOf(TableElement table, double width, double height)
    {
        var probe = new DocumentRenderer(new PdfDocument(), new PdfRectangle(0, 0, width, height), new EdgeInsets(10));
        return probe.ContinuationBoundOf(new TableRenderer(table));
    }

    // Saves the table alone on a page, with the document's limit set to the bound, and returns the
    // pages it took, or -1 when the save was refused for a reason other than the limit.
    private static int PagesUnderItsOwnBound(TableElement table, double width, double height, string what)
    {
        var bound = BoundOf(table, width, height);
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var document = new DocumentRenderer(pdf, new PdfRectangle(0, 0, width, height), new EdgeInsets(10))
        {
            ContinuationLimit = (int)bound,
        };
        document.Add(new TableRenderer(table));
        try
        {
            using var ms = new MemoryStream();
            document.Render(ms);
            return pdf.Pages.Count;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("page continuations", StringComparison.Ordinal))
        {
            Assert.Fail($"{what}: limit {bound} reached, page {width}x{height}");
            return -1;
        }
        catch (InvalidOperationException)
        {
            // A cell too tall for a page: a refusal that is not the limit.
            return -1;
        }
    }

    /// <summary>
    /// A row that holds a tall cell with line feeds and a short empty cell, over every page height
    /// in a range, never reaches the limit set to its bound. The first piece of such a row can
    /// take only the short cell when little room is left, which is the shape that adds a piece.
    /// </summary>
    [Fact]
    public void ATallCellWithLineEndsBesideAnEmptyOne_staysWithinItsBound()
    {
        var taken = 0;
        foreach (var lines in new[] { 1, 2, 3 })
        {
            for (var height = 25.0; height <= 90; height += 1.5)
            {
                var table = Rows(40, (new string('\n', lines), 14), ("", 4));
                var pages = PagesUnderItsOwnBound(table, 100, height, $"{lines} line feeds, page height {height}");
                if (pages > 0) taken += pages;
            }
        }

        Assert.True(taken > 1000, $"only {taken} pages across the sample");
    }

    /// <summary>
    /// Random tables with cells that hold line feeds, carriage returns, tabs and other control
    /// white space, mixed sizes and paddings, and a repeated header row sometimes, are never
    /// refused for the limit when it is set to their bound.
    /// </summary>
    [Fact]
    public void ATableWhoseCellsReadLineEnds_staysWithinItsBound()
    {
        string[] contents =
        [
            "", "", "\n", "\n\n", "\n\n\n", "x", "x\nx", "\nx", "x\n", " ", "ab cd", "\r\n", "\r",
            "\r\n\r\n", "\t", "a\tb\nc", "WWWWWWWW", "\f\v", "\u0085", "\u2028",
        ];
        var rng = new Random(20261123);
        var taken = 0;
        for (var seed = 0; seed < 500; seed++)
        {
            var table = new TableElement();
            var columns = rng.Next(1, 4);
            if (rng.Next(5) == 0)
            {
                var header = table.AddRow(isHeader: true);
                for (var c = 0; c < columns; c++)
                    header.AddCell(new Cell(Pick(rng, "h", "", "h\nh")) { Style = H(Pick(rng, 4.0, 6.0)), Padding = EdgeInsets.Zero });
            }

            for (var r = rng.Next(1, 40); r > 0; r--)
            {
                var row = table.AddRow();
                for (var c = 0; c < columns; c++)
                {
                    row.AddCell(new Cell(Pick(rng, contents))
                    {
                        Style = H(Pick(rng, 4.0, 6.0, 10.0, 14.0, 20.0, 30.0)),
                        Padding = new EdgeInsets(rng.Next(0, 3) * rng.Next(0, 8), 0, rng.Next(0, 3) * rng.Next(0, 8), 0),
                    });
                }
            }

            var pages = PagesUnderItsOwnBound(table, Pick(rng, 60.0, 100.0, 200.0), 20 + rng.Next(5, 80), $"seed {seed}");
            if (pages > 0) taken += pages;
        }

        // The sample reaches many pages, so the property is not met by tables of one page.
        Assert.True(taken > 1500, $"only {taken} pages across the sample");
    }

    /// <summary>
    /// The bound is two, one for each row and one for each character of each cell, with a null
    /// cell text counting nothing.
    /// </summary>
    [Fact]
    public void TheBoundOfATable_isTwoPlusItsRowsAndCharacters()
    {
        var table = Rows(3, ("a\nb", 10), ("", 10));
        table.AddRow().AddCell(new Cell(null!));

        Assert.Equal(2 + 4 + 3 * 3, BoundOf(table, 200, 80));
    }
}
