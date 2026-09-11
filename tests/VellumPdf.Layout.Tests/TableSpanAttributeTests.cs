// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// What a spanning cell's structure element claims, against what the renderer actually drew.
/// <c>/RowSpan</c> and <c>/ColSpan</c> come from ISO 32000-1:2008 Table 349, which ISO
/// 14289-1:2014 clause 7.5 requires a table header to be tagged according to, and an absent value
/// there defaults to 1.
///
/// Every case reads the saved document rather than the renderer's own object graph, because the
/// defect these cover was a number correct in the graph and wrong on the page: the renderer clamps
/// what it paints to the rows and columns that exist, and the attribute used to be written from the
/// cell's declared value instead. A cell could therefore claim rows the table does not have, or
/// claim to cover a row that a continuation page draws in full.
///
/// The veraPDF verdicts on these same shapes live in <c>PdfValidatorOracleTests</c>. These assert
/// the emitted number itself, so a change that keeps a document compliant by emitting nothing at
/// all still fails here.
/// </summary>
public sealed class TableSpanAttributeTests
{
    private static TextStyle Style(double size = 10) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = size,
    };

    /// <summary>
    /// The saved document as Latin-1 text with every run of whitespace collapsed to one space.
    /// Structure elements are ordinary uncompressed objects in the file body, not content streams
    /// and not object streams, so this reads the bytes directly rather than going through
    /// <c>PdfTestUtil.DecompressAllFlatStreams</c>, which would see none of them. The collapse is
    /// what lets a dictionary be matched as one string: the writer puts each key on its own line.
    /// </summary>
    private static string SaveAndFlatten(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        var text = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// A two-row table whose first cell declares more rows than the table holds. The renderer's own
    /// combined-height loop has always stopped at the last row that exists, so it paints two rows
    /// tall whatever the declared number is; the attribute used to be written from the declared
    /// number. Measured with veraPDF 1.30.2 before the clamp: 2 passed, 5 and 50 failed the
    /// column-count check on the row below, and <c>int.MaxValue</c> produced no verdict at all,
    /// because veraPDF tried to allocate a row array of that size and aborted the job with
    /// "Requested array size exceeds VM limit" and exit code 3.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(int.MaxValue)]
    public void RowSpan_declaredPastTheLastRow_emitsTheSpanTheRendererApplied(int declared)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
            Tagged = true,
            Language = "en-US",
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        var header = t.AddRow(isHeader: true);
        header.AddCell(new Cell("H0") { RowSpan = declared });
        header.AddCell("H1");
        var data = t.AddRow();
        data.AddCell("a1");
        data.AddCell("b1");
        doc.Add(t);

        var pdf = SaveAndFlatten(doc);

        Assert.Equal(1, Occurrences(pdf, "/RowSpan 2"));
        Assert.Equal(1, Occurrences(pdf, "/RowSpan"));
    }

    /// <summary>
    /// <c>/ColSpan</c> needs no equivalent clamp, and this pins the reason rather than the clamp.
    /// <c>TableGridResolver</c> sets the column count to the widest row's own span sum (#485), so
    /// declaring a span wider than the widths the caller supplied widens the grid instead of
    /// overrunning it: two supplied widths and a header cell declaring three columns resolve to a
    /// three-column grid, and the attribute is the declared 3 because the renderer covered 3.
    ///
    /// This case started life asserting a clamped 2, on the assumption that the grid stayed at the
    /// two supplied widths. It does not, and the clamp the assumption justified was removed.
    /// </summary>
    [Fact]
    public void ColSpan_widerThanTheSuppliedWidths_widensTheGridAndIsEmittedAsDeclared()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
            Tagged = true,
            Language = "en-US",
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(150, 150);
        t.AddRow(isHeader: true).AddCell(new Cell("H") { ColSpan = 3 });
        var data = t.AddRow();
        data.AddCell("a1");
        data.AddCell("b1");
        data.AddCell("c1");
        doc.Add(t);

        var pdf = SaveAndFlatten(doc);

        Assert.Equal(1, Occurrences(pdf, "/ColSpan 3"));
        Assert.Equal(1, Occurrences(pdf, "/ColSpan"));
    }

    /// <summary>
    /// A header row carrying <c>RowSpan = 2</c> on a table that paginates, so <c>Draw</c> repeats
    /// the header run at the top of every continuation page. The span is real on the first page,
    /// where the row below it is the row the span was declared over, and covers nothing on the
    /// others, where the row below it is the split row: the header must claim two rows once and one
    /// row on every repeat.
    ///
    /// This is the case that took the attribute commit out of #486. Measured with veraPDF 1.30.2 on
    /// this same shape: compliant before the attribute existed, `failedChecks="2"` with the
    /// attribute written from the declared value, compliant again once it is written from the span
    /// the page applied.
    /// </summary>
    [Fact]
    public void RowSpan_onARepeatedHeader_claimsTheCoveredRowOnlyOnThePageThatHasIt()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 170),
            Margins = new EdgeInsets(20),
            Tagged = true,
            Language = "en-US",
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        var header = t.AddRow(isHeader: true);
        header.AddCell(new Cell("H0") { RowSpan = 2 });
        header.AddCell("H1");
        for (var r = 0; r < 12; r++)
        {
            var row = t.AddRow();
            row.AddCell("a" + r.ToString(System.Globalization.CultureInfo.InvariantCulture));
            row.AddCell("b" + r.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        doc.Add(t);

        var pdf = SaveAndFlatten(doc);

        // Two header cells per page across three pages; exactly one of the six claims two rows.
        Assert.Equal(6, Occurrences(pdf, "/Scope /Column"));
        Assert.Equal(1, Occurrences(pdf, "/RowSpan 2"));
        Assert.Equal(1, Occurrences(pdf, "/RowSpan"));
    }

    /// <summary>
    /// A header cell's <c>/Scope</c> and its span go into one attribute dictionary, not two, since
    /// both are owner <c>/Table</c> attributes under ISO 32000-1:2008 Table 349.
    /// </summary>
    [Fact]
    public void RowSpan_onAHeaderCell_sharesTheAttributeDictionaryWithScope()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
            Tagged = true,
            Language = "en-US",
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        var header = t.AddRow(isHeader: true);
        header.AddCell(new Cell("H0") { RowSpan = 2 });
        header.AddCell("H1");
        var data = t.AddRow();
        data.AddCell("a1");
        data.AddCell("b1");
        doc.Add(t);

        var pdf = SaveAndFlatten(doc);

        Assert.Equal(1, Occurrences(pdf, "/O /Table /Scope /Column /RowSpan 2"));
    }

    /// <summary>
    /// A row every column of which is covered by a span from an earlier row contributes no cell of
    /// its own, so it gets no structure element: a <c>TR</c> with no <c>/K</c> and no <c>/Pg</c>
    /// describes nothing.
    ///
    /// The fixture matters more than the assertion. The first version of this test used a covered
    /// row that declared no cell at all, which never reaches the loop the fix changed and passes
    /// with the fix reverted. Here row 1's single declared cell is consumed by neither column,
    /// because row 0's cell covers both: <c>col</c> advances past the last column while
    /// <c>cellIdx</c> stays at 0. That declared-but-undrawn cell is #487, present on the base too,
    /// and is not what this test is about. Proven by mutation in a detached worktree: reverting the
    /// fix makes the second assertion below fail with 2.
    /// </summary>
    [Fact]
    public void CoveredRow_thatDeclaresACellItCannotDraw_emitsNoRowElement()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
            Tagged = true,
            Language = "en-US",
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(150, 150);
        t.AddRow().AddCell(new Cell("Span") { RowSpan = 2, ColSpan = 2 });
        t.AddRow().AddCell("covered");
        doc.Add(t);

        var pdf = SaveAndFlatten(doc);

        Assert.Equal(1, Occurrences(pdf, "/RowSpan 2"));
        Assert.Equal(1, Occurrences(pdf, "/S /TR"));
    }
}
