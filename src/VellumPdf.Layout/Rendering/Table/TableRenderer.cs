// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Rendering.Table;

/// <summary>
/// Two-phase renderer for <see cref="TableElement"/>.
///
/// Layout:
///   • Resolves column widths.
///   • Computes row heights (max cell content height in each row).
///   • Splits at row boundaries if the table crosses a page break.
///   • Header rows are repeated at the top of each continuation page.
///
/// Draw:
///   • Fills cell backgrounds.
///   • Draws cell text.
///   • Draws collapsed borders (single shared line between cells).
/// </summary>
public sealed class TableRenderer : IRenderer
{
    private readonly TableElement _table;
    private readonly int _startRow;  // first data row index (0-based)

    private double[] _colWidths = [];
    private double[] _rowHeights = [];
    private LayoutBox _occupied;

    public TableRenderer(TableElement table, int startRow = 0)
    {
        _table = table;
        _startRow = startRow;
    }

    public LayoutResult Layout(LayoutContext context)
    {
        var area = context.Area.Deflate(_table.Margins);
        if (area.Width <= 0) return LayoutResult.Nothing();

        var grid = new TableGridResolver();
        grid.Resolve(_table, area.Width);
        _colWidths = grid.ColWidths;

        if (_colWidths.Length == 0) return LayoutResult.Nothing();

        // Compute row heights
        var rows = _table.Rows;
        var style = _table.DefaultCellStyle ?? TextStyle.Default;
        _rowHeights = new double[rows.Count];

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var maxH = 0.0;
            var col = 0;
            foreach (var cell in row.Cells)
            {
                if (col >= _colWidths.Length) break;
                var cs = cell.Style ?? style;
                var colW = ColSpanWidth(col, cell.ColSpan);
                var innerW = colW - cell.Padding.Horizontal;
                var lines = WordWrapCount(cell.Content, cs, Math.Max(1, innerW));
                var cellH = lines * cs.EffectiveLeading + cell.Padding.Vertical;
                maxH = Math.Max(maxH, cellH);
                col += cell.ColSpan;
            }
            _rowHeights[r] = maxH;
        }

        // Find header row indices (may not be contiguous at top; use actual IsHeader rows)
        var headerRowIndices = FindHeaderRowIndices(rows);
        var headerHeight = headerRowIndices.Sum(i => _rowHeights[i]);
        var lastHeaderRow = headerRowIndices.Count > 0 ? headerRowIndices[^1] + 1 : 0;
        var dataStartRow = Math.Max(_startRow, lastHeaderRow);

        // Fit as many data rows as possible
        var y = area.Y;
        var lastFit = dataStartRow - 1;
        var runHeight = headerHeight;

        for (var r = dataStartRow; r < rows.Count; r++)
        {
            if (y + runHeight + _rowHeights[r] > area.Bottom + 0.001)
                break;
            runHeight += _rowHeights[r];
            lastFit = r;
        }

        if (lastFit < dataStartRow)
        {
            // Nothing fits beyond headers
            if (dataStartRow >= rows.Count) return LayoutResult.Nothing();
            return LayoutResult.Nothing();
        }

        _occupied = area.WithHeight(runHeight);

        if (lastFit == rows.Count - 1)
            return LayoutResult.Full(_occupied);

        // Partial
        var split = new TableRenderer(_table, _startRow) { _colWidths = _colWidths, _rowHeights = _rowHeights, _occupied = _occupied };
        var overflow = new TableRenderer(_table, lastFit + 1) { _colWidths = _colWidths, _rowHeights = _rowHeights };
        return LayoutResult.Partial(_occupied, split, overflow);
    }

    public void Draw(DrawContext ctx)
    {
        var area = _occupied.Deflate(_table.Margins.Left, _table.Margins.Top, _table.Margins.Right, 0);
        var style = _table.DefaultCellStyle ?? TextStyle.Default;
        var rows = _table.Rows;

        // Determine which rows to draw (headers + data rows starting at _startRow)
        var headerRowIndices = FindHeaderRowIndices(rows);
        var dataStartRow = Math.Max(_startRow, headerRowIndices.Count > 0 ? headerRowIndices[^1] + 1 : 0);

        // RowSpan occupancy: tracks which (row, col) slots are covered by a span from a prior row.
        // Key: (rowIndex, colIndex), Value: the cell + the Y position where the span started.
        var spanMap = new Dictionary<(int row, int col), (Cell cell, Row originRow, double startY, int remainingRows)>();

        // Tagged PDF: build the Table → TR → TH/TD → P hierarchy.
        PdfStructElem? tableElem = null;
        if (ctx.Tagged)
            tableElem = new PdfStructElem("Table");

        var rowY = area.Y;

        // Draw actual header rows (wherever they appear)
        foreach (var hi in headerRowIndices)
        {
            var trElem = tableElem is not null ? new PdfStructElem("TR") : null;
            if (trElem is not null) tableElem!.Children.Add(trElem);
            DrawRow(ctx, rows[hi], hi, rowY, area.X, style, spanMap, trElem);
            rowY += _rowHeights[hi];
        }

        // Then data rows
        for (var r = dataStartRow; r < rows.Count; r++)
        {
            if (rowY >= _occupied.Bottom - 0.001) break;
            var trElem = tableElem is not null ? new PdfStructElem("TR") : null;
            if (trElem is not null) tableElem!.Children.Add(trElem);
            DrawRow(ctx, rows[r], r, rowY, area.X, style, spanMap, trElem);
            rowY += _rowHeights[r];
        }

        // Register the complete Table struct elem tree with the document.
        if (tableElem is not null)
            ctx.RegisterStructElemTree(tableElem);
    }

    private void DrawRow(DrawContext ctx, Row row, int rowIdx, double rowY, double startX,
        TextStyle style,
        Dictionary<(int row, int col), (Cell cell, Row originRow, double startY, int remainingRows)> spanMap,
        PdfStructElem? trElem)
    {
        var x = startX;
        var col = 0;

        // Advance x past any columns that are occupied by row-spanning cells from prior rows.
        // We iterate columns the same way as the source row's cells, but skip occupied slots.
        var cellIdx = 0;
        var cells = row.Cells;

        // Build a column-x map for this row to resolve span-origin x positions.
        var colXPositions = new double[_colWidths.Length + 1];
        colXPositions[0] = startX;
        for (var i = 0; i < _colWidths.Length; i++)
            colXPositions[i + 1] = colXPositions[i] + _colWidths[i];

        while (col < _colWidths.Length)
        {
            // Check if this column is occupied by a span from a prior row
            if (spanMap.TryGetValue((rowIdx, col), out var span))
            {
                // It's the last row of a multi-row span — draw the cell spanning full combined height
                if (span.remainingRows == 1)
                {
                    var spanCell = span.cell;
                    var spanColW = ColSpanWidth(col, spanCell.ColSpan);
                    var spanTotalH = rowY + _rowHeights[rowIdx] - span.startY;
                    DrawCell(ctx, spanCell, span.originRow, col, colXPositions[col], span.startY,
                             spanColW, spanTotalH, style, trElem);
                    // Remove all slots this cell occupied in this row
                    for (var sc = col; sc < col + spanCell.ColSpan; sc++)
                        spanMap.Remove((rowIdx, sc));
                }
                else
                {
                    // Still spanning — forward remaining rows
                    for (var sc = col; sc < col + span.cell.ColSpan && sc < _colWidths.Length; sc++)
                    {
                        spanMap[(rowIdx + 1, sc)] = span with { remainingRows = span.remainingRows - 1 };
                        spanMap.Remove((rowIdx, sc));
                    }
                }
                col += span.cell.ColSpan;
                continue;
            }

            // No span — process the next cell from this row
            if (cellIdx >= cells.Count) break;
            var cell = cells[cellIdx++];
            if (col >= _colWidths.Length) break;

            var colW = ColSpanWidth(col, cell.ColSpan);
            var h = _rowHeights[rowIdx];

            if (cell.RowSpan <= 1)
            {
                // Normal single-row cell — draw immediately
                DrawCell(ctx, cell, row, col, colXPositions[col], rowY, colW, h, style, trElem);
            }
            else
            {
                // Multi-row span: don't draw yet — register in spanMap for the origin row,
                // draw when the last spanned row is reached (or draw here spanning combined height).
                // Strategy: draw immediately spanning the combined height of all spanned rows.
                var totalSpanH = 0.0;
                for (var sr = rowIdx; sr < rowIdx + cell.RowSpan && sr < _rowHeights.Length; sr++)
                    totalSpanH += _rowHeights[sr];

                DrawCell(ctx, cell, row, col, colXPositions[col], rowY, colW, totalSpanH, style, trElem);

                // Mark subsequent rows as occupied so they skip this column range
                for (var sr = rowIdx + 1; sr < rowIdx + cell.RowSpan && sr < _rowHeights.Length; sr++)
                    for (var sc = col; sc < col + cell.ColSpan && sc < _colWidths.Length; sc++)
                        spanMap[(sr, sc)] = (cell, row, rowY, cell.RowSpan - (sr - rowIdx));
            }

            col += cell.ColSpan;
        }
    }

    private void DrawCell(DrawContext ctx, Cell cell, Row row, int colIdx,
        double cellX, double cellY, double colW, double h,
        TextStyle style, PdfStructElem? trElem)
    {
        var cs = cell.Style ?? style;

        // Background fill
        var bg = cell.Background ?? row.Background;
        if (bg.HasValue)
        {
            var (bx, by, bw, bh) = ctx.ToPdfRect(new LayoutBox(cellX, cellY, colW, h));
            ctx.Canvas
                .SetFillColorRgb(bg.Value.R, bg.Value.G, bg.Value.B)
                .Rectangle(bx, by, bw, bh)
                .Fill();
        }

        // Cell border
        var (rx, ry, rw, rh) = ctx.ToPdfRect(new LayoutBox(cellX, cellY, colW, h));
        ctx.Canvas
            .SetStrokeColorRgb(_table.BorderColor.R, _table.BorderColor.G, _table.BorderColor.B)
            .SetLineWidth(_table.BorderWidth)
            .Rectangle(rx, ry, rw, rh)
            .Stroke();

        // Cell text
        var innerBox = new LayoutBox(
            cellX + cell.Padding.Left, cellY + cell.Padding.Top,
            colW - cell.Padding.Horizontal, h - cell.Padding.Vertical);

        var textW = cs.FontRef.MeasureString(cell.Content, cs.FontSize);
        double txOffset = cell.Alignment switch
        {
            HorizontalAlignment.Center => (innerBox.Width - textW) / 2,
            HorizontalAlignment.Right => innerBox.Width - textW,
            _ => 0
        };

        var pdfTextY = ctx.ToPdfY(innerBox.Y + cs.FontSize);

        // Tagged PDF: TH (header) or TD (data), containing a P with the MCID.
        // Structure: TR → TH/TD → P
        int mcid = -1;
        if (ctx.Tagged)
            mcid = ctx.Canvas.BeginMarkedContent("P");

        var canvas = ctx.Canvas.BeginText();

        if (cs.FontRef.IsEmbedded)
        {
            var handle = cs.FontRef.Embedded;
            var resourceName = ctx.UseEmbeddedFont(handle);
            canvas.SetFontByName(resourceName, cs.FontSize);
        }
        else
        {
            var cf = ctx.GetFont(cs.Font);
            canvas.SetFont(cf, cs.FontSize);
        }

        canvas
            .SetFillColorRgb(cs.Color.R, cs.Color.G, cs.Color.B)
            .SetTextMatrix(1, 0, 0, 1, innerBox.X + txOffset, pdfTextY);

        if (cs.FontRef.IsEmbedded)
            ShowEmbeddedText(canvas, cs.FontRef.Embedded, cell.Content);
        else
            canvas.ShowText(cell.Content);

        canvas.EndText();

        if (ctx.Tagged && mcid >= 0)
        {
            ctx.Canvas.EndMarkedContent();

            // Build: TR → TH/TD → P
            var cellType = row.IsHeader ? "TH" : "TD";
            var cellElem = new PdfStructElem(cellType);
            var pElem = new PdfStructElem("P") { Mcid = mcid };
            ctx.StampStructElemPage(pElem);
            cellElem.Children.Add(pElem);
            trElem?.Children.Add(cellElem);
        }
    }

    /// <summary>Maps every code point to a glyph id and emits a single hex glyph run.</summary>
    private static void ShowEmbeddedText(PdfCanvas canvas, EmbeddedFontHandle handle, string text)
    {
        // Allocate conservatively: one glyph per char (surrogate pairs → 1 glyph each).
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
    }

    /// <summary>Returns the indices of all header rows in the table, in order.</summary>
    private static List<int> FindHeaderRowIndices(IReadOnlyList<Row> rows)
    {
        var indices = new List<int>();
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].IsHeader) indices.Add(i);
        return indices;
    }

    private double ColSpanWidth(int startCol, int span)
    {
        var w = 0.0;
        for (var i = 0; i < span && startCol + i < _colWidths.Length; i++)
            w += _colWidths[startCol + i];
        return w;
    }

    private static int WordWrapCount(string text, TextStyle style, double maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var words = text.Split(' ');
        var lines = 1;
        var lineW = 0.0;
        var spaceW = style.FontRef.MeasureString(" ", style.FontSize);

        foreach (var word in words)
        {
            var ww = style.FontRef.MeasureString(word, style.FontSize);
            if (lineW == 0) { lineW = ww; }
            else if (lineW + spaceW + ww <= maxWidth) { lineW += spaceW + ww; }
            else { lines++; lineW = ww; }
        }
        return lines;
    }
}
