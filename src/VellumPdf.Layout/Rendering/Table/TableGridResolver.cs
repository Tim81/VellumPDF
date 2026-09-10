// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Rendering.Table;

/// <summary>
/// Resolves column widths and builds the cell occupancy grid.
///
/// Width resolution order:
///   1. If explicit widths provided via SetColumnWidths → reconcile them against the resolved
///      column count: a missing or zero entry means auto (see <see cref="TableElement.ColWidths"/>),
///      a negative entry is clamped to the same auto meaning, and the auto columns share the width
///      left over after the explicit ones. If the explicit entries alone still overrun the
///      available width, they are scaled down to what the auto columns' own content floors leave
///      them.
///   2. Otherwise: compute min-content (longest word) and max-content (full text) widths for each
///      column, then distribute available width proportionally, scaled down to the available width
///      when the content floors alone overrun it (#468).
///
/// Occupancy grid: a 2D bool array [row][col] marking cells occupied by a span origin.
/// </summary>
internal sealed class TableGridResolver
{
    public double[] ColWidths { get; private set; } = [];
    public int ColCount { get; private set; }

    public void Resolve(TableElement table, double availableWidth)
    {
        // The column count is the widest row, not merely the first (#480 section 1). Nothing stops
        // a caller giving one row more cells than another: Row.AddCell checks no other row's arity,
        // and TableElement exposes no column count to check against. So a later row with an extra
        // cell was losing it entirely, because the grid never had a column for it. Measured before
        // this fix, a first row of two cells and a second of three drew four literals and not five.
        ColCount = 0;
        foreach (var row in table.Rows)
        {
            var rowCols = 0;
            foreach (var cell in row.Cells) rowCols += cell.ColSpan;
            if (rowCols > ColCount) ColCount = rowCols;
        }
        if (ColCount == 0) { ColWidths = []; return; }

        ColWidths = table.ColWidths.Count > 0
            ? ReconcileExplicitWidths(table, availableWidth, ColCount)
            : AutoWidth(table, availableWidth, ColCount);
    }

    /// <summary>
    /// Reconciles an explicit <see cref="TableElement.ColWidths"/> array against the resolved
    /// column count (#477). Three things follow from treating an explicit array as a literal copy,
    /// the way this method used to:
    /// <list type="bullet">
    ///   <item>
    ///   An array shorter than the column count silently dropped every column past its own length,
    ///   because the renderer stops at <c>_colWidths.Length</c> — the same symptom as a zero width,
    ///   just at the array boundary instead of inside it.
    ///   </item>
    ///   <item>
    ///   Zero already means auto in two places this type's own callers document
    ///   (<see cref="TableElement.ColWidths"/> and <see cref="TableElement.SetColumnWidths"/>) and
    ///   neither implemented, so a zero column rendered at zero width instead.
    ///   </item>
    ///   <item>
    ///   A negative width advanced <c>x</c> backwards for every following column. Refusing it would
    ///   turn a document that renders today into a thrown exception, which is not a patch-release
    ///   change, so it is clamped to the same auto meaning as zero. This is a maintainer decision
    ///   rather than the plan's own, since the plan left it open.
    ///   </item>
    /// </list>
    /// Auto columns share the width left over after the explicit ones, weighted by content and
    /// floored at their own minimum content width. If the explicit entries alone still leave the
    /// row over <paramref name="available"/> — the same "oversized" case an all-explicit array can
    /// reach on its own — they are scaled down to what the auto columns' floors leave them, rather
    /// than the floors being scaled down in turn.
    /// </summary>
    private double[] ReconcileExplicitWidths(TableElement table, double available, int cols)
    {
        var raw = new double[cols];
        var isAuto = new bool[cols];
        for (var i = 0; i < cols; i++)
        {
            // A missing entry (the array shorter than the column count) reads as 0.0, the same
            // auto sentinel as an explicit zero.
            raw[i] = i < table.ColWidths.Count ? table.ColWidths[i] : 0.0;

            // Auto covers four inputs, not two. A missing entry and an explicit zero are the
            // documented ones. A negative width and a non-finite one cannot be honoured at all,
            // and the alternative to treating them as auto is what this method emitted before:
            // measured on eedaa3c, an explicit NaN width put the token "NaN" where a PDF number
            // belongs, positive infinity put "Infinity" and negative infinity "-Infinity", and
            // each also poisoned the x operand of every following column. A stream carrying those
            // tokens is not a PDF, so replacing them changes only output that was already invalid.
            if (!double.IsFinite(raw[i]) || raw[i] <= 0)
            {
                isAuto[i] = true;
            }
        }

        var result = new double[cols];
        var explicitSum = 0.0;
        for (var i = 0; i < cols; i++)
        {
            if (isAuto[i]) continue;
            result[i] = raw[i];
            explicitSum += raw[i];
        }

        var autoCount = 0;
        for (var i = 0; i < cols; i++) if (isAuto[i]) autoCount++;

        var autoFloorSum = 0.0;
        if (autoCount > 0)
        {
            var residual = Math.Max(0.0, available - explicitSum);
            var (minW, maxW) = ContentWidths(table, cols);

            var autoMaxTotal = 0.0;
            for (var i = 0; i < cols; i++) if (isAuto[i]) autoMaxTotal += maxW[i];

            for (var i = 0; i < cols; i++)
            {
                if (!isAuto[i]) continue;
                result[i] = autoMaxTotal > 0
                    ? Math.Max(minW[i], residual * maxW[i] / autoMaxTotal)
                    : residual / autoCount;
                autoFloorSum += result[i];
            }
        }

        // An auto column's floor is a minimum, not a target, so the overflow this whole method
        // exists to correct has to come out of the explicit columns first: an oversized explicit
        // entry is what asked for more than the table has, not the auto column sized from what was
        // left over. Shrinking the auto column instead would crush "c2" below its own longest
        // word's width and reach TableRenderer's hard-break path (#473) for content that was never
        // the reason the row overran -- measured on an oversized+short explicit array together,
        // where the residual is already zero, this was the difference between one drawn literal
        // and two hard-break fragments for the same short cell.
        var explicitBudget = Math.Max(0.0, available - autoFloorSum);
        if (explicitSum > explicitBudget && explicitSum > 0)
        {
            var scale = explicitBudget / explicitSum;
            for (var i = 0; i < cols; i++)
                if (!isAuto[i]) result[i] *= scale;
        }

        // Last-resort safety net: only reached when the auto floors alone already exceed
        // `available`, so even an explicit budget of zero was not enough.
        ScaleToFit(result, available);
        return result;
    }

    /// <summary>
    /// Distributes <paramref name="available"/> across every column proportionally to its
    /// max-content width, floored at its own min-content (longest word) width (#468).
    /// </summary>
    private static double[] AutoWidth(TableElement table, double available, int cols)
    {
        var (minW, maxW) = ContentWidths(table, cols);

        // Distribute available width proportionally to max-content widths
        var totalMax = maxW.Sum();
        var result = new double[cols];
        for (var i = 0; i < cols; i++)
        {
            result[i] = totalMax > 0
                ? Math.Max(minW[i], available * maxW[i] / totalMax)
                : available / cols;
        }

        // The floor above has no upper bound of its own: when every column's longest word alone
        // is wider than its proportional share, the sum of the floors can exceed `available`, and
        // nothing before this scaled it back down (#468) -- the table then drew past the content
        // box, and past the page once the overrun was large enough. Scaling every column down
        // proportionally keeps their relative sizes rather than truncating the rightmost one.
        ScaleToFit(result, available);
        return result;
    }

    /// <summary>
    /// Scales <paramref name="widths"/> down in place, proportionally, so their sum does not
    /// exceed <paramref name="available"/>. A no-op when the row already fits.
    /// </summary>
    private static void ScaleToFit(double[] widths, double available)
    {
        var total = 0.0;
        foreach (var w in widths) total += w;

        if (total <= available || total <= 0) return;

        var scale = available / total;
        for (var i = 0; i < widths.Length; i++) widths[i] *= scale;
    }

    /// <summary>
    /// Per-column min-content (longest word) and max-content (full text) widths, shared by the
    /// fully-auto path and the residual distribution <see cref="ReconcileExplicitWidths"/> runs for
    /// the columns an explicit array leaves auto.
    /// </summary>
    private static (double[] MinW, double[] MaxW) ContentWidths(TableElement table, int cols)
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
        return (minW, maxW);
    }
}
