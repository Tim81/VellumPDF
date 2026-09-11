// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The table pull request's row axis: a header row that sits after a data row, and the table's own
/// margins applied to <c>Draw</c> a second time on top of the deflate <c>Layout</c> already did.
/// The first is content loss. The second is an offset on both axes while the margin stays below the
/// row's own height, and becomes loss on the vertical axis once it reaches that height, which is why
/// section (b) pins both the corrected origin and the row's survival at the boundary.
///
/// Sectioned like <see cref="TableColumnAxisTests"/>: (a) the header row window, (b) the doubly
/// applied table margin.
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
}
