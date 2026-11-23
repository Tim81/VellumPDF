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
///      column count: a missing entry, an explicit zero and a negative entry all mean auto (see
///      <see cref="TableElement.ColWidths"/> for the first two; the negative one is a clamp
///      rather than a documented meaning), and so does a non-finite entry, which cannot be
///      honoured at all. The auto columns share the width left over after the explicit ones. If
///      the explicit entries alone still overrun the available width, they are scaled down toward
///      what the auto columns' own content floors leave them — or, on the rarer path where even
///      those floors alone exceed the available width, scaled down together with the floors by
///      the same ratio, so neither set is forced to zero while the other keeps its own floor.
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

    /// <summary>
    /// The cells, as (row, cell) positions in authoring order, that are sized from their whole
    /// text, control characters included, because the table could not hold their line breaks. Null
    /// when every cell's breaks are honoured.
    /// </summary>
    public IReadOnlySet<(int Row, int Cell)>? OneLineCells { get; init; }

    /// <summary>
    /// The measurements an earlier resolution of the same table already made, or null to measure
    /// every cell. Which cells are read unbroken is part of an entry's key.
    /// </summary>
    public TableMeasureCache? Cache { get; init; }

    /// <summary>
    /// The per-column minimum and maximum content widths to resolve from, in place of measuring the
    /// cells, together with the column count they were taken for. Null to measure.
    /// </summary>
    public (double[] MinW, double[] MaxW)? Content { get; init; }

    /// <summary>
    /// The per-column minimum and maximum content widths the resolution used, or null when it read
    /// none: every column has a width of its own, or there are no columns.
    /// </summary>
    public (double[] MinW, double[] MaxW)? ContentUsed { get; private set; }

    public void Resolve(TableElement table, double availableWidth)
    {
        ContentUsed = null;
        // The column count is the widest row, not merely the first (#480 section 1). Nothing stops
        // a caller giving one row more cells than another: Row.AddCell checks no other row's arity,
        // and TableElement exposes no column count to check against. So a later row with an extra
        // cell was losing it entirely, because the grid never had a column for it. Measured before
        // this fix, a first row of two cells and a second of three drew four literals and not five.
        //
        // The same widening also reaches a row whose own cells already declare more columns than
        // an earlier row via ColSpan, not just a row with more cells: before this fix, a table
        // whose first row set the column count with two ordinary cells silently drew a later row's
        // ColSpan-2 cell at one column's width instead of the two it asked for, because the grid
        // never had a second column to give it. Measured, page 400x300 at 10pt margins, row 0 "a0
        // a1" and row 1 "b0" plus a ColSpan-2 "b1": before this fix the columns resolved to 190,
        // 190 and the span cell drew 190pt wide, one column's worth; now they resolve to 152, 152,
        // 76 and the span cell draws 228pt, exactly 152 + 76. Both shapes keep every literal inside
        // the content box, so the fix is not recovering lost content — it is honouring a span width
        // the caller set and the old count silently discarded, and any document with this shape now
        // resolves to different column widths than before.
        //
        // `checked` here is not a change in kind: LINQ's `Sum` this loop replaced already threw
        // `OverflowException` on a total past `int.MaxValue`, so a caller passing pathological
        // ColSpan values already got a loud failure rather than a document. An unchecked `+=` would
        // have turned that into a silently wrapped, wrong column count and a quietly wrong document
        // instead — the same class of regression the reconciliation below exists to avoid on the
        // width axis.
        ColCount = Content is { } known ? known.MinW.Length : 0;
        if (Content is null)
        {
            // Indexed, because an enumerator over a list of cells is one allocation per row, and
            // the decision resolves the grid again for every step that moves a column.
            var rows = table.Rows;
            for (var r = 0; r < rows.Count; r++)
            {
                var cells = rows[r].Cells;
                var rowCols = 0;
                checked
                {
                    for (var c = 0; c < cells.Count; c++) rowCols += cells[c].ColSpan;
                }
                if (rowCols > ColCount) ColCount = rowCols;
            }
        }
        if (ColCount == 0) { ColWidths = []; return; }

        ColWidths = table.ColWidths.Count > 0
            ? ReconcileExplicitWidths(table, availableWidth, ColCount)
            : AutoWidth(table, availableWidth, ColCount);
    }

    /// <summary>
    /// Reconciles an explicit <see cref="TableElement.ColWidths"/> array against the resolved
    /// column count (#477). Four things follow from treating an explicit array as a literal copy,
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
    ///   <item>
    ///   A non-finite width could not be honoured at all: measured on eedaa3c, it put the literal
    ///   token <c>NaN</c> or <c>Infinity</c>/<c>-Infinity</c> where a PDF number belongs, and
    ///   poisoned the <c>x</c> operand of every following column besides. The file itself stayed
    ///   well formed — only the content stream stopped conforming — but replacing the token
    ///   changes output that no viewer could have drawn correctly either way.
    ///   </item>
    /// </list>
    /// Auto columns share the width left over after the explicit ones, weighted by content and
    /// floored at their own minimum content width. If the explicit entries alone still leave the
    /// row over <paramref name="available"/> — the same "oversized" case an all-explicit array can
    /// reach on its own — they are scaled down toward what the auto columns' floors leave them,
    /// rather than the floors being scaled down in turn; see the comment ahead of
    /// <c>explicitBudget</c> below for the rarer case where even the floors do not fit.
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
            // each also poisoned the x operand of every following column. The file itself stayed
            // well formed; it was the content stream that stopped conforming, and no validator was
            // run against it, so replacing the token is a correctness fix rather than a proven
            // conformance one.
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

        // Each entry is finite here, but their sum need not be: three of 1e308 reach +Infinity, the
        // scale ratio becomes budget / Infinity = 0, and every explicit column is written at width 0,
        // an auto column keeping its content width, so cells that start at the same x are drawn on
        // top of one another. The file is valid, so it is kept; documented on ColWidths,
        // and 3.0 refuses it.

        var autoCount = 0;
        for (var i = 0; i < cols; i++) if (isAuto[i]) autoCount++;

        var autoFloorSum = 0.0;
        if (autoCount > 0)
        {
            var residual = Math.Max(0.0, available - explicitSum);
            var (minW, maxW) = ContentOf(table, cols);

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
        // the reason the row overran — measured on an oversized+short explicit array together,
        // where the residual is already zero, this was the difference between one drawn literal
        // and two hard-break fragments for the same short cell.
        //
        // That preference holds only while the auto floors themselves still fit: `explicitBudget`
        // below is what is left for the explicit columns once the floors are reserved, and it can
        // go negative when the floors alone already exceed `available` — a column whose longest
        // word is wider than an equal share, the same corner AutoWidth's own ScaleToFit exists for.
        // Reserving the explicit columns' whole sum in that case and scaling only them to a
        // negative-clamped-to-zero budget would zero every explicit column while the auto columns
        // kept their unreduced floor, re-introducing on the explicit side exactly the collapse this
        // method exists to avoid on the auto side. So a negative budget is left unresolved here:
        // both sets stay at their unscaled sum (explicit) and floor (auto) values, and the single
        // `ScaleToFit` call below — already needed as the last-resort net for the floors-alone
        // case — scales the whole array, explicit and auto together, by the one ratio
        // `available / (explicitSum + autoFloorSum)`. That keeps every column's share of the
        // shortfall proportional to what it asked for and neither set reaches zero.
        //
        // Be precise about the scope of that, because the two regimes meet at a cliff rather than
        // blending. The preference above holds while explicitBudget is positive, and it holds all
        // the way down: at a budget of one part in a million million the explicit columns are
        // scaled to it and the stream writes their width as 0 while the auto columns keep their
        // whole floor. Measured on a 200pt explicit entry against an auto floor of 956pt, an
        // available width of 956 gives 165.22 and 789.78, and 956.000000000001 gives 0 and 956.
        // That is the preference working as designed rather than a lapse in it, but "neither set
        // reaches zero" is true of the non-positive-budget regime and not of the method.
        //
        // The proportional regime has a cost in the other direction. An explicit entry far larger
        // than the table takes proportionally more of it, which can leave an auto column below
        // what its own content needs: measured on a 380pt available width against the same 956pt
        // floor, an explicit 5,000 resolves to 319.01 and 60.99 and renders, while 10,000 resolves
        // to 346.84 and 33.16 and the auto column's hard-broken content then makes the row taller
        // than the page, so the render refuses where the unscaled behaviour drew it.
        //
        // If available itself is zero or negative, ScaleToFit does scale, and to a non-positive
        // ratio: measured by calling the resolver directly, an available width of 0 gives every
        // column 0 and -50 gives negative widths. No document reaches it, because
        // TableRenderer.Layout returns Nothing for a non-positive area width and is the only caller.
        var explicitBudget = available - autoFloorSum;
        if (explicitBudget > 0 && explicitSum > explicitBudget)
        {
            var scale = explicitBudget / explicitSum;
            for (var i = 0; i < cols; i++)
                if (!isAuto[i]) result[i] *= scale;
        }

        // Last-resort safety net: reached whenever the row above did not already fit `available`
        // — either because the explicit columns were scaled down to their budget and the auto
        // floors alone still do not leave room for it, or because the auto floors alone already
        // exceeded `available` and the explicit columns were left unscaled above.
        ScaleToFit(result, available);
        return result;
    }

    /// <summary>
    /// Distributes <paramref name="available"/> across every column proportionally to its
    /// max-content width, floored at its own min-content (longest word) width (#468).
    /// </summary>
    private double[] AutoWidth(TableElement table, double available, int cols)
    {
        var (minW, maxW) = ContentOf(table, cols);

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
        // nothing before this scaled it back down (#468) — the table then drew past the content
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
    /// The content widths this resolution works from: the ones it was given, or the ones it
    /// measures. It keeps them for <see cref="ContentUsed"/>.
    /// </summary>
    private (double[] MinW, double[] MaxW) ContentOf(TableElement table, int cols)
    {
        var content = Content ?? ContentWidths(table, cols);
        ContentUsed = content;
        return content;
    }

    /// <summary>
    /// Per-column min-content (longest word) and max-content (full text) widths, shared by the
    /// fully-auto path and the residual distribution <see cref="ReconcileExplicitWidths"/> runs for
    /// the columns an explicit array leaves auto.
    /// </summary>
    private (double[] MinW, double[] MaxW) ContentWidths(TableElement table, int cols)
    {
        var minW = new double[cols];
        var maxW = new double[cols];

        var style = table.DefaultCellStyle ?? TextStyle.Default;

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var col = 0;
            var cells = table.Rows[r].Cells;
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = cells[c];
                if (col >= cols) break;
                var cellStyle = cell.Style ?? style;
                if (cell.Content is null)
                    throw new ArgumentNullException("content", "A table cell has a null text and a column is sized automatically.");

                // The widest word and the widest line of the lines the cell is measured in. A cell
                // of one line, which is every cell that holds no line break, measures as it always
                // has. They depend on the text, the style and the mode alone, so a decision that
                // measures the table again takes them from its cache.
                var honourBreaks = OneLineCells?.Contains((r, c)) != true;
                double longest, widest;
                if (Cache is null)
                {
                    (longest, widest) = MeasureCell(cell.Content, cellStyle, honourBreaks);
                }
                else
                {
                    ref var entry = ref Cache.Of(r, c, cells.Count, honourBreaks);
                    if (!entry.HasWidths)
                    {
                        (entry.LongestWord, entry.WidestLine) = MeasureCell(cell.Content, cellStyle, honourBreaks);
                        entry.HasWidths = true;
                        if (TableMeasureProbe.Current is { } miss) miss.WidthMisses++;
                    }
                    else if (TableMeasureProbe.Current is { } hit)
                    {
                        hit.WidthHits++;
                    }
                    (longest, widest) = (entry.LongestWord, entry.WidestLine);
                }
                var full = widest + cell.Padding.Horizontal;

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

    /// <summary>
    /// The widest word and the widest line of a cell's text, each line measured on its own.
    /// </summary>
    internal static (double Longest, double Widest) MeasureCell(string content, TextStyle style, bool honourBreaks)
    {
        var lines = TableRenderer.CellLines(content, honourBreaks);
        var longest = 0.0;
        var widest = 0.0;
        for (var l = 0; l < lines.Length; l++)
        {
            var words = lines[l].Split(' ');
            var lineLongest = words.Max(w => style.FontRef.MeasureString(w, style.FontSize));
            var lineWidth = style.FontRef.MeasureString(lines[l], style.FontSize);
            longest = l == 0 ? lineLongest : Math.Max(longest, lineLongest);
            widest = l == 0 ? lineWidth : Math.Max(widest, lineWidth);
        }
        return (longest, widest);
    }
}
