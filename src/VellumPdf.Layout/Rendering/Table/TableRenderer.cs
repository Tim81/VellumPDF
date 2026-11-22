// Copyright © Timothy van der Ham (@Tim81)
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
///   • Splits at row boundaries if the table crosses a page break, and between the lines of a
///     row that is taller than a page.
///   • The leading run of header rows is repeated at the top of each continuation page, when a
///     row that is not a header follows it.
///   • Rowspan cells: the page break is not placed inside a rowspan group that starts in a
///     data row; see <see cref="Cell.RowSpan"/>.
///
/// Draw:
///   • Fills cell backgrounds.
///   • Draws cell text, word-wrapped to the cell inner width.
///   • Draws collapsed borders (single shared line between cells).
/// </summary>
/// <remarks>
/// A table that resolves to no columns is refused from <see cref="Layout"/>. A table whose rows
/// are all headers has no repeatable header: its rows are laid out and drawn as the body, each
/// once, and the table splits between rows like any other. A cell of those rows that holds a line
/// is tagged as a header cell. A table that has a row that is not a header after its leading
/// header rows repeats those header rows on every page.
/// </remarks>
public sealed class TableRenderer : IRenderer, IPageBreakSource, IKeepSource, IPlacementSource, IContinuationGuard
{
    private readonly TableElement _table;
    private readonly int _startRow;  // first data row index (0-based)

    // True for an overflow renderer and for the split pieces made from one: it resumes a table
    // that an earlier renderer began, so it never holds the table's top.
    private bool _continuation;

    private double[] _colWidths = [];
    private double[] _rowHeights = [];
    private LayoutBox _occupied;

    // A row cut between two of its lines. The overflow renderer of a cut row carries the lines the
    // row was wrapped into and the line each of its cells resumes at, so a row of L lines over P
    // pages wraps its text once and not P times. _headSlice is the part of row _startRow this
    // renderer draws when that row resumes here; _tailSlice is the part of its last row, when that
    // row is cut again. Layout writes both, and gives them to the renderer that draws the page.
    private RowLines? _resumeLines;
    private int[]? _resumeFrom;
    private RowSlice? _headSlice;
    private RowSlice? _tailSlice;

    // The cells, as (row, cell) in authoring order, that this table lays out unbroken: wrapped at
    // spaces alone and measured with their control characters, and drawn without them. Cell.Content
    // says why. Layout writes it and gives it to the renderers that draw the pages; null when every
    // break is kept.
    private HashSet<(int Row, int Cell)>? _oneLine;

    // True when every cell is laid out unbroken, as the table was measured before breaks were
    // honoured. Layout sets it when the decision finds that the table, so measured, is refused:
    // the table is then refused with that measurement's refusal. It is decided with _oneLine, so it
    // goes with it.
    private bool _allUnbroken;

    // True for a renderer the document made because laying the table out with its breaks read
    // would take more page continuations than one element may: it reads every cell unbroken from
    // the start, as the table was measured before breaks were honoured, and says so in the
    // diagnostics. Copies carry it.
    private bool _readsUnbrokenForTheLimit;

    // An upper bound on the page continuations the decision's layout needs, set where the decision
    // is made; see ContinuationBoundOf.
    private long _continuationBound;

    // What the decision under way has measured of each cell; null outside it.
    private TableMeasureCache? _measured;

    // The per-column content widths the last resolution of the grid worked from, which the steps of
    // one decision keep up to date as cells give up their breaks. Null outside a decision and when
    // no column takes its width from its content.
    private (double[] MinW, double[] MaxW)? _content;

    // What _oneLine was decided for: the table width and the vertical extent of the two fresh
    // boxes.
    // A renderer that carries the decision to the next page reuses it, with the grid and the
    // heights it was made with, when it lays out at the same width and heights; Layout says what
    // that leaves out. The boxes' x is left out of the key: the decision never reads it, and
    // mirrored margins move it on every other page.
    private (double Width, double Top0, double Bottom0, double Top1, double Bottom1)? _decidedFor;

    /// <summary>The lines each cell of one row wraps into, at one table width.</summary>
    private sealed class RowLines(int row, double width, List<string>?[] cells)
    {
        public int Row { get; } = row;
        public double Width { get; } = width;

        /// <summary>One entry per cell of the row; null for a cell past the last column.</summary>
        public List<string>?[] Cells { get; } = cells;
    }

    /// <summary>
    /// The part of a cut row that one page draws: for each cell, <c>Count</c> lines from line
    /// <c>First</c>, and the height of the tallest cell slice.
    /// </summary>
    private sealed class RowSlice(RowLines source, int[] first, int[] count, double height, bool complete)
    {
        public RowLines Source { get; } = source;
        public int Row => Source.Row;
        public int[] First { get; } = first;
        public int[] Count { get; } = count;
        public double Height { get; } = height;

        /// <summary>True when every cell has all its remaining lines in this slice.</summary>
        public bool Complete { get; } = complete;

        public int TotalLines => Count.Sum();

        /// <summary>For each cell, the line a following slice resumes at.</summary>
        public int[] Next() => [.. First.Zip(Count, (f, c) => f + c)];
    }

    /// <summary>
    /// Creates a renderer for the table, optionally starting at row <paramref name="startRow"/>
    /// of <see cref="TableElement.Rows"/> for pagination.
    /// </summary>
    /// <remarks>
    /// <paramref name="startRow"/> indexes <see cref="TableElement.Rows"/> with the header rows
    /// counted. When a row that is not a header follows the leading header rows, those rows are
    /// drawn first on every page, then the other rows from <paramref name="startRow"/> on, so a
    /// start below zero or inside the header run draws every row. A table whose rows are all
    /// headers has no header run, so its rows are drawn from <paramref name="startRow"/> on, once
    /// each. A start past the last row lays out as <see cref="LayoutResult.Outcome.Nothing"/>, so a
    /// document saving it throws <see cref="InvalidOperationException"/> saying the element is too
    /// tall to fit. A null <paramref name="table"/> is refused here.
    /// <para>Do not pass a start outside the table's rows. 3.0 will throw from
    /// this call.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="table"/> is <see langword="null"/>.
    /// </exception>
    public TableRenderer(TableElement table, int startRow = 0)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
        _startRow = startRow;
    }

    // Explicit, because the renderer is a public class and an implicit member would be public API.
    // The mode belongs to the element, so a renderer added through Document.Add(IRenderer) honours
    // it as well.
    PageBreakMode IPageBreakSource.BreakBefore => _table.BreakBefore;

    bool IKeepSource.KeepWithNext => _table.KeepWithNext;

    bool IKeepSource.KeepTogether => _table.KeepTogether;

    // Explicit, as above. The anchor lands on the first line the table draws, which is its top
    // edge, so only the renderer the caller built, or the split piece made from it, reports it.
    // An overflow renderer never does, and a row cut between its lines resumes in an overflow
    // renderer whose start row is the cut row, which is row 0 when the first row is the one cut:
    // a test on the start row alone would report the table again from the next page. When the
    // overflow is laid out at another width the cut row restarts from its first line (see
    // Layout), but the table's top was already drawn by the first piece, so that restart is not
    // the top and reports nothing either.
    void IPlacementSource.ReportPlacements(PlacementSink sink)
    {
        if (!_continuation && _startRow == 0 && _table.Anchor is { } anchor)
            sink.ReportAnchor(_table, anchor, _occupied);
    }

    internal TableElement Element => _table;

    // Explicit, as above.
    long IContinuationGuard.ContinuationBound => _continuationBound;

    IRenderer IContinuationGuard.WithoutBreaks() =>
        new TableRenderer(_table, _startRow) { _continuation = _continuation, _readsUnbrokenForTheLimit = true };

    /// <summary>Resolves column widths and row heights, fitting as many rows as possible and splitting at row boundaries on overflow, or between the lines of a row that fits no fresh page.</summary>
    /// <remarks>
    /// Overflow splits at a row boundary. A row that does not fit the area is cut between its
    /// lines instead when it fits neither box a fresh page offers, and at least one line of one
    /// of its cells fits the area; otherwise the table breaks before it. Each cell is cut at its
    /// own line, and the split renderer and the overflow renderer between them draw every line of
    /// the row once. An area with no width returns <see cref="LayoutResult.Outcome.Nothing"/>
    /// before anything is checked. When the document calls this method, the exceptions it lists
    /// reach you from the save.
    /// <para><b>A row is first cut only under a context the document built.</b> The document
    /// tells the renderer what a fresh page offers through the context; a context built with the
    /// public constructor of <see cref="LayoutContext"/> carries no such box, so a row that does
    /// not fit is not cut. The table splits before that row as it does for any row that does not
    /// fit, keeping a <see cref="Cell.RowSpan"/> group whole; when it is the first row in the
    /// area the call returns <see cref="LayoutResult.Outcome.Nothing"/>. A row that was already
    /// cut keeps cutting under such a context only at the width it was cut at. At another width
    /// the saved lines are dropped and the row is laid out again from its first line, which
    /// under a context without the box is not cut.</para>
    /// <para>A carriage return or line feed in a cell ends a line. A table can make some cells give
    /// up their breaks, among them cells in rows that are neither header rows nor joined by a
    /// <see cref="Cell.RowSpan"/>, and the save reports each as
    /// <see cref="LayoutDiagnosticCode.CellWhiteSpaceDropped"/>;
    /// <see cref="Cell.Content"/> says when and which. A context built with the public
    /// constructor of <see cref="LayoutContext"/> keeps every break.</para>
    /// <para>These are refused with the too-tall exception, extended with a sentence naming the
    /// rows or the cell, and are never cut: the repeated header rows when they do not fit a page;
    /// a row that declares a <see cref="Cell.RowSpan"/> above one, or lies under one; a row with a
    /// cell that cannot hold one line and its vertical padding on a page under the header rows.
    /// See <see cref="TableElement"/>.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The table resolves to no columns, or a cell's span, padding or font size, or the table's border
    /// width when it is not finite, is refused; see <see cref="TableElement.Rows"/>, <see cref="Cell.ColSpan"/>,
    /// <see cref="Cell.Padding"/>, <see cref="TableElement.BorderWidth"/>. A cell's text colour, and a negative
    /// border width, are refused the same way where the cell is drawn. The retry on a new page also
    /// throws it, saying the element is too tall, when the rows that remain cannot be placed on a
    /// page; the message then ends with the rows or the cell it names.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <see cref="VellumPdf.Canvas.PdfCanvas"/> refuses a non-finite <see cref="Cell.Background"/> or
    /// <see cref="TableElement.BorderColor"/> channel, or a non-finite edge of
    /// <see cref="TableElement.Margins"/>, where it would be written; see <see cref="Document"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// A cell's text is null and a column is sized automatically; <c>ParamName</c> is <c>content</c>.
    /// See <see cref="Cell"/>.
    /// </exception>
    /// <exception cref="OverflowException">
    /// A row's column spans sum past <see cref="int.MaxValue"/>; see <see cref="Cell.ColSpan"/>.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// The resolved column count is too large to allocate; see <see cref="Cell.ColSpan"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Text this method measures in an embedded font holds an unpaired surrogate; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public LayoutResult Layout(LayoutContext context)
    {
        var area = context.Area.Deflate(_table.Margins);
        if (area.Width <= 0) return LayoutResult.Nothing();

        _headSlice = null;
        _tailSlice = null;

        var rows = _table.Rows;
        var style = _table.DefaultCellStyle ?? TextStyle.Default;

        // A cut row resumed at the width it was cut at keeps the grid and heights the cut was made
        // with, and the lines it wrapped into. At another width they no longer describe the area,
        // so the row is laid out from its first line, like any other. Equals, not ==, because a
        // width that is NaN is the same width on the next page, and == would restart the row on
        // every page until the continuation limit.
        var resumed = _resumeLines is { } carried
            && carried.Width.Equals(area.Width)
            && _resumeFrom is not null
            && _colWidths.Length > 0
            && _rowHeights.Length == rows.Count;

        // The boxes a fresh page offers, when the caller says so. A row is cut between its lines
        // only when neither box would hold it whole, so a row that fits one of them still moves to
        // the next page intact.
        var fresh = FreshBoxes(context);

        if (!resumed)
        {
            _resumeLines = null;
            _resumeFrom = null;

            // A decision made for this width and these boxes by the renderer that handed the table
            // over stands: the cells it read unbroken are the cells this layout reads unbroken.
            var key = fresh is { Length: 2 }
                ? (area.Width, fresh[0].Y, fresh[0].Bottom, fresh[1].Y, fresh[1].Bottom)
                : ((double, double, double, double, double)?)null;
            var decided = key is not null && _decidedFor is { } was && was.Equals(key.Value);
            if (!decided)
            {
                _oneLine = null;
                _allUnbroken = false;
            }

            // The grid and the heights that decision was made with stand too: they follow from the
            // table, the width and the cells read unbroken, which are all the same. The renderer
            // that made the decision resolved the grid and ran every check below on it, so a
            // renderer that carries it to a later page checks and resolves nothing again.
            if (decided && _colWidths.Length > 0 && _rowHeights.Length == rows.Count)
            {
                if (TableMeasureProbe.Current is { } held) held.GridsReused++;
            }
            else
            {
                // Before the grid resolves: a ColSpan of zero gives a column count of zero, so
                // Resolve returns no widths and Layout gives up below, and the caller was told the
                // table was too tall to fit (#481). Checking here names the cell instead.
                for (var r = 0; r < _table.Rows.Count; r++)
                {
                    var cells = _table.Rows[r].Cells;
                    for (var c = 0; c < cells.Count; c++)
                        LayoutValidation.ValidateCell(cells[c], r, c);
                }

                LayoutValidation.ValidateLineWidthIsFinite(_table.BorderWidth, "A table");

                // A decision that reads some cells unbroken measures the table once per step, so
                // it keeps what each cell measures: nothing else depends on the cells' text, and
                // the cache is dropped with the decision. A table that reads no cell unbroken
                // measures once.
                var passesNeeded = !decided && fresh is not null && AnyControlSpace(rows);

                // A test measures the table as it was before breaks were honoured: every cell as
                // one line, with nothing to decide. So does the renderer made for a table whose
                // breaks would take it past the limit of page continuations.
                if (_readsUnbrokenForTheLimit
                    || (passesNeeded && TableMeasureProbe.Current is { ReadEveryCellUnbroken: true }))
                {
                    _allUnbroken = true;
                    passesNeeded = false;
                }

                var cache = passesNeeded ? new TableMeasureCache(rows.Count) : null;

                ResolveGrid(area.Width, style, cache);

                // Rows that cannot be cut between their lines must not be left without room on a
                // page by reading a cell's line breaks, or its other control white space as
                // spaces; Cell.Content says when, and which cells give them up. The cells that
                // would leave them so are laid out unbroken, and the grid and the heights are
                // taken again. Each pass adds cells, so this ends; most tables, which hold no
                // control white space, take no second pass.
                if (passesNeeded)
                {
                    while (CellsToReadAsOneLine(fresh!, style, area.Width, cache!) is { } more)
                    {
                        _oneLine ??= [];
                        _oneLine.UnionWith(more);
                        ResolveGrid(area.Width, style, cache);
                    }

                    // A table that 2.3.3 refused stays refused: the decision above keeps the breaks
                    // of the cells that left every header and group room, and that can leave the
                    // table fit where it measures every cell as one line and is refused. Cell.Content
                    // says why.
                    if (AnyControlSpaceKeepsItsBreaks(rows))
                    {
                        var (widths, heights) = (_colWidths, _rowHeights);
                        _allUnbroken = true;

                        // A trial that is dropped must leave nothing in the file: a tab measured
                        // in an embedded font would otherwise add glyph 0 to its subset.
                        FontReference.MeasuringWithoutRegistering = true;
                        try
                        {
                            ResolveGrid(area.Width, style, cache);
                        }
                        finally
                        {
                            FontReference.MeasuringWithoutRegistering = false;
                        }
                        if (!RefusedOnAFreshPage(fresh!, style))
                        {
                            _allUnbroken = false;
                            (_colWidths, _rowHeights) = (widths, heights);
                        }
                    }
                }
                _measured = null;
                _content = null;
                _decidedFor = key;
                if (fresh is not null && !_readsUnbrokenForTheLimit) _continuationBound = ContinuationBoundOf(fresh);
            }
        }

        // The repeatable header is the leading contiguous run of IsHeader rows — see
        // FindHeaderRowIndices for why a header row elsewhere in the table is not part of it.
        var headerRowIndices = FindHeaderRowIndices(rows);
        var headerHeight = headerRowIndices.Sum(i => _rowHeights[i]);
        var lastHeaderRow = headerRowIndices.Count > 0 ? headerRowIndices[^1] + 1 : 0;
        var dataStartRow = Math.Max(_startRow, lastHeaderRow);

        // Build a set of rows that are part of a rowspan group (cannot break inside a span).
        // For each rowspan cell, mark all rows from startRow+1 through startRow+rowSpan-1
        // as "rowspan continuation" — we must not split after any row that has a span
        // that continues into the next row.
        var rowspanBlockedAfter = BuildRowspanBlockedSet(rows, lastHeaderRow);

        var refusal = new RefusalContext(fresh, headerRowIndices, headerHeight, lastHeaderRow);

        var y = area.Y;
        var lastFit = dataStartRow - 1;
        var runHeight = headerHeight;
        var next = dataStartRow;
        RowSlice? head = null;
        RowSlice? tail = null;

        if (resumed)
        {
            // The cut row goes first, with as much of it as this area holds.
            var slice = CutRow(rows[_startRow], _resumeLines!, _resumeFrom!, y + runHeight, area.Bottom, style);
            if (slice.TotalLines == 0)
                return NothingFor(dataStartRow, refusal, style);

            runHeight += slice.Height;
            lastFit = dataStartRow;
            next = dataStartRow + 1;
            if (slice.Complete) head = slice;
            else tail = slice;
        }

        // Fit as many data rows as possible
        var breakRow = -1;
        if (tail is null)
        {
            for (var r = next; r < rows.Count; r++)
            {
                if (y + runHeight + _rowHeights[r] > area.Bottom + 0.001)
                {
                    breakRow = r;
                    break;
                }
                runHeight += _rowHeights[r];
                lastFit = r;
            }
        }

        // The first row that did not fit is cut between its lines when it fits neither fresh box,
        // and at least one line of it fits here. When no line fits, the table breaks before the
        // row, as it does for a row that fits a fresh box.
        if (breakRow >= 0 && CanCut(breakRow, refusal, style))
        {
            var lines = WrapRow(rows[breakRow], breakRow, area.Width, style);
            var slice = CutRow(rows[breakRow], lines, new int[lines.Cells.Length], y + runHeight, area.Bottom, style);
            if (slice.TotalLines > 0)
            {
                runHeight += slice.Height;
                lastFit = breakRow;
                tail = slice;
            }
        }

        // Walk back from lastFit to find a row we're allowed to break after.
        while (tail is null && lastFit >= dataStartRow && rowspanBlockedAfter.Contains(lastFit))
        {
            // Can't break after lastFit — back up one row.
            runHeight -= _rowHeights[lastFit];
            lastFit--;
        }

        if (lastFit < dataStartRow)
        {
            // Nothing fits beyond headers (or all fitted rows are inside a span)
            return NothingFor(dataStartRow, refusal, style);
        }

        _headSlice = head;
        _tailSlice = tail;
        _occupied = area.WithHeight(runHeight);

        if (tail is null && lastFit == rows.Count - 1)
            return LayoutResult.Full(_occupied);

        // Partial
        var split = new TableRenderer(_table, _startRow)
        {
            _continuation = _continuation,
            _colWidths = _colWidths,
            _rowHeights = _rowHeights,
            _occupied = _occupied,
            _headSlice = head,
            _tailSlice = tail,
            _oneLine = _oneLine,
            _allUnbroken = _allUnbroken,
            _readsUnbrokenForTheLimit = _readsUnbrokenForTheLimit,
            _decidedFor = _decidedFor,
        };
        var overflow = tail is null
            ? new TableRenderer(_table, lastFit + 1) { _continuation = true, _colWidths = _colWidths, _rowHeights = _rowHeights, _oneLine = _oneLine, _allUnbroken = _allUnbroken, _readsUnbrokenForTheLimit = _readsUnbrokenForTheLimit, _decidedFor = _decidedFor }
            : new TableRenderer(_table, tail.Row)
            {
                _continuation = true,
                _colWidths = _colWidths,
                _rowHeights = _rowHeights,
                _resumeLines = tail.Source,
                _resumeFrom = tail.Next(),
                _oneLine = _oneLine,
                _allUnbroken = _allUnbroken,
                _readsUnbrokenForTheLimit = _readsUnbrokenForTheLimit,
                _decidedFor = _decidedFor,
            };
        return LayoutResult.Partial(_occupied, split, overflow);
    }

    /// <summary>
    /// Resolves the column widths and every row's height, reading the breaks of the cells in
    /// <c>_oneLine</c> unbroken. The checks run in the order the exceptions are documented in.
    /// </summary>
    private void ResolveGrid(double areaWidth, TextStyle style, TableMeasureCache? cache)
    {
        if (TableMeasureProbe.Current is { } probe) probe.Passes++;
        _measured = cache;
        var rows = _table.Rows;
        var grid = new TableGridResolver { OneLineCells = _allUnbroken ? EveryCell.Instance : _oneLine, Cache = cache };
        grid.Resolve(_table, areaWidth);
        _colWidths = grid.ColWidths;
        _content = grid.ContentUsed;

        // #481's third refusal, and the one route to it that no span check covers: a table with no
        // rows, or rows holding no cells, resolves to no columns without any bad ColSpan, and
        // reporting Nothing here made DocumentRenderer blame the page size.
        LayoutValidation.ValidateColumnCount(_colWidths.Length);

        // Compute row heights
        _rowHeights = new double[rows.Count];

        for (var r = 0; r < rows.Count; r++)
            _rowHeights[r] = MeasureRow(r, style);
    }

    private double MeasureRow(int r, TextStyle style)
    {
        var row = _table.Rows[r];
        var maxH = 0.0;
        var col = 0;
        var cellIndex = -1;
        var rowCells = row.Cells;
        for (var k = 0; k < rowCells.Count; k++)
        {
            var cell = rowCells[k];
            cellIndex++;
            if (col >= _colWidths.Length) break;
            var cs = cell.Style ?? style;

            // The owner text is built only for the refusal: this runs for every cell on every pass.
            if (!double.IsFinite(cs.FontSize))
                LayoutValidation.ValidateFontSize(cs, $"Table row {r}, cell {cellIndex}");
            var colW = ColSpanWidth(col, cell.ColSpan);
            var innerW = colW - cell.Padding.Horizontal;
            var honourBreaks = HonoursBreaks(r, cellIndex);
            var wrapWidth = Math.Max(1, innerW);
            var lines = _measured is null
                ? WordWrapCount(cell.Content, cs, wrapWidth, honourBreaks)
                : CachedLineCount(_measured, (r, cellIndex, row.Cells.Count), cell.Content, cs, wrapWidth, honourBreaks);
            var cellH = lines * cs.EffectiveLeading + cell.Padding.Vertical;
            maxH = Math.Max(maxH, cellH);
            col += cell.ColSpan;
        }
        return maxH;
    }

    /// <summary>
    /// The number of lines <see cref="WordWrapLines"/> wraps the text into at
    /// <paramref name="width"/>, from the decision's cache when the width lies in the interval the
    /// cached count was found for.
    /// </summary>
    private static int CachedLineCount(
        TableMeasureCache cache, (int Row, int Cell, int CellCount) at, string text, TextStyle style,
        double width, bool honourBreaks)
    {
        ref var entry = ref cache.Of(at.Row, at.Cell, at.CellCount, honourBreaks);
        var probe = TableMeasureProbe.Current;
        if (entry.Holds(width))
        {
            if (probe is not null)
            {
                probe.WrapHits++;
                if (probe.Verify && WordWrapCount(text, style, width, honourBreaks) != entry.Lines)
                    throw new InvalidOperationException(
                        $"A cached line count of {entry.Lines} does not hold at width {width:R}.");
            }
            return entry.Lines;
        }

        if (probe is not null) probe.WrapMisses++;
        entry.Lines = CountLines(text, style, width, honourBreaks, out entry.From, out entry.Below);
        entry.HasLines = true;
        return entry.Lines;
    }

    /// <summary>
    /// The number of lines <see cref="WordWrapLines"/> returns, without building them, and the
    /// widths over which the count is the same. Every comparison of a width against a measurement
    /// that the wrap makes, including the ones that decide where a word wider than the width is
    /// broken at its characters, narrows <paramref name="from"/> and <paramref name="below"/>:
    /// the wrap takes the same path at every width in [from, below), and so returns the same
    /// lines. This method follows <see cref="WrapLine"/> and <see cref="HardBreakWord"/> step for
    /// step, an empty word included, and takes the same measurements from the font in the same
    /// order.
    /// </summary>
    internal static int CountLines(
        string text, TextStyle style, double maxWidth, bool honourBreaks,
        out double from, out double below)
    {
        from = double.NegativeInfinity;
        below = double.PositiveInfinity;
        if (string.IsNullOrEmpty(text)) return 1;

        var total = 0;
        foreach (var line in CellLines(text, honourBreaks))
            total += CountLine(line, style, maxWidth, ref from, ref below);
        return total;
    }

    private static int CountLine(
        string text, TextStyle style, double maxWidth, ref double from, ref double below)
    {
        if (string.IsNullOrEmpty(text)) return 1;

        var words = text.Split(' ');
        var spaceW = style.FontRef.MeasureString(" ", style.FontSize);

        // The length of the line under way, as WrapLine's buffer holds it: an empty word at the
        // start of a line leaves the buffer empty, so the next word starts a line again.
        var length = 0;
        var lineW = 0.0;
        var lines = 0;

        foreach (var word in words)
        {
            var ww = style.FontRef.MeasureString(word, style.FontSize);
            if (length > 0)
            {
                var joined = lineW + spaceW + ww;
                if (joined <= maxWidth)
                {
                    from = Math.Max(from, joined);
                    length += 1 + word.Length;
                    lineW += spaceW + ww;
                    continue;
                }

                below = Math.Min(below, joined);
                lines++;
                length = 0;
                lineW = 0.0;
            }

            // The start of a line. A word wider than the line is broken at its characters, which
            // adds its fragments and leaves no line under way.
            if (ww > maxWidth)
            {
                below = Math.Min(below, ww);
                lines += CountHardBreakFragments(word, style, maxWidth, ref from, ref below);
                continue;
            }
            from = Math.Max(from, ww);
            length = word.Length;
            lineW = ww;
        }
        if (length > 0) lines++;
        return lines == 0 ? 1 : lines;
    }

    /// <summary>
    /// The number of lines <see cref="HardBreakWord"/> adds for a word. A fragment is cut where
    /// the next character no longer fits, so each such comparison narrows the widths the count
    /// holds over, like the ones in <see cref="CountLine"/>.
    /// </summary>
    private static int CountHardBreakFragments(
        string word, TextStyle style, double maxWidth, ref double from, ref double below)
    {
        var fragments = 0;
        var open = false;
        var fragmentW = 0.0;
        foreach (var rune in word.EnumerateRunes())
        {
            var charW = style.FontRef.MeasureString(RuneString(rune), style.FontSize);
            if (open)
            {
                var grown = fragmentW + charW;
                if (grown > maxWidth)
                {
                    below = Math.Min(below, grown);
                    fragments++;
                    fragmentW = 0.0;
                    open = false;
                }
                else
                {
                    from = Math.Max(from, grown);
                }
            }
            open = true;
            fragmentW += charW;
        }
        if (open) fragments++;
        return fragments;
    }

    // The text of a rune, taken from a table for the characters below U+0100 so that measuring a
    // long word one character at a time does not allocate a string per character.
    private static readonly string[] s_latin1Strings = BuildLatin1Strings();

    private static string[] BuildLatin1Strings()
    {
        var table = new string[256];
        for (var i = 0; i < table.Length; i++) table[i] = ((char)i).ToString();
        return table;
    }

    private static string RuneString(System.Text.Rune rune) =>
        rune.Value < s_latin1Strings.Length ? s_latin1Strings[rune.Value] : rune.ToString();

    /// <summary>
    /// True when the cell's line breaks are kept; false when the table lays the cell out unbroken.
    /// </summary>
    private bool HonoursBreaks(int row, int cell) => !_allUnbroken && _oneLine?.Contains((row, cell)) != true;

    /// <summary>True when some cell holds control white space and the table reads its breaks.</summary>
    private bool AnyControlSpaceKeepsItsBreaks(IReadOnlyList<Row> rows)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Cells;
            for (var c = 0; c < cells.Count; c++)
                if (HonoursBreaks(r, c) && LayoutText.HasControlSpace(cells[c].Content)) return true;
        }
        return false;
    }

    /// <summary>The set of every cell, for a table that lays out every cell unbroken.</summary>
    private sealed class EveryCell : IReadOnlySet<(int Row, int Cell)>
    {
        public static readonly EveryCell Instance = new();

        public int Count => throw new NotSupportedException();
        public bool Contains((int Row, int Cell) item) => true;
        public bool IsProperSubsetOf(IEnumerable<(int Row, int Cell)> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<(int Row, int Cell)> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<(int Row, int Cell)> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<(int Row, int Cell)> other) => throw new NotSupportedException();
        public bool Overlaps(IEnumerable<(int Row, int Cell)> other) => throw new NotSupportedException();
        public bool SetEquals(IEnumerable<(int Row, int Cell)> other) => throw new NotSupportedException();
        public IEnumerator<(int Row, int Cell)> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new NotSupportedException();
    }

    private static bool AnyControlSpace(IReadOnlyList<Row> rows)
    {
        foreach (var row in rows)
            foreach (var cell in row.Cells)
                if (LayoutText.HasControlSpace(cell.Content)) return true;
        return false;
    }

    // ── Cells that read their breaks as spaces ──

    /// <summary>
    /// The cells to lay out unbroken in the next pass, or null when there are none that are not
    /// already laid out unbroken. Which cells, and why, <see cref="Cell.Content"/> says. This
    /// method judges the header rows first and then each <see cref="Cell.RowSpan"/> group in
    /// order, and a group again after its own breaks are given up in fixed-width columns. A step
    /// that adds a cell in a header row, or in an automatically sized column, ends the pass,
    /// because every height has to be taken again before the next group is judged. The one
    /// exception is a step whose cells all lie in the group's own rows and leave every column
    /// width as it was, bit for bit: no other row measures differently then, so the group's rows
    /// are measured again and the next group is judged in the same pass. Each of the two header
    /// steps takes every cell it reaches at once, so the header ends at most two passes. When a
    /// pass finds nothing to change and a header or a group still has no room, the last step takes
    /// every cell in an automatically sized column that holds control white space.
    /// </summary>
    private HashSet<(int Row, int Cell)>? CellsToReadAsOneLine(
        LayoutBox[] fresh, TextStyle style, double areaWidth, TableMeasureCache cache)
    {
        var rows = _table.Rows;
        var header = FindHeaderRowIndices(rows);
        var lastHeaderRow = header.Count > 0 ? header[^1] + 1 : 0;
        var headerHeight = 0.0;
        foreach (var i in header) headerHeight += _rowHeights[i];

        HashSet<(int Row, int Cell)>? found = null;

        // The cells the step under way has added to found, in the order it added them.
        var step = new List<(int Row, int Cell)>();

        // The rows of the group being judged, and whether a cell was added that another group's
        // height can depend on: one in a row outside the group, which is a header row, or one in an
        // automatically sized column, whose width every row that has a cell in an automatic column
        // can follow. A cell added in a row of the group and in fixed-width columns only changes
        // that group's rows, so the groups after it are judged on the heights already taken.
        var own = (First: -1, Last: -1);
        var shared = false;

        // Whether a cell was added in a row outside the group, which changes that row's height
        // whatever the columns do.
        var outside = false;

        // True when the cells found so far, read unbroken, leave every column the width it has.
        // Then no row outside the group measures differently, so a step that stayed in the group's
        // rows does not end the pass: going on judges the next group on the heights a new pass
        // would start from. The widths are compared bit for bit, because a width that moved by
        // one unit in the last place can move a line break.
        bool ColumnWidthsHold()
        {
            if (outside) return false;

            // The widths the table would have with the cells of this step read unbroken. Each
            // column's minimum and maximum is the greatest of what its cells contribute, and a
            // cell read unbroken contributes at least what it did, so the step raises them to the
            // greater of the two and nothing else needs to be measured. When a cell would
            // contribute less, the table is measured again from scratch.
            var widths = WidthsWithStep(step);
            if (widths is null)
            {
                if (TableMeasureProbe.Current is { } again) again.WidthsMeasuredAgain++;
                widths = WidthsMeasuredAgain();
            }
            else if (TableMeasureProbe.Current is { } raised)
            {
                raised.WidthsRaised++;
                if (raised.Verify)
                {
                    var fresh = WidthsMeasuredAgain();
                    for (var i = 0; i < fresh.Length; i++)
                        if (fresh.Length != widths.Length || BitConverter.DoubleToInt64Bits(fresh[i]) != BitConverter.DoubleToInt64Bits(widths[i]))
                            throw new InvalidOperationException("Column widths raised by a step differ from the table measured again.");
                }
            }
            if (widths.Length != _colWidths.Length) return false;
            for (var i = 0; i < _colWidths.Length; i++)
                if (BitConverter.DoubleToInt64Bits(widths[i]) != BitConverter.DoubleToInt64Bits(_colWidths[i]))
                    return false;
            if (TableMeasureProbe.Current is { } probe) probe.StepsKeptPassGoing++;
            return true;
        }

        // The column widths with every cell of the pass so far read unbroken, measuring the whole
        // table. The step's cells are in neither _oneLine nor the measurements until the step is
        // taken, so they come from the set found.
        double[] WidthsMeasuredAgain()
        {
            var trial = new HashSet<(int Row, int Cell)>(_oneLine ?? []);
            trial.UnionWith(found!);
            var grid = new TableGridResolver { OneLineCells = trial, Cache = cache };
            grid.Resolve(_table, areaWidth);
            _content = grid.ContentUsed;
            return grid.ColWidths;
        }

        // The column widths with the cells of the step read unbroken, taken from the content widths
        // kept for the pass, or null when a cell would contribute less than it did.
        double[]? WidthsWithStep(List<(int Row, int Cell)> cells)
        {
            if (_content is not { } content) return null;
            foreach (var (r, c) in cells)
                if (!RaiseContentWidths(content, r, c, cache))
                    return null;

            var grid = new TableGridResolver { Content = content };
            grid.Resolve(_table, areaWidth);
            return grid.ColWidths;
        }

        void Note(int r, int c, int col, int span)
        {
            step.Add((r, c));
            if (r < own.First || r > own.Last) { shared = true; outside = true; return; }
            for (var k = 0; k < span && col + k < _colWidths.Length; k++)
                if (IsAutoColumn(col + k)) { shared = true; return; }
        }

        // True when a cell was added that the table did not already lay out unbroken. The cells of
        // rows first to last that hold a line break qualify.
        bool CollectBreaks(int first, int last)
        {
            var added = false;
            for (var r = first; r <= last && r < rows.Count; r++)
            {
                var cells = rows[r].Cells;
                var col = 0;
                for (var c = 0; c < cells.Count; c++)
                {
                    var span = cells[c].ColSpan;
                    if (HonoursBreaks(r, c) && LayoutText.HasLineBreak(cells[c].Content) && (found ??= []).Add((r, c)))
                    {
                        added = true;
                        Note(r, c, col, span);
                    }
                    col += span;
                }
            }
            return added;
        }

        // The cells that hold any control white space, in rows first to last or in the header rows,
        // or in an automatically sized column that one of those cells is in.
        bool CollectControlSpace(int first, int last)
        {
            var reach = new bool[_colWidths.Length];
            void Reach(int from, int to)
            {
                for (var r = from; r <= to && r < rows.Count; r++)
                {
                    var col = 0;
                    foreach (var cell in rows[r].Cells)
                    {
                        for (var k = 0; k < cell.ColSpan && col + k < reach.Length; k++) reach[col + k] = true;
                        col += cell.ColSpan;
                        if (col >= reach.Length) break;
                    }
                }
            }
            Reach(first, last);
            if (header.Count > 0) Reach(0, header[^1]);

            var added = false;
            for (var r = 0; r < rows.Count; r++)
            {
                var inside = (r >= first && r <= last) || (header.Count > 0 && r <= header[^1]);
                var cells = rows[r].Cells;
                var col = 0;
                for (var c = 0; c < cells.Count && col < reach.Length; c++)
                {
                    var span = cells[c].ColSpan;
                    if (HonoursBreaks(r, c) && LayoutText.HasControlSpace(cells[c].Content) && (inside || SharesAutoColumn(col, span, reach))
                        && (found ??= []).Add((r, c)))
                    {
                        added = true;
                        Note(r, c, col, span);
                    }
                    col += span;
                }
            }
            return added;
        }

        // True when a header or a group has no room and the steps above could take nothing more
        // from it. It is read only when the pass finds nothing else to change.
        var exhausted = false;

        // The header rows have no room when they do not fit a page, and when they leave a row below
        // them no room for one line of one of its cells.
        if (header.Count > 0 && (!FitsFresh(fresh, 0, headerHeight) || LeavesNoRoomForALine(fresh, headerHeight, lastHeaderRow, style)))
        {
            if (CollectBreaks(0, header[^1]) || CollectControlSpace(0, header[^1])) return found;
            exhausted = true;
        }

        foreach (var (first, last) in SpanGroups(lastHeaderRow))
        {
            // A group is judged again after its own breaks are given up, on its rows' new heights:
            // the steps after that one read them. The last step takes every cell it can reach in
            // one go, so judging the group again after it would find nothing more to take.
            while (true)
            {
                var height = 0.0;
                for (var i = first; i <= last; i++) height += _rowHeights[i];
                if (FitsFresh(fresh, headerHeight, height)) break;

                // A step that adds a cell the other groups' heights can depend on ends the pass,
                // because the next group would be judged by heights that the step changes, a
                // column's width among them, unless the widths come out as they were and the step
                // stays in this group's rows (ColumnWidthsHold). The header steps below end it too.
                // A step that stays in this group's rows and in fixed-width columns changes no
                // other group's height, so the next group is judged in the same pass.
                own = (first, last);
                shared = false;
                outside = false;
                step.Clear();
                if (CollectBreaks(first, last))
                {
                    if (shared && !ColumnWidthsHold()) return found;

                    // Only this group's rows are measured again, in the columns they have.
                    (_oneLine ??= []).UnionWith(found!);
                    for (var r = first; r <= last; r++) _rowHeights[r] = MeasureRow(r, style);
                    continue;
                }
                own = (-1, -1);
                if (header.Count > 0 && CollectBreaks(0, header[^1])) return found;
                own = (first, last);
                shared = false;
                outside = false;
                step.Clear();
                if (CollectControlSpace(first, last) && shared)
                {
                    // The last step takes every cell it can reach, so the group is not judged
                    // again after it; the cells are applied here when the pass goes on.
                    if (ColumnWidthsHold())
                    {
                        (_oneLine ??= []).UnionWith(found!);
                        for (var r = first; r <= last; r++) _rowHeights[r] = MeasureRow(r, style);
                    }
                    else
                    {
                        return found;
                    }
                }
                exhausted = true;
                break;
            }
        }

        // The widths of the columns that are not fixed follow from every cell in an automatically
        // sized column, whichever row it is in, and the columns that are fixed are scaled by what
        // those leave. When a header or a group still has no room, a cell in a row of no group
        // that is reached can be what made the columns too narrow: the widest line of it is
        // narrower than its whole text, so reading its breaks leaves less to share out than 2.3.3
        // measured. The last step reads every such cell unbroken (see Cell.Content), which gives
        // the widths 2.3.3 measured, and the heights it measured for each header or group that
        // still has no room. A group that fits keeps its breaks, so it can be taller than that.
        return found ?? (exhausted ? CollectAutoColumnControlSpace() : null);
    }

    /// <summary>
    /// Raises the per-column content widths by what cell <paramref name="c"/> of row
    /// <paramref name="r"/> contributes when it is read unbroken, as
    /// <see cref="TableGridResolver"/> would take it, and returns false, leaving the widths
    /// unusable, when the cell contributes less to some column than it did read as it is. A
    /// column's width is the greatest contribution of its cells, so a contribution that does not
    /// fall leaves that greatest equal to the one a measurement of the whole table finds, bit for
    /// bit, and one that is not a number is left to that measurement.
    /// </summary>
    private bool RaiseContentWidths((double[] MinW, double[] MaxW) content, int r, int c, TableMeasureCache cache)
    {
        var cells = _table.Rows[r].Cells;
        var cols = content.MinW.Length;
        var col = 0;
        for (var k = 0; k < c; k++) col += cells[k].ColSpan;
        if (col >= cols) return true;

        var cell = cells[c];
        ref var before = ref cache.Of(r, c, cells.Count, honourBreaks: true);
        if (!before.HasWidths) return false;

        ref var after = ref cache.Of(r, c, cells.Count, honourBreaks: false);
        if (!after.HasWidths)
        {
            var style = cell.Style ?? _table.DefaultCellStyle ?? TextStyle.Default;
            (after.LongestWord, after.WidestLine) = TableGridResolver.MeasureCell(cell.Content, style, honourBreaks: false);
            after.HasWidths = true;
            if (TableMeasureProbe.Current is { } miss) miss.WidthMisses++;
        }

        var share = cell.ColSpan;
        var padding = cell.Padding.Horizontal;
        var minBefore = (before.LongestWord / share) + padding;
        var minAfter = (after.LongestWord / share) + padding;
        var maxBefore = (before.WidestLine + padding) / share;
        var maxAfter = (after.WidestLine + padding) / share;
        if (!(minAfter >= minBefore) || !(maxAfter >= maxBefore)) return false;

        for (var s = 0; s < share && col + s < cols; s++)
        {
            content.MinW[col + s] = Math.Max(content.MinW[col + s], minAfter);
            content.MaxW[col + s] = Math.Max(content.MaxW[col + s], maxAfter);
        }
        return true;
    }

    /// <summary>
    /// Every cell that holds control white space and has a column that is sized automatically
    /// among those it spans, and is not laid out unbroken yet, or null when there is none. The
    /// widths of the columns come out of the cells of these columns alone, so with these cells
    /// unbroken, and those of the rows that have no room, every column is as wide as it was when
    /// every cell was measured as one line.
    /// </summary>
    private HashSet<(int Row, int Cell)>? CollectAutoColumnControlSpace()
    {
        var rows = _table.Rows;
        HashSet<(int Row, int Cell)>? found = null;
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Cells;
            var col = 0;
            for (var c = 0; c < cells.Count && col < _colWidths.Length; c++)
            {
                var span = cells[c].ColSpan;
                if (HonoursBreaks(r, c) && LayoutText.HasControlSpace(cells[c].Content) && TouchesAutoColumn(col, span))
                    (found ??= []).Add((r, c));
                col += span;
            }
        }
        return found;
    }

    /// <summary>
    /// True when a column that a cell at <paramref name="col"/> for <paramref name="span"/>
    /// columns is in is sized automatically.
    /// </summary>
    private bool TouchesAutoColumn(int col, int span)
    {
        for (var k = 0; k < span && col + k < _colWidths.Length; k++)
            if (IsAutoColumn(col + k)) return true;
        return false;
    }

    /// <summary>
    /// True when a column of <paramref name="reach"/> that a cell at <paramref name="col"/> for
    /// <paramref name="span"/> columns is in is automatically sized and is one that a relieved row
    /// reaches.
    /// </summary>
    private bool SharesAutoColumn(int col, int span, bool[] reach)
    {
        for (var k = 0; k < span && col + k < reach.Length; k++)
            if (reach[col + k] && IsAutoColumn(col + k)) return true;
        return false;
    }

    /// <summary>True when column <paramref name="i"/> takes its width from its content.</summary>
    private bool IsAutoColumn(int i)
    {
        var widths = _table.ColWidths;
        if (widths.Count == 0 || i >= widths.Count) return true;
        return !double.IsFinite(widths[i]) || widths[i] <= 0;
    }

    /// <summary>
    /// True when a row from <paramref name="firstDataRow"/> on has a cell that cannot hold one line
    /// under the header rows.
    /// </summary>
    private bool LeavesNoRoomForALine(LayoutBox[] fresh, double headerHeight, int firstDataRow, TextStyle style)
    {
        for (var r = firstDataRow; r < _table.Rows.Count; r++)
            if (CellBeyondProgress(r, fresh, headerHeight, style) is not null) return true;
        return false;
    }

    /// <summary>
    /// The runs of rows that the layout keeps whole, which the decision judges one at a time: the
    /// runs of rows held together by <see cref="Cell.RowSpan"/> declarations in the data rows,
    /// merged where one reaches into another, and each data row that lies under a span declared in
    /// a header row. The same runs as <see cref="BuildRowspanBlockedSet"/> never splits, found in
    /// one pass over the rows, in order.
    /// </summary>
    /// <remarks>
    /// A span declared in a header row holds the data rows under it together as far as the cut is
    /// concerned (<see cref="InSpanGroup"/>), but the layout may still end a page between them, so
    /// each of those rows is judged alone: a run of them is not a group, and counting one as a
    /// group made rows that fit a page on their own give up their breaks.
    /// </remarks>
    private List<(int First, int Last)> SpanGroups(int firstDataRow)
    {
        var rows = _table.Rows;
        var groups = new List<(int First, int Last)>();
        var headerReach = -1;
        for (var q = 0; q < rows.Count; q++)
        {
            var reach = 1;
            var qCells = rows[q].Cells;
            for (var k = 0; k < qCells.Count; k++) reach = Math.Max(reach, qCells[k].RowSpan);
            if (reach <= 1) continue;

            var end = (int)Math.Min((long)q + reach - 1, rows.Count - 1);
            if (q < firstDataRow)
            {
                headerReach = Math.Max(headerReach, end);
                continue;
            }

            if (groups.Count > 0 && q <= groups[^1].Last)
                groups[^1] = (groups[^1].First, Math.Max(groups[^1].Last, end));
            else
                groups.Add((q, end));
        }

        if (headerReach < firstDataRow) return groups;

        // The data rows under a header span that no group holds, each alone, in order with the rest.
        var all = new List<(int First, int Last)>(groups.Count + headerReach - firstDataRow + 1);
        var next = 0;
        for (var r = firstDataRow; r <= headerReach; r++)
        {
            while (next < groups.Count && groups[next].Last < r) all.Add(groups[next++]);
            if (next < groups.Count && groups[next].First <= r)
            {
                r = groups[next].Last;
                continue;
            }
            all.Add((r, r));
        }
        while (next < groups.Count) all.Add(groups[next++]);
        return all;
    }

    // ── Cutting a row between its lines ──

    /// <summary>What a layout call knows about the page a refused row would have to fit.</summary>
    private readonly record struct RefusalContext(
        LayoutBox[]? Fresh, List<int> HeaderRows, double HeaderHeight, int LastHeaderRow);

    /// <summary>
    /// The two boxes a fresh page can offer this table, or null when the caller did not say. The
    /// document offers the content area to the retry. At a page top it offers the taller of the
    /// content area and the area from the top of the content area to its bottom, which can differ
    /// from it by a rounding step. This method builds both heights, so either one is covered.
    /// </summary>
    private LayoutBox[]? FreshBoxes(LayoutContext context)
    {
        if (context.FreshPageArea is not { } page) return null;
        var pageTop = new LayoutBox(page.X, page.Y, page.Width, page.Bottom - page.Y);
        return [page.Deflate(_table.Margins), pageTop.Deflate(_table.Margins)];
    }

    /// <summary>True when a block of <paramref name="height"/> fits a fresh box.</summary>
    private static bool FitsFresh(LayoutBox[] fresh, double headerHeight, double height)
    {
        foreach (var box in fresh)
            if (!(box.Y + headerHeight + height > box.Bottom + 0.001))
                return true;
        return false;
    }

    /// <summary>
    /// An upper bound on the page continuations that laying the table out from
    /// <c>_rowHeights</c> and <c>_colWidths</c> takes, over every page.
    /// </summary>
    /// <remarks>
    /// A continuation is a layout call that answers Partial, or Nothing and is retried on the next
    /// page. Every Partial takes at least one row, or at least one line of a row cut between its
    /// lines, so the Partials number at most the units less one, where a row is one unit unless
    /// it fits neither fresh box, when it is cut and counts a unit for each of its lines. A
    /// Nothing is followed by a call that makes progress, which is a Partial or the last call, so
    /// the Nothings number at most the Partials and one. Together at most twice the units less
    /// one.
    /// </remarks>
    private long ContinuationBoundOf(LayoutBox[] fresh)
    {
        var rows = _table.Rows;
        var header = FindHeaderRowIndices(rows);
        var headerHeight = 0.0;
        foreach (var i in header) headerHeight += _rowHeights[i];
        var style = _table.DefaultCellStyle ?? TextStyle.Default;

        var units = 0L;
        for (var r = Math.Max(_startRow, header.Count > 0 ? header[^1] + 1 : 0); r < rows.Count; r++)
        {
            if (FitsFresh(fresh, headerHeight, _rowHeights[r]))
            {
                units++;
                continue;
            }

            var lines = 0L;
            var cells = rows[r].Cells;
            var col = 0;
            for (var c = 0; c < cells.Count && col < _colWidths.Length; c++)
            {
                var cell = cells[c];
                var innerW = ColSpanWidth(col, cell.ColSpan) - cell.Padding.Horizontal;
                lines += WordWrapCount(cell.Content, cell.Style ?? style, Math.Max(1, innerW), HonoursBreaks(r, c));
                col += cell.ColSpan;
            }
            units += Math.Max(1, lines);
        }
        return (2 * units) - 1;
    }

    /// <summary>
    /// True when laying the table out from <c>_rowHeights</c> and <c>_colWidths</c>, page after
    /// page, ends with a page that answers <see cref="LayoutResult.Outcome.Nothing"/> from the top
    /// of a fresh page, which the save refuses with the too-tall exception.
    /// </summary>
    /// <remarks>
    /// The layout answers Nothing where no row fits the page it is given and the first row cannot
    /// be cut. It is given a fresh page after every break, and a break falls only where
    /// <see cref="BuildRowspanBlockedSet"/> allows one, so the layout is refused exactly when
    /// the header rows do not fit a page, or a run of rows it never breaks inside does not fit
    /// one, and the run is not a single row that can be cut between its lines. Fitting is the fit
    /// loop's own test, row by row on the running height, on each of the two boxes a fresh page
    /// can offer.
    /// </remarks>
    private bool RefusedOnAFreshPage(LayoutBox[] fresh, TextStyle style)
    {
        var rows = _table.Rows;
        var header = FindHeaderRowIndices(rows);
        var lastHeaderRow = header.Count > 0 ? header[^1] + 1 : 0;
        var headerHeight = 0.0;
        foreach (var i in header) headerHeight += _rowHeights[i];

        var first = Math.Max(_startRow, lastHeaderRow);
        if (first >= rows.Count) return false;
        if (header.Count > 0 && !FitsFresh(fresh, 0, headerHeight)) return true;

        var blocked = BuildRowspanBlockedSet(rows, lastHeaderRow);
        var ctx = new RefusalContext(fresh, header, headerHeight, lastHeaderRow);
        for (var s = first; s < rows.Count;)
        {
            var last = s;
            while (blocked.Contains(last)) last++;
            if (!RunFitsFresh(fresh, headerHeight, s, last) && (last != s || !CanCut(s, ctx, style)))
                return true;
            s = last + 1;
        }
        return false;
    }

    /// <summary>
    /// True when rows <paramref name="first"/> to <paramref name="last"/>, taken one after the
    /// other under the header rows, fit one of the fresh boxes by the fit loop's own arithmetic.
    /// </summary>
    private bool RunFitsFresh(LayoutBox[] fresh, double headerHeight, int first, int last)
    {
        foreach (var box in fresh)
        {
            var run = headerHeight;
            var fits = true;
            for (var r = first; r <= last && fits; r++)
            {
                fits = !(box.Y + run + _rowHeights[r] > box.Bottom + 0.001);
                run += _rowHeights[r];
            }
            if (fits) return true;
        }
        return false;
    }

    /// <summary>
    /// True when row <paramref name="r"/>, which did not fit the area, can be cut between its
    /// lines: the caller supplied the fresh boxes, the row would fit neither of them whole, nothing
    /// forbids cutting it, and every cell of it holds a line on a fresh page.
    /// </summary>
    private bool CanCut(int r, RefusalContext ctx, TextStyle style)
    {
        if (ctx.Fresh is not { } fresh) return false;
        if (FitsFresh(fresh, ctx.HeaderHeight, _rowHeights[r])) return false;
        if (ctx.HeaderRows.Contains(r)) return false;
        if (InSpanGroup(r)) return false;
        return CellBeyondProgress(r, fresh, ctx.HeaderHeight, style) is null;
    }

    /// <summary>
    /// True when row <paramref name="r"/> declares a <see cref="Cell.RowSpan"/> above one, or lies
    /// under one declared by an earlier row, header rows included.
    /// </summary>
    private bool InSpanGroup(int r)
    {
        var rows = _table.Rows;
        for (var q = 0; q <= r && q < rows.Count; q++)
            foreach (var cell in rows[q].Cells)
                if (cell.RowSpan > 1 && (long)q + cell.RowSpan - 1 >= r)
                    return true;
        return false;
    }

    /// <summary>
    /// The first cell of row <paramref name="r"/> that cannot hold one line, with its padding, in a
    /// fresh box under the header rows, or null when every cell can. Such a cell would make no
    /// progress however many pages the row was given.
    /// </summary>
    private (int Cell, double Needed, double Available)? CellBeyondProgress(
        int r, LayoutBox[] fresh, double headerHeight, TextStyle style)
    {
        var cells = _table.Rows[r].Cells;
        var col = 0;
        for (var c = 0; c < cells.Count; c++)
        {
            if (col >= _colWidths.Length) break;
            var cell = cells[c];
            var cs = cell.Style ?? style;
            var needed = 1 * cs.EffectiveLeading + cell.Padding.Vertical;
            foreach (var box in fresh)
                if (box.Y + headerHeight + needed > box.Bottom + 0.001)
                    return (c, needed, Math.Min(fresh[0].Bottom, fresh[1].Bottom) - fresh[0].Y - headerHeight);
            col += cell.ColSpan;
        }
        return null;
    }

    /// <summary>
    /// Wraps every cell of a row once, at the widths the grid gave it. The lines are what a cut row
    /// is drawn from, on every page it spans.
    /// </summary>
    private RowLines WrapRow(Row row, int rowIndex, double width, TextStyle style)
    {
        var cells = new List<string>?[row.Cells.Count];
        var col = 0;
        for (var c = 0; c < row.Cells.Count; c++)
        {
            if (col >= _colWidths.Length) break;
            var cell = row.Cells[c];
            var cs = cell.Style ?? style;
            var innerW = ColSpanWidth(col, cell.ColSpan) - cell.Padding.Horizontal;
            cells[c] = WordWrapLines(cell.Content, cs, Math.Max(1, innerW), HonoursBreaks(rowIndex, c));
            col += cell.ColSpan;
        }
        return new RowLines(rowIndex, width, cells);
    }

    /// <summary>
    /// Takes from each cell of the row, starting at the line given for it, the most lines whose
    /// height with the cell's padding fits between <paramref name="top"/> and
    /// <paramref name="bottom"/>, by the same test the fit loop applies to a whole row.
    /// </summary>
    private static RowSlice CutRow(Row row, RowLines lines, int[] from, double top, double bottom, TextStyle style)
    {
        var count = new int[lines.Cells.Length];
        var height = 0.0;
        var complete = true;
        for (var c = 0; c < lines.Cells.Length; c++)
        {
            if (lines.Cells[c] is not { } cellLines) continue;
            var cell = row.Cells[c];
            var cs = cell.Style ?? style;
            var remaining = cellLines.Count - from[c];
            var n = LinesThatFit(top, bottom, cs.EffectiveLeading, cell.Padding.Vertical, remaining);
            count[c] = n;
            if (n < remaining) complete = false;
            if (n > 0) height = Math.Max(height, n * cs.EffectiveLeading + cell.Padding.Vertical);
        }
        return new RowSlice(lines, from, count, height, complete);
    }

    /// <summary>
    /// The largest number of lines, up to <paramref name="remaining"/>, for which
    /// <c>top + (n * leading + padding) &gt; bottom + 0.001</c> is false: the fit loop's test for a
    /// whole row, so a cut and a fit agree on a block sitting exactly on the boundary.
    /// </summary>
    private static int LinesThatFit(double top, double bottom, double leading, double padding, int remaining)
    {
        if (remaining <= 0) return 0;
        bool Fits(int n) => !(top + (n * leading + padding) > bottom + 0.001);

        // A leading that is not positive makes a taller block no taller, so all of it fits or none.
        if (!(leading > 0)) return Fits(remaining) ? remaining : 0;

        var estimate = Math.Floor((bottom + 0.001 - top - padding) / leading);
        var n = double.IsNaN(estimate) ? 0 : (int)Math.Clamp(estimate, 0, remaining);
        while (n > 0 && !Fits(n)) n--;
        while (n < remaining && Fits(n + 1)) n++;
        return n;
    }

    // ── Refusals ──

    /// <summary>
    /// <see cref="LayoutResult.Nothing()"/>, with the reason when the content starting at
    /// <paramref name="row"/> could not be placed on a fresh page either.
    /// </summary>
    private LayoutResult NothingFor(int row, RefusalContext ctx, TextStyle style) =>
        RefusalReason(row, ctx, style) is { } reason ? LayoutResult.Nothing(reason) : LayoutResult.Nothing();

    /// <summary>
    /// Why the content starting at data row <paramref name="r"/> cannot be placed on a page of its
    /// own, or null when a fresh page holds it, or the caller did not supply the fresh boxes.
    /// </summary>
    private string? RefusalReason(int r, RefusalContext ctx, TextStyle style)
    {
        if (ctx.Fresh is not { } fresh || r >= _table.Rows.Count) return null;
        var smallest = fresh[0].Bottom <= fresh[1].Bottom ? fresh[0] : fresh[1];
        var pageHeight = smallest.Bottom - smallest.Y;

        if (ctx.HeaderRows.Count > 0 && !FitsFresh(fresh, 0, ctx.HeaderHeight))
        {
            return $"The repeated header ({RowRange(ctx.HeaderRows[0], ctx.HeaderRows[^1])}) is {Pt(ctx.HeaderHeight)} tall, " +
                $"but a page leaves {Pt(pageHeight)} for the table. Header rows repeat on every page and are never split.";
        }

        var below = ctx.HeaderRows.Count > 0 ? $" under the {Pt(ctx.HeaderHeight)} of repeated header rows" : "";

        if (InSpanGroup(r))
        {
            var (first, last) = SpanGroupOf(r, ctx.LastHeaderRow);
            var groupHeight = 0.0;
            for (var i = first; i <= last; i++) groupHeight += _rowHeights[i];
            if (FitsFresh(fresh, ctx.HeaderHeight, groupHeight)) return null;
            var leaves = Pt(pageHeight - ctx.HeaderHeight);
            return first == last
                ? $"Table row {first} takes part in a RowSpan, so it is never split between pages. " +
                  $"It is {Pt(groupHeight)} tall, but a page leaves {leaves}{below}."
                : $"Table rows {first} to {last} are joined by a RowSpan, so they are never split between pages. " +
                  $"Together they are {Pt(groupHeight)} tall, but a page leaves {leaves}{below}.";
        }

        if (FitsFresh(fresh, ctx.HeaderHeight, _rowHeights[r])) return null;

        if (CellBeyondProgress(r, fresh, ctx.HeaderHeight, style) is { } stuck)
        {
            return $"Table row {r}, cell {stuck.Cell} needs {Pt(stuck.Needed)} for its vertical padding and one line, " +
                $"but a page leaves {Pt(stuck.Available)}{below}, so the row cannot be split between pages.";
        }

        return null;
    }

    private static string Pt(double points) =>
        points.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "pt";

    private static string RowRange(int first, int last) =>
        first == last ? $"table row {first}" : $"table rows {first} to {last}";

    /// <summary>
    /// The data rows from the first to the last of the run of rows held together by RowSpan
    /// declarations that includes <paramref name="r"/>; the row alone when none holds it. A span
    /// declared in a header row starts the run at the first data row, because the header rows are
    /// counted separately.
    /// </summary>
    private (int First, int Last) SpanGroupOf(int r, int firstDataRow)
    {
        var rows = _table.Rows;
        var first = r;
        var last = r;
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var q = 0; q < rows.Count; q++)
            {
                var reach = 1;
                foreach (var cell in rows[q].Cells) reach = Math.Max(reach, cell.RowSpan);
                var end = (int)Math.Min((long)q + reach - 1, rows.Count - 1);
                if (reach <= 1 || end < first || q > last) continue;
                var start = Math.Max(q, firstDataRow);
                if (start < first) { first = start; changed = true; }
                if (end > last) { last = end; changed = true; }
            }
        }
        return (first, last);
    }

    private static string DroppedForRoom(long subject) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Table row {subject >> 32}, cell {subject & 0xFFFFFFFF} holds a tab, a line break or another control white-space character that is left out of what is drawn, because reading its line breaks, or its other control white space as spaces, would leave a row that cannot be cut no room on a page. The words it separated run together.");

    private static string DroppedForTheLimit(long subject) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Table row {subject >> 32}, cell {subject & 0xFFFFFFFF} holds a tab, a line break or another control white-space character that is left out of what is drawn, because reading the table's line breaks would take it more page continuations than one element may take. The words it separated run together.");

    /// <summary>Draws cell backgrounds, borders and text (repeating header rows) and builds the tagged Table struct tree when tagging is enabled.</summary>
    /// <remarks>
    /// See <see cref="IRenderer.Draw"/>.
    /// <para><b>Calling this method before <see cref="Layout"/> draws nothing when the table has no
    /// repeatable header, and throws <see cref="IndexOutOfRangeException"/> when it has one.</b>
    /// A repeatable header is a leading run of header rows followed by a row that is not a header.
    /// A table with no header row, or whose every row is a header, has none, and no area was laid
    /// out for it to draw into.</para>
    /// </remarks>
    /// <exception cref="IndexOutOfRangeException">
    /// This method is called before <see cref="Layout"/> on a table with a repeatable header, that
    /// is, leading header rows followed by a row that is not a header; see
    /// <see cref="IRenderer.Draw"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <b>A cell's text colour with a non-finite channel, and a negative table border width, are refused
    /// here and not in <see cref="Layout"/>,</b> because each is checked where it is written; a table
    /// that is laid out and not drawn is not refused for either. See <see cref="Document"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A cell background, the table's border colour or a coordinate written here is <c>NaN</c> or
    /// infinite, which <see cref="VellumPdf.Canvas.PdfCanvas"/> refuses (<c>ParamName</c> is null).
    /// A row background reaches here unchecked only if the row was put into the table some way
    /// other than <see cref="TableElement.Add(Row)"/>, which refuses such a channel.
    /// </exception>
    public void Draw(DrawContext ctx)
    {
        // Layout already deflated context.Area by _table.Margins and stored the result in
        // _occupied; deflating again here took the table's own margin out of the box twice. The
        // vertical half was the one that lost content rather than merely shifting it: _occupied's
        // height is exactly the rows that fit, with no margin allowance baked in, so a second top
        // deflate ate into that height directly and past a margin equal to the row's own height
        // left nothing to draw.
        var area = _occupied;
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

        // Which rows this page draws, resolved before any of them is drawn. A RowSpan is measured
        // against this set rather than taken from the cell: on a continuation page the repeated
        // header run is followed by the split row, not by the row the header's span was declared
        // over, so a span declared there covers nothing that this page puts below it. See
        // EffectiveRowSpan. The data half repeats the stop test the loop below uses, so the two
        // agree by construction — including the accumulation itself, which adds one row height at a
        // time rather than summing them, because a sum starting from zero and a running total
        // starting from the area's own Y can differ in the last bits and so disagree about a row
        // sitting exactly on the boundary.
        var drawnRows = new HashSet<int>(headerRowIndices);
        var probeY = rowY;
        foreach (var hi in headerRowIndices)
            probeY += _rowHeights[hi];
        for (var r = dataStartRow; r < rows.Count; r++)
        {
            if (probeY >= _occupied.Bottom - 0.001) break;
            drawnRows.Add(r);
            probeY += HeightOf(r);
            if (_tailSlice?.Row == r) break;
        }

        // Draw the leading contiguous run of header rows; every later row, header-flagged or not,
        // is drawn by the data loop below. The TR is added to the tree only once DrawRow has
        // populated it: a row entirely covered by a span from an earlier row draws no cell of its
        // own, and a TR with no /K and no /Pg is a struct elem describing nothing.
        foreach (var hi in headerRowIndices)
        {
            var trElem = tableElem is not null ? new PdfStructElem("TR") : null;
            DrawRow(ctx, rows[hi], hi, rowY, area.X, style, spanMap, trElem, drawnRows);
            if (trElem is not null && trElem.Children.Count > 0) tableElem!.AddChild(trElem);
            rowY += _rowHeights[hi];
        }

        // Then data rows. A row cut between its lines is drawn from its slice, and is the last row
        // drawn when the cut falls inside it, whatever the accumulated height says.
        for (var r = dataStartRow; r < rows.Count; r++)
        {
            if (rowY >= _occupied.Bottom - 0.001) break;
            var trElem = tableElem is not null ? new PdfStructElem("TR") : null;
            DrawRow(ctx, rows[r], r, rowY, area.X, style, spanMap, trElem, drawnRows);
            if (trElem is not null && trElem.Children.Count > 0) tableElem!.AddChild(trElem);
            rowY += HeightOf(r);
            if (_tailSlice?.Row == r) break;
        }

        // Register the complete Table struct elem tree with the document.
        if (tableElem is not null)
            ctx.RegisterStructElemTree(tableElem);
    }

    /// <summary>
    /// The slice of row <paramref name="r"/> this renderer draws, when the row is cut: the part it
    /// ends with when it is cut here, else the part it resumes with. Null for a row drawn whole.
    /// </summary>
    private RowSlice? SliceOf(int r) =>
        _tailSlice?.Row == r ? _tailSlice : _headSlice?.Row == r ? _headSlice : null;

    /// <summary>The height row <paramref name="r"/> takes on this page.</summary>
    private double HeightOf(int r) => SliceOf(r)?.Height ?? _rowHeights[r];

    private void DrawRow(DrawContext ctx, Row row, int rowIdx, double rowY, double startX,
        TextStyle style,
        Dictionary<(int row, int col), (Cell cell, Row originRow, double startY, int remainingRows)> spanMap,
        PdfStructElem? trElem, HashSet<int> drawnRows)
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
                // Last row of a multi-row span. The cell was already drawn once, at the origin
                // row, spanning the full combined height (see the RowSpan > 1 branch below) — this
                // only clears the slots it occupied in this row so the column can resume ordinary
                // cells on the next row.
                if (span.remainingRows == 1)
                {
                    for (var sc = col; sc < col + span.cell.ColSpan; sc++)
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

            // The rows and columns this cell actually covers, which is what both the paint and the
            // structure attribute are taken from. A declared span is a request: ColSpanWidth and the
            // combined-height loop already stopped at what exists, while the attribute was written
            // from the declared number, so a cell could claim rows and columns that are not there.
            //
            // The column bound is not simply the grid's width. TableGridResolver sets the column
            // count to the widest row's own span sum (#485), so a row whose cells are all its own
            // can never overrun it. But col is not always the running total of this row's cells: a
            // span from an earlier row advances it by that cell's ColSpan instead. A row of one
            // ColSpan-2 cell, sitting under a row that sums to three columns and spans its first
            // two down into this one, starts at column 2 of 3 and asks for two. It gets one.
            var rowSpan = EffectiveRowSpan(cell.RowSpan, rowIdx, drawnRows);
            var colSpan = Math.Min(cell.ColSpan, _colWidths.Length - col);
            var colW = ColSpanWidth(col, colSpan);
            var slice = SliceOf(rowIdx);
            var h = slice?.Height ?? _rowHeights[rowIdx];

            if (rowSpan <= 1)
            {
                // Normal single-row cell — draw immediately
                DrawCell(ctx, cell, row, col, colXPositions[col], rowY, colW, h, style, trElem,
                    rowSpan, colSpan, rowIdx, cellIdx - 1, slice);
            }
            else
            {
                // Multi-row span: drawn once here, spanning the combined height of the rows it
                // covers, with the rows below marked occupied so they skip this column range.
                var totalSpanH = 0.0;
                for (var sr = rowIdx; sr < rowIdx + rowSpan; sr++)
                    totalSpanH += HeightOf(sr);

                DrawCell(ctx, cell, row, col, colXPositions[col], rowY, colW, totalSpanH, style,
                    trElem, rowSpan, colSpan, rowIdx, cellIdx - 1, null);

                for (var sr = rowIdx + 1; sr < rowIdx + rowSpan; sr++)
                    for (var sc = col; sc < col + colSpan; sc++)
                        spanMap[(sr, sc)] = (cell, row, rowY, rowSpan - (sr - rowIdx));
            }

            col += colSpan;
        }
    }

    /// <summary>
    /// How many rows a cell at <paramref name="rowIdx"/> declaring <paramref name="declared"/> rows
    /// actually covers on this page: the longest run of consecutive rows from its own, all of which
    /// this page draws, capped by the declared count.
    ///
    /// A page break does not clip a span that starts in a data row and whose last row does not
    /// overflow int, because <c>Layout</c> walks the break back out of such a group; a group taller
    /// than the page leaves below the repeated header rows raises <c>ElementTooTall</c> instead of
    /// splitting. A span from the leading header rows is not walked back out of, so it is clipped
    /// on each continuation page (see <c>Cell.RowSpan</c>). What this also clips, on a data row as
    /// readily as a header row, is a span reaching past the last row of the table. Measured on a
    /// 300x170pt page at 20pt margins with a repeated header, where the box holds five 20pt rows
    /// under it: groups of two to five keep their declared span, six and up raise.
    ///
    /// The repeated header run is the case this exists for. <c>Draw</c> draws that run again at the
    /// top of every continuation page, but
    /// the row below it there is the split row rather than row 1, so a header cell declaring
    /// <c>RowSpan = 2</c> covers nothing the page puts under it. Painting it two rows tall overlaps
    /// the first data row's own rectangle, and tagging it as two rows made the header declare one
    /// column fewer than the row below it, which fails ISO 14289-1:2014 clause 7.5 through the
    /// column-count check veraPDF applies to each row of a table.
    /// </summary>
    private static int EffectiveRowSpan(int declared, int rowIdx, HashSet<int> drawnRows)
    {
        var span = 1;
        while (span < declared && drawnRows.Contains(rowIdx + span))
            span++;
        return span;
    }

    private void DrawCell(DrawContext ctx, Cell cell, Row row, int colIdx,
        double cellX, double cellY, double colW, double h,
        TextStyle style, PdfStructElem? trElem, int rowSpan, int colSpan,
        int rowIndex, int cellIndex, RowSlice? slice)
    {
        var cs = cell.Style ?? style;

        // Background fill (decorative — wrapped as /Artifact when tagged)
        var bg = cell.Background ?? row.Background;
        if (bg.HasValue)
        {
            var (bx, by, bw, bh) = ctx.ToPdfRect(new LayoutBox(cellX, cellY, colW, h));
            if (ctx.Tagged) ctx.Canvas.BeginArtifactMarkedContent();
            ctx.Canvas
                .SetFillColorRgb(bg.Value.R, bg.Value.G, bg.Value.B)
                .Rectangle(bx, by, bw, bh)
                .Fill();
            if (ctx.Tagged) ctx.Canvas.EndMarkedContent();
        }

        // Where the border is written, so a table that is never drawn is not refused for a negative
        // width. The finite check is in Layout.
        LayoutValidation.ValidateLineWidth(_table.BorderWidth, "A table");

        // Cell border (decorative — wrapped as /Artifact when tagged)
        var (rx, ry, rw, rh) = ctx.ToPdfRect(new LayoutBox(cellX, cellY, colW, h));
        if (ctx.Tagged) ctx.Canvas.BeginArtifactMarkedContent();
        ctx.Canvas
            .SetStrokeColorRgb(_table.BorderColor.R, _table.BorderColor.G, _table.BorderColor.B)
            .SetLineWidth(_table.BorderWidth)
            .Rectangle(rx, ry, rw, rh)
            .Stroke();
        if (ctx.Tagged) ctx.Canvas.EndMarkedContent();

        // Cell text — word-wrapped to the cell inner width so draw matches measurement.
        var innerBox = new LayoutBox(
            cellX + cell.Padding.Left, cellY + cell.Padding.Top,
            colW - cell.Padding.Horizontal, h - cell.Padding.Vertical);

        // A cell of a cut row draws the lines this page was given of the ones the row was wrapped
        // into when it was cut, so the page break falls where the cut put it. Every other cell
        // wraps here.
        List<string> wrappedLines;
        var firstLine = 0;
        int lineCount;
        if (slice is not null)
        {
            wrappedLines = slice.Source.Cells[cellIndex] ?? [];
            firstLine = slice.First[cellIndex];
            lineCount = slice.Count[cellIndex];
        }
        else
        {
            wrappedLines = WordWrapLines(cell.Content, cs, Math.Max(1, innerBox.Width), HonoursBreaks(rowIndex, cellIndex));
            lineCount = wrappedLines.Count;
        }

        // A cell the table laid out unbroken is reported where it is first drawn: once per page for
        // a repeated header row, and from its first slice for a cut row. Only a cell that holds a
        // control character loses anything by it, which is the separation its words had.
        if (!HonoursBreaks(rowIndex, cellIndex) && LayoutText.HasControlSpace(cell.Content)
            && (slice is null || slice.First[cellIndex] == 0))
        {
            ctx.Diagnostics?.Report(
                LayoutDiagnosticCode.CellWhiteSpaceDropped,
                ((long)rowIndex << 32) | (uint)cellIndex,
                _readsUnbrokenForTheLimit ? DroppedForTheLimit : DroppedForRoom,
                perElement: true);
        }

        // A cell with no line on this page keeps its rectangle and writes no text, and is tagged as
        // an empty cell: a marked-content sequence around nothing would describe nothing.
        if (lineCount == 0)
        {
            if (ctx.Tagged)
                trElem?.AddChild(NewCellElement(cell, row, rowSpan, colSpan, null));
            return;
        }

        // Tagged PDF: TH (header) or TD (data), containing a P with the MCID.
        // Structure: TR → TH/TD → P
        int mcid = -1;
        if (ctx.Tagged)
            mcid = ctx.Canvas.BeginMarkedContent("P");

        var canvas = ctx.Canvas.BeginText();

        // A fresh reference, so the font operator is written for every cell.
        TextEmitter.SelectedFont? cellFont = null;
        TextEmitter.SelectFont(ctx, canvas, cs, ref cellFont);

        // Where the text colour is written, so a cell that is never drawn is not refused for it.
        // The font size is checked in Layout.
        LayoutValidation.ValidateColor(cs.Color, $"Table row {rowIndex}, cell {cellIndex}", "text colour");
        canvas.SetFillColorRgb(cs.Color.R, cs.Color.G, cs.Color.B);

        var lineHeight = cs.EffectiveLeading;
        for (var lineIdx = 0; lineIdx < lineCount; lineIdx++)
        {
            var line = wrappedLines[firstLine + lineIdx];
            var lineW = cs.FontRef.MeasureString(line, cs.FontSize);

            // Floored at the cell's own left edge (#473), the same clamp #472 applied to a
            // paragraph line for the same reason. Unlike the paragraph, this does not transfer as
            // "the only way": WordWrapLines is called against Math.Max(1, innerBox.Width) above,
            // while lineW is compared here against innerBox.Width itself, so the two only agree
            // when the cell's own inner width is already at least 1pt. Every line WordWrapLines
            // emits stays within that max(1pt, innerBox.Width) bound, but two kinds of line can
            // still exceed innerBox.Width proper: a single over-wide rune that cannot be broken any
            // further, the case the paragraph shares, and any line drawn in a cell whose inner
            // width has fallen below 1pt — an empty line included, since MeasureString("") is 0 and
            // 0 > innerBox.Width once the latter goes negative. The floor moves nothing for a line
            // that already fits and keeps the one thing it can for both residues: the line's origin
            // stays inside the cell instead of starting left of it.
            double txOffset = cell.Alignment switch
            {
                HorizontalAlignment.Center => Math.Max(0, (innerBox.Width - lineW) / 2),
                HorizontalAlignment.Right => Math.Max(0, innerBox.Width - lineW),
                _ => 0
            };

            var pdfTextY = ctx.ToPdfY(innerBox.Y + cs.FontSize + lineIdx * lineHeight);
            canvas.SetTextMatrix(1, 0, 0, 1, innerBox.X + txOffset, pdfTextY);

            TextEmitter.Show(canvas, cs, line);
        }

        canvas.EndText();

        if (ctx.Tagged && mcid >= 0)
        {
            ctx.Canvas.EndMarkedContent();

            // Build: TR → TH/TD → P
            var pElem = new PdfStructElem("P") { Mcid = mcid };
            ctx.StampStructElemPage(pElem);
            trElem?.AddChild(NewCellElement(cell, row, rowSpan, colSpan, pElem));
        }
    }

    /// <summary>
    /// The TH or TD for a cell, with its attributes and the paragraph it holds. A cell that holds
    /// nothing is always a TD, never a TH: the table checklist in the Tagged PDF Best Practice
    /// Guide (4.2.6.2) says "empty cells are always &lt;TD&gt; cells, never &lt;TH&gt; cells".
    /// Only a slice with no line left passes no paragraph.
    /// </summary>
    private static PdfStructElem NewCellElement(Cell cell, Row row, int rowSpan, int colSpan, PdfStructElem? paragraph)
    {
        var header = row.IsHeader && paragraph is not null;
        var cellElem = new PdfStructElem(header ? "TH" : "TD");
        if (cell.Language is not null)
            cellElem.Language = cell.Language;
        if (header)
            cellElem.TableHeaderScope = "Column";
        if (rowSpan > 1)
            cellElem.TableRowSpan = rowSpan;
        if (colSpan > 1)
            cellElem.TableColSpan = colSpan;
        if (paragraph is not null)
            cellElem.AddChild(paragraph);
        return cellElem;
    }

    /// <summary>
    /// Returns the indices of the repeatable header: the leading contiguous run of header rows,
    /// when at least one row that is not a header follows it, and an empty list otherwise. The two
    /// loops in <see cref="Draw"/> partition the rows whenever this list is a prefix of them, and
    /// the leading contiguous run is the longest prefix that holds header rows alone; a header row
    /// elsewhere in the table draws as an ordinary row instead of extending it.
    ///
    /// A run that covers every row has no data below it to repeat over. Returning it would set
    /// <c>dataStartRow</c> to the row count, so <see cref="Layout"/> would never enter its fit loop
    /// and would give up on every area, however large. An empty list makes the rows the table's
    /// body: each is laid out and drawn once, in order, and the table splits between rows like any
    /// other. Both <see cref="Layout"/> and <see cref="Draw"/> read this list, so they agree on
    /// which case a table is in.
    ///
    /// The list stops at the first row that is not a header. Collecting every header row wherever
    /// it sits would set <c>dataStartRow</c>, taken from the list's last index, past the *last*
    /// header while the header loop drew only the rows the list held, so a data row between two
    /// headers would be reached by neither loop: header H0, data D1, header H2, data D3 would draw
    /// H0, H2 and D3 and never D1.
    /// </summary>
    private static List<int> FindHeaderRowIndices(IReadOnlyList<Row> rows)
    {
        var indices = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].IsHeader) return indices;
            indices.Add(i);
        }
        // Every row is a header: nothing follows the run, so there is no header to repeat.
        return [];
    }

    private double ColSpanWidth(int startCol, int span)
    {
        var w = 0.0;
        for (var i = 0; i < span && startCol + i < _colWidths.Length; i++)
            w += _colWidths[startCol + i];
        return w;
    }

    /// <summary>
    /// Delegates to <see cref="WordWrapLines"/> rather than carrying its own line-counting walk.
    /// The two used to be separate algorithms, and they did not merely risk drifting apart — they
    /// already disagreed. The old walk counted a line for every word it moved to a fresh line,
    /// including a trailing empty token from a cell string ending in a space that does not fit,
    /// which <see cref="WordWrapLines"/>'s own trailing <c>if (lineBuilder.Length &gt; 0)</c> never
    /// turns into a drawn line. The disagreement is one-sided: across random sweeps of cell
    /// strings in a 40pt column at Helvetica 12, comparing the old walk's count against the old
    /// <c>WordWrapLines</c>'s own <c>.Count</c>, every case found had the walk counting a taller
    /// row than the lines actually drawn and none a shorter one. The count itself belongs to the
    /// sample and not to the defect, so it is not quoted: two sweeps over different alphabets gave
    /// 152 and 744 in 4,000. The known-answer case does not move: <c>"AAAAii "</c> in the same column
    /// counted 2 lines (a 28.8pt row) while only 1 was drawn; delegating resolves the row to the
    /// 14.4pt height the single drawn line actually needs. <c>Layout</c>'s row height (built from
    /// this count) has to match what <c>Draw</c> actually emits or a cell's text overruns the row
    /// it was measured against, so this also silently shortens the row for any document whose cell
    /// text ends in a space that does not fit — the corrected height, not a side effect, but one
    /// the CHANGELOG has to carry since it moves geometry a caller could depend on. One walk also
    /// removes the chance of separate algorithms drifting further apart than this, which is what
    /// let the hard-break below (#473) reach both call sites from a single change instead of two
    /// that would otherwise have to be kept in step by hand.
    /// </summary>
    internal static int WordWrapCount(string text, TextStyle style, double maxWidth, bool honourBreaks) =>
        WordWrapLines(text, style, maxWidth, honourBreaks).Count;

    /// <summary>
    /// Word-wraps text into lines for drawing. A word wider than <paramref name="maxWidth"/> on
    /// its own — the "start of a fresh line" arms below, reached both for the text's first word and
    /// for the word starting a fresh line after a wrap — used to be emitted whole regardless, which
    /// is #473: the line then drew past the cell's own right edge, and past the page once the
    /// column was narrow enough. <see cref="HardBreakWord"/> now breaks it at character granularity
    /// instead, the same behaviour <c>ParagraphRenderer.HardBreakWord</c> already has for a
    /// paragraph line — kept as its own copy rather than shared, since the two callers differ in
    /// what they know about their own box and a shared version would be a third code path to
    /// review for this pull request alone.
    ///
    /// "Start of a fresh line" is tested on <c>lineBuilder.Length == 0</c>, not on <c>lineW == 0</c>
    /// as an earlier version of this method did: <c>lineW</c> is a measured advance, not an
    /// emptiness flag, and a character whose advance is zero — 38 of the first 256 codes in the
    /// Helvetica and Times faces, 32 in Courier, and every one of the 256 in Symbol and
    /// ZapfDingbats, which is the metrics gap #470 is about — leaves the buffer holding a
    /// pending, un-flushed line while <c>lineW</c> still reads 0. Testing <c>lineW</c> there made
    /// the next over-wide word believe it was starting fresh too, so it pushed its own hard-break
    /// fragments into <paramref name="maxWidth"/> ahead of the pending line, which was flushed only
    /// at the end of the loop — the zero-advance token then drew last, after content that followed
    /// it in the source. <c>ParagraphRenderer</c> already guards on <c>lineFrags.Count == 0</c> for
    /// the same reason, which is why it never had this.
    /// </summary>
    private static List<string> WrapLine(string text, TextStyle style, double maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) { result.Add(string.Empty); return result; }

        var words = text.Split(' ');
        var spaceW = style.FontRef.MeasureString(" ", style.FontSize);
        var lineBuilder = new System.Text.StringBuilder();
        var lineW = 0.0;

        foreach (var word in words)
        {
            var ww = style.FontRef.MeasureString(word, style.FontSize);
            if (lineBuilder.Length == 0)
            {
                if (ww > maxWidth)
                {
                    HardBreakWord(word, style, maxWidth, result);
                    continue;
                }
                lineBuilder.Append(word);
                lineW = ww;
            }
            else if (lineW + spaceW + ww <= maxWidth)
            {
                lineBuilder.Append(' ');
                lineBuilder.Append(word);
                lineW += spaceW + ww;
            }
            else
            {
                result.Add(lineBuilder.ToString());
                lineBuilder.Clear();
                lineW = 0.0;

                if (ww > maxWidth)
                {
                    HardBreakWord(word, style, maxWidth, result);
                    continue;
                }
                lineBuilder.Append(word);
                lineW = ww;
            }
        }
        if (lineBuilder.Length > 0)
            result.Add(lineBuilder.ToString());
        if (result.Count == 0)
            result.Add(string.Empty);
        return result;
    }

    /// <summary>
    /// The lines a cell's text is drawn in: each hard line of the text, wrapped by
    /// <see cref="WrapLine"/>. A hard line ends at a carriage return and line feed pair, a lone
    /// carriage return or a lone line feed. An empty hard line is a blank line, and so is the one
    /// after a trailing break, which a paragraph drops. Runs of spaces are kept as written, where a
    /// paragraph collapses them. Every other white-space control character is a space; the no-break
    /// spaces and the other space separators are written as they are. A null text, and text with
    /// nothing in it, is one empty line. With <paramref name="honourBreaks"/> false the text is
    /// wrapped at spaces alone, measuring each control white-space character at its own width, and
    /// the lines are drawn without them, so a line is never wider than it was measured.
    /// </summary>
    internal static List<string> WordWrapLines(string text, TextStyle style, double maxWidth, bool honourBreaks)
    {
        if (string.IsNullOrEmpty(text)) return WrapLine(string.Empty, style, maxWidth);

        var hardLines = CellLines(text, honourBreaks);
        if (!honourBreaks)
        {
            var wrapped = WrapLine(hardLines[0], style, maxWidth);
            for (var i = 0; i < wrapped.Count; i++)
                wrapped[i] = LayoutText.RemoveControlSpaces(wrapped[i]);
            return wrapped;
        }

        if (hardLines.Length == 1) return WrapLine(hardLines[0], style, maxWidth);

        var result = new List<string>();
        foreach (var line in hardLines)
            result.AddRange(WrapLine(line, style, maxWidth));
        return result;
    }

    /// <summary>
    /// The lines a cell's text is measured in. When <paramref name="honourBreaks"/> is true, the
    /// hard lines of the text with their control white space turned into spaces. Otherwise the text
    /// itself as the one line it is measured as, control characters and all.
    /// </summary>
    internal static string[] CellLines(string text, bool honourBreaks)
    {
        if (!honourBreaks) return [text];

        var lines = LayoutText.SplitHardLines(text);
        for (var i = 0; i < lines.Length; i++)
            lines[i] = LayoutText.ControlSpacesToSpaces(lines[i]);
        return lines;
    }

    /// <summary>Hard-breaks a word wider than <paramref name="maxWidth"/> at character granularity.</summary>
    private static void HardBreakWord(string word, TextStyle style, double maxWidth, List<string> lines)
    {
        var fragment = new System.Text.StringBuilder();
        var fragmentW = 0.0;

        foreach (var rune in word.EnumerateRunes())
        {
            var ch = rune.ToString();
            var charW = style.FontRef.MeasureString(ch, style.FontSize);
            if (fragment.Length > 0 && fragmentW + charW > maxWidth)
            {
                lines.Add(fragment.ToString());
                fragment.Clear();
                fragmentW = 0.0;
            }
            fragment.Append(ch);
            fragmentW += charW;
        }
        if (fragment.Length > 0)
            lines.Add(fragment.ToString());
    }

    /// <summary>
    /// Builds a set of row indices after which we must NOT split the table because
    /// a rowspan cell starting at or before that row continues into the next row.
    /// </summary>
    private static HashSet<int> BuildRowspanBlockedSet(IReadOnlyList<Row> rows, int firstDataRow)
    {
        var blocked = new HashSet<int>();
        for (var r = firstDataRow; r < rows.Count; r++)
        {
            foreach (var cell in rows[r].Cells)
            {
                if (cell.RowSpan > 1)
                {
                    // All rows from r through r+rowSpan-2 are blocked (can't split after them).
                    for (var span = r; span < r + cell.RowSpan - 1 && span < rows.Count - 1; span++)
                        blocked.Add(span);
                }
            }
        }
        return blocked;
    }
}
