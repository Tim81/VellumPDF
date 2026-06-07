// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
    private readonly int          _startRow;  // first data row index (0-based)

    private double[] _colWidths  = [];
    private double[] _rowHeights = [];
    private LayoutBox _occupied;

    public TableRenderer(TableElement table, int startRow = 0)
    {
        _table    = table;
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
            var row  = rows[r];
            var maxH = 0.0;
            var col  = 0;
            foreach (var cell in row.Cells)
            {
                if (col >= _colWidths.Length) break;
                var cs      = cell.Style ?? style;
                var colW    = ColSpanWidth(col, cell.ColSpan);
                var innerW  = colW - cell.Padding.Horizontal;
                var lines   = WordWrapCount(cell.Content, cs, Math.Max(1, innerW));
                var cellH   = lines * cs.EffectiveLeading + cell.Padding.Vertical;
                maxH = Math.Max(maxH, cellH);
                col += cell.ColSpan;
            }
            _rowHeights[r] = maxH;
        }

        // Find header rows (always repeated; never counted toward start)
        var headerRows    = rows.Where(r => r.IsHeader).Count();
        var headerHeight  = Enumerable.Range(0, headerRows).Sum(i => _rowHeights[i]);
        var dataStartRow  = Math.Max(_startRow, headerRows);

        // Fit as many data rows as possible
        var y         = area.Y;
        var lastFit   = dataStartRow - 1;
        var runHeight = headerHeight;

        for (var r = dataStartRow; r < rows.Count; r++)
        {
            if (y + runHeight + _rowHeights[r] > area.Bottom + 0.001)
                break;
            runHeight += _rowHeights[r];
            lastFit    = r;
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
        var split    = new TableRenderer(_table, _startRow)   { _colWidths = _colWidths, _rowHeights = _rowHeights, _occupied = _occupied };
        var overflow = new TableRenderer(_table, lastFit + 1) { _colWidths = _colWidths, _rowHeights = _rowHeights };
        return LayoutResult.Partial(_occupied, split, overflow);
    }

    public void Draw(DrawContext ctx)
    {
        var area  = _occupied.Deflate(_table.Margins.Left, _table.Margins.Top, _table.Margins.Right, 0);
        var style = _table.DefaultCellStyle ?? TextStyle.Default;
        var font  = DocumentFontRegistry.GetOrCreate(style.Font);
        var rows  = _table.Rows;

        // Determine which rows to draw (headers + startRow..endRow)
        var headerRows   = rows.Where(r => r.IsHeader).Count();
        var dataStartRow = Math.Max(_startRow, headerRows);

        var rowY = area.Y;

        // Draw header rows first
        for (var r = 0; r < headerRows; r++)
        {
            DrawRow(ctx, rows[r], rowY, area.X, style, font);
            rowY += _rowHeights[r];
        }

        // Then data rows
        for (var r = dataStartRow; r < rows.Count; r++)
        {
            if (rowY >= _occupied.Bottom - 0.001) break;
            DrawRow(ctx, rows[r], rowY, area.X, style, font);
            rowY += _rowHeights[r];
        }
    }

    private void DrawRow(DrawContext ctx, Row row, double rowY, double startX,
        TextStyle style, VellumPdf.Fonts.PdfFontResource font)
    {
        var x = startX;
        var col = 0;
        foreach (var cell in row.Cells)
        {
            if (col >= _colWidths.Length) break;
            var colW = ColSpanWidth(col, cell.ColSpan);
            var h    = _rowHeights[RowIndex(row)];
            var cs   = cell.Style ?? style;
            var cf   = DocumentFontRegistry.GetOrCreate(cs.Font);

            // Background fill
            var bg = cell.Background ?? row.Background;
            if (bg.HasValue)
            {
                var (bx, by, bw, bh) = ctx.ToPdfRect(new LayoutBox(x, rowY, colW, h));
                ctx.Canvas
                    .SetFillColorRgb(bg.Value.R, bg.Value.G, bg.Value.B)
                    .Rectangle(bx, by, bw, bh)
                    .Fill();
            }

            // Cell border
            var (rx, ry, rw, rh) = ctx.ToPdfRect(new LayoutBox(x, rowY, colW, h));
            ctx.Canvas
                .SetStrokeColorRgb(_table.BorderColor.R, _table.BorderColor.G, _table.BorderColor.B)
                .SetLineWidth(_table.BorderWidth)
                .Rectangle(rx, ry, rw, rh)
                .Stroke();

            // Cell text
            var innerBox = new LayoutBox(
                x + cell.Padding.Left, rowY + cell.Padding.Top,
                colW - cell.Padding.Horizontal, h - cell.Padding.Vertical);

            var textW = Standard14Metrics.MeasureString(cs.Font, cell.Content, cs.FontSize);
            double txOffset = cell.Alignment switch
            {
                HorizontalAlignment.Center => (innerBox.Width - textW) / 2,
                HorizontalAlignment.Right  => innerBox.Width - textW,
                _ => 0
            };

            var pdfTextY = ctx.ToPdfY(innerBox.Y + cs.FontSize);
            ctx.Canvas
                .BeginText()
                .SetFont(cf, cs.FontSize)
                .SetFillColorRgb(cs.Color.R, cs.Color.G, cs.Color.B)
                .SetTextMatrix(1, 0, 0, 1, innerBox.X + txOffset, pdfTextY)
                .ShowText(cell.Content)
                .EndText();

            x   += colW;
            col += cell.ColSpan;
        }
    }

    private int RowIndex(Row row)
    {
        var rows = _table.Rows;
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i], row)) return i;
        return 0;
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
        var words    = text.Split(' ');
        var lines    = 1;
        var lineW    = 0.0;
        var spaceW   = Standard14Metrics.MeasureString(style.Font, " ", style.FontSize);

        foreach (var word in words)
        {
            var ww = Standard14Metrics.MeasureString(style.Font, word, style.FontSize);
            if (lineW == 0) { lineW = ww; }
            else if (lineW + spaceW + ww <= maxWidth) { lineW += spaceW + ww; }
            else { lines++; lineW = ww; }
        }
        return lines;
    }
}
