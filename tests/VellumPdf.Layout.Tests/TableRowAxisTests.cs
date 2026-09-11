// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The table pull request's row axis: a header row that sits after a data row, the table's own
/// margins applied to <c>Draw</c> a second time on top of the deflate <c>Layout</c> already did,
/// and a <c>RowSpan</c> cell drawn once at its origin row and a second time when the span map
/// runs down. All three are content loss or duplication rather than a cosmetic offset, which is
/// why every case here asserts an occurrence count instead of presence.
///
/// Sectioned like <see cref="TableColumnAxisTests"/>: (a) the header row window.
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
}
