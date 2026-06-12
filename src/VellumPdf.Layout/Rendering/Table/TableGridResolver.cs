// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Rendering.Table;

/// <summary>
/// Resolves column widths and builds the cell occupancy grid.
///
/// Width resolution order:
///   1. If explicit widths provided via SetColumnWidths → use those.
///   2. Otherwise: compute min-content (longest word) and max-content (full text)
///      widths for each column, then distribute available width proportionally.
///
/// Occupancy grid: a 2D bool array [row][col] marking cells occupied by a span origin.
/// </summary>
internal sealed class TableGridResolver
{
    public double[] ColWidths { get; private set; } = [];
    public int ColCount { get; private set; }

    public void Resolve(TableElement table, double availableWidth)
    {
        // Determine column count from the first non-header row (or header row)
        var firstRow = table.Rows.FirstOrDefault();
        ColCount = firstRow?.Cells.Sum(c => c.ColSpan) ?? 0;
        if (ColCount == 0) { ColWidths = []; return; }

        if (table.ColWidths.Count > 0)
        {
            ColWidths = table.ColWidths.ToArray();
        }
        else
        {
            ColWidths = AutoWidth(table, availableWidth, ColCount);
        }
    }

    private static double[] AutoWidth(TableElement table, double available, int cols)
    {
        var minW = new double[cols];
        var maxW = new double[cols];

        var style = table.DefaultCellStyle ?? TextStyle.Default;

        foreach (var row in table.Rows)
        {
            var col = 0;
            foreach (var cell in row.Cells)
            {
                if (col >= cols) break;
                var cellStyle = cell.Style ?? style;
                var words = cell.Content.Split(' ');
                var longest = words.Max(w => cellStyle.FontRef.MeasureString(w, cellStyle.FontSize));
                var full = cellStyle.FontRef.MeasureString(cell.Content, cellStyle.FontSize)
                              + cell.Padding.Horizontal;

                var share = cell.ColSpan;
                for (var s = 0; s < share && col + s < cols; s++)
                {
                    minW[col + s] = Math.Max(minW[col + s], longest / share + cell.Padding.Horizontal);
                    maxW[col + s] = Math.Max(maxW[col + s], full / share);
                }
                col += share;
            }
        }

        // Distribute available width proportionally to max-content widths
        var totalMax = maxW.Sum();
        var result = new double[cols];
        for (var i = 0; i < cols; i++)
        {
            result[i] = totalMax > 0
                ? Math.Max(minW[i], available * maxW[i] / totalMax)
                : available / cols;
        }
        return result;
    }
}
