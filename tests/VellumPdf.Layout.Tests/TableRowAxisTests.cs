// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The table pull request's row axis: a header row that sits after a data row, the table's own
/// margins applied to <c>Draw</c> a second time on top of the deflate <c>Layout</c> already did,
/// and a <c>RowSpan</c> cell drawn once at its origin row and a second time when the span map runs
/// down. The first and third are content loss and duplication. The second is an offset on both axes
/// while the margin stays below the row's own height, and becomes loss on the vertical axis once it
/// reaches that height, which is why section (b) pins both the corrected origin and the row's
/// survival at the boundary. Section (c) asserts an occurrence count rather than presence: the
/// existing rowspan test in <see cref="LayoutFixTests"/> passed whether the spanning cell drew once
/// or twice, which is exactly how that defect survived review undetected.
///
/// Sectioned like <see cref="TableColumnAxisTests"/>: (a) the header row window, (b) the doubly
/// applied table margin, (c) the duplicated rowspan draw.
/// </summary>
public sealed class TableRowAxisTests
{
    private static TextStyle Style(double size = 10) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = size,
    };

    private static string RenderAndDecompress(Document doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    // ── (a) The header row window ─────────────────────────────────────────────

    /// <summary>
    /// Page 400x300, margin 10; rows H0 (header), D1 (data), H2 (header), D3 (data).
    /// <c>FindHeaderRowIndices</c> used to collect every <c>IsHeader</c> row wherever it sat, so
    /// <c>dataStartRow</c> became <c>headerRowIndices[^1] + 1</c> — 3, past H2 — while the header
    /// loop only ever drew the rows in <c>headerRowIndices</c> itself (0 and 2). D1 sits between
    /// the two loops and neither one reaches it: before the fix this drew H0, H2 and D3 and never
    /// D1. Restricting the repeatable header to the leading contiguous run makes H2 an ordinary
    /// row, so the data loop now starts at row 1 and draws D1, H2 and D3 in turn.
    /// </summary>
    [Fact]
    public void HeaderRow_dataRowBeforeALaterHeader_drawnByExactlyOneLoop()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.AddRow(isHeader: true).AddCell("H0");
        t.AddRow().AddCell("D1");
        t.AddRow(isHeader: true).AddCell("H2");
        t.AddRow().AddCell("D3");
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        foreach (var text in new[] { "H0", "D1", "H2", "D3" })
            Assert.Equal(1, placements.Count(p => p.Text == text));
    }

    // ── (b) The doubly applied table margin ───────────────────────────────────

    /// <summary>
    /// Page 400x300, document margin 10, one row with one cell ("Wg", the default padding's left
    /// inset of 6pt applying to whichever edge <c>Layout</c> resolves). <c>Draw</c> used to deflate
    /// <c>_occupied</c> by <c>_table.Margins</c> a second time after <c>Layout</c> had already
    /// deflated <c>context.Area</c> by the same margins once, so the cell's left edge sat at the
    /// document margin plus twice the table margin. Measured before this fix at table margin 10:
    /// x = 10 (document) + 20 (table margin doubled) + 6 (padding) = 36. This pins the corrected
    /// single application across the five table margins the brief measured (5, 9, 10, 15, 19).
    /// </summary>
    [Theory]
    [InlineData(5.0)]
    [InlineData(9.0)]
    [InlineData(10.0)]
    [InlineData(15.0)]
    [InlineData(19.0)]
    public void TableMargin_cellTextOrigin_isDocumentMarginPlusTableMarginOnce(double tableMargin)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style(), Margins = new EdgeInsets(tableMargin) };
        t.AddRow().AddCell("Wg");
        doc.Add(t);

        var placement = Assert.Single(ContentStreamReadback.TextPlacements(RenderAndDecompress(doc)));

        const double documentMargin = 10.0;
        const double cellLeftPadding = 6.0; // Cell's default EdgeInsets(4, 6, 4, 6): Left = 6.
        Assert.Equal(documentMargin + tableMargin + cellLeftPadding, placement.X, 0.001);
    }

    /// <summary>
    /// Same page and cell as <see cref="TableMargin_cellTextOrigin_isDocumentMarginPlusTableMarginOnce"/>,
    /// table margin 20. A single-line "Wg" cell at font 10 measures a row height of exactly 20:
    /// <c>EffectiveLeading</c> (12, since <c>FontSize * 1.2</c> with no explicit leading) plus the
    /// cell's default vertical padding (4 + 4 = 8). The double deflate took the table margin out of
    /// the vertical axis twice, so once the margin reached the row's own height the second top
    /// deflate pushed <c>rowY</c> at or past <c>_occupied.Bottom</c> before the first row drew,
    /// leaving no rectangle and no text — measured before this fix as zero <c>Tj</c> operators for
    /// this fixture in the byte-identity corpus (`table margin 20 at row height`). After the fix
    /// the row still fits, since the margin is only removed once.
    /// </summary>
    [Fact]
    public void TableMargin_equalToTheRowHeight_stillDrawsTheRow()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style(), Margins = new EdgeInsets(20) };
        t.AddRow().AddCell("Wg");
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        Assert.Equal(1, placements.Count(p => p.Text == "Wg"));
    }

    // ── (c) The duplicated rowspan draw ───────────────────────────────────────

    /// <summary>
    /// Page 400x300, margin 10; two rows, the first cell of row 0 carrying <c>RowSpan = 2</c>.
    /// <c>DrawRow</c> drew the spanning cell immediately at its origin row across the full combined
    /// height of both spanned rows, then drew it again when the span map reached its last spanned
    /// row (<c>remainingRows == 1</c>) — the same cell, recomputed to the same combined height, a
    /// second time. The existing rowspan test in <see cref="LayoutFixTests"/> asserted only that
    /// "SPAN" appeared in the stream, which passes whether it appears once or twice; this asserts
    /// the count.
    /// </summary>
    [Fact]
    public void RowSpan_spanningCellText_drawnExactlyOnce()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        var r0 = t.AddRow();
        r0.AddCell(new Cell("SPAN") { RowSpan = 2 });
        r0.AddCell("x0");
        t.AddRow().AddCell("x1");
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        Assert.Equal(1, placements.Count(p => p.Text == "SPAN"));
        Assert.Equal(1, placements.Count(p => p.Text == "x0"));
        Assert.Equal(1, placements.Count(p => p.Text == "x1"));
    }

    /// <summary>
    /// Page 300x150, margin 20, 12 rows each holding two cells, a <c>RowSpan = 3</c> at row 5 —
    /// crossing a page break, so the split path in <c>Layout</c> is exercised too (it never places
    /// the break inside a rowspan group, per <see cref="TableElement"/>'s own documented guarantee,
    /// so the span itself lands whole on one page). Of the 24 cells this loop builds, two are never
    /// reachable through the draw loop regardless of this fix: rows 6 and 7 (the two rows the span
    /// covers besides its origin) each still get a second cell appended for the column the span
    /// occupies, and <c>DrawRow</c>'s <c>cellIdx</c> then reads it as if it belonged to the next
    /// column instead, leaving that row's true second cell ("x6", "x7") unconsumed — a property of
    /// how this fixture builds continuation rows, not a defect this pull request's scope covers.
    /// 24 cells minus those 2 leaves 22 reachable ones. Before this fix, the duplicated span draw
    /// added a 23rd literal on top of those 22; after it, the count is exactly 22.
    /// </summary>
    [Fact]
    public void RowSpan_acrossAPageBreak_totalLiteralsMatchTheReachableCells()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 150),
            Margins = new EdgeInsets(20),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        for (var r = 0; r < 12; r++)
        {
            var row = t.AddRow();
            if (r == 5)
                row.AddCell(new Cell("SPAN") { RowSpan = 3 });
            else
                row.AddCell("r" + r.ToString(CultureInfo.InvariantCulture));
            row.AddCell("x" + r.ToString(CultureInfo.InvariantCulture));
        }
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        Assert.Equal(1, placements.Count(p => p.Text == "SPAN"));
        Assert.Equal(22, placements.Count);
    }

    // ── (d) A row entirely covered by a span emits no struct elem ──────────────

    /// <summary>
    /// Page 400x300, margin 10, tagged; one column, row 0 holding a <c>RowSpan = 2</c> cell, row 1
    /// contributing no cell of its own — its only column is covered by the span from row 0.
    /// <c>Draw</c> used to create a <c>TR</c> struct elem for every row before <c>DrawRow</c> ran,
    /// so a row that draws no cell of its own left a struct elem with no <c>/K</c> and no
    /// <c>/Pg</c> in the tree — measured directly as <c>&lt;&lt; /Type /StructElem /S /TR
    /// /P 8 0 R &gt;&gt;</c> and nothing else. A <c>TR</c> is now added to the tree only once
    /// <c>DrawRow</c> has actually populated it, so this table's tree holds exactly one.
    /// </summary>
    [Fact]
    public void RowSpan_rowFullyCoveredBySpan_emitsNoEmptyTR()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
            Tagged = true,
            Language = "en-US",
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.AddRow().AddCell(new Cell("Span") { RowSpan = 2 });
        t.AddRow(); // No cell of its own: its one column is covered by the span above.
        doc.Add(t);

        var ms = new MemoryStream();
        doc.Save(ms);
        var text = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        var trCount = System.Text.RegularExpressions.Regex.Matches(text, @"/S\s*/TR\b").Count;
        Assert.Equal(1, trCount);
    }
}
