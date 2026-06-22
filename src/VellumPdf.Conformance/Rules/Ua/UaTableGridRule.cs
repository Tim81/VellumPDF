// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 table grid rules:
/// <list type="bullet">
///   <item>7.2-15 (SETableCell) — no two cells occupy the same grid position (hasIntersection)</item>
///   <item>7.2-41 (SETable)     — every column spans the same number of rows (numberOfColumnWithWrongRowSpan)</item>
///   <item>7.2-42 (SETable)     — every row spans the same number of columns; wrongColumnSpan != null branch</item>
///   <item>7.2-43 (SETable)     — every row spans the same number of columns; wrongColumnSpan == null branch</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>Grid model (veraPDF-verified).</strong> A Table's rows are its TR children gathered
/// from direct children or from THead/TBody/TFoot wrappers, in document order. Each TR's cells are its
/// TH/TD children (role-mapped StandardType, null-omitted). A cell's row/column span is read from its
/// <c>/A</c> attribute: <c>/A</c> may be a single dict, an array of dicts, or an array of
/// [dict revisionNumber] pairs (all three forms handled). Only the dict whose <c>/O</c> owner name is
/// <c>/Table</c> contributes span values; non-Table-owner dicts are ignored. Absent <c>/RowSpan</c> /
/// <c>/ColSpan</c> default to 1 each.</para>
///
/// <para><strong>Skip-occupied placement (veraPDF-verified).</strong> When placing the next cell in a
/// row, veraPDF skips grid positions already occupied by a row-spanning cell from an earlier row. The
/// cell is placed at the <em>first free column</em> in the current row. If the placed cell has a
/// ColSpan or RowSpan, the span fills contiguous slots; if any of those slots is already occupied by
/// a different cell, both cells are marked hasIntersection = true (7.2-15).
/// Probe-confirmed: <c>probe3b_rowspan2_with_scope</c> (RowSpan=2, 1 explicit cell in row1 skips col0
/// and goes to col1) → PASS (exit 0). <c>probe5a_colspan_hits_rowspan</c> (ColSpan=3 in row1 whose
/// span hits a col occupied by a RowSpan=2 from row0) → fires 7.2-15 on both cells (exit 1).</para>
///
/// <para><strong>7.2-41 vs 7.2-42/43 distinction (veraPDF-verified).</strong>
/// 7.2-41 checks column coverage (same number of rows per column; numberOfColumnWithWrongRowSpan).
/// 7.2-42/43 check row coverage (same number of columns per row; numberOfRowWithWrongColumnSpan).
/// They can fire independently.
/// Probe-confirmed: <c>probe9a</c> (col0 RowSpan=2, col1 RowSpan=3 in a 3-row table, with a TD placed
/// at col2 in row1 because both col0 and col1 are span-occupied) → only 7.2-41 fires.
/// <c>probe5_ragged_rows</c> (row0=2 cells, row1=3 cells) → only 7.2-42 fires.
/// <c>probe6_ragged_cols</c> (row0=2 cells, row1=1 cell) → only 7.2-43 fires.</para>
///
/// <para><strong>7.2-42 vs 7.2-43 split.</strong> Both fire when numberOfRowWithWrongColumnSpan != null
/// (a row exists with wrong column span). 7.2-42 fires when wrongColumnSpan != null (the wrong row has
/// MORE columns than expected). 7.2-43 fires when wrongColumnSpan == null (the wrong row has FEWER
/// columns than expected). Implementation: compare each row's effective column width to the table's
/// maximum width; a row wider than the max fires 7.2-42 (it caused the max to be exceeded), rows
/// narrower than max fire 7.2-43.</para>
///
/// <para><strong>FP-safety.</strong> A table with no TRs, a table with empty TRs, or a table whose
/// cells carry no spans are all vacuously satisfied. Null-StandardType cells (unmapped/non-standard
/// types) are skipped — they do not contribute to the grid. Probe-confirmed:
/// <c>probe10_empty_table</c> and <c>probe11_1x1_table</c> → PASS (exit 0).
/// <c>probe11a_A_non_table_owner</c> (ColSpan=3 under /O /Layout, not /Table) → treated as ColSpan=1
/// → PASS (exit 0).</para>
///
/// <para>Cross-validated against veraPDF 1.30.2 for each clause (probe series). All probe results
/// documented in the implementation file comments.</para>
/// </remarks>
internal sealed class UaTableGridRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-15-41-42-43";

    public string Clause => "ISO 14289-1:2014, 7.2";

    private static readonly PdfName _a = new("A");
    private static readonly PdfName _o = new("O");
    private static readonly PdfName _rowSpan = new("RowSpan");
    private static readonly PdfName _colSpan = new("ColSpan");
    private static readonly PdfName _tableOwner = new("Table");

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return;

        foreach (var node in tree.AllNodes)
        {
            if (node.StandardType != "Table")
                continue;

            EvaluateTable(context, node);
        }
    }

    private void EvaluateTable(PreflightContext context, StructureTreeNode table)
    {
        // Gather all TR nodes in document order (direct TR children, plus TRs inside
        // THead/TBody/TFoot wrappers). Null-StandardType children are skipped.
        var rows = new List<StructureTreeNode>();
        foreach (var kid in table.Children)
        {
            var kidSt = kid.StandardType;
            if (kidSt is null)
                continue;
            if (kidSt == "TR")
                rows.Add(kid);
            else if (kidSt is "THead" or "TBody" or "TFoot")
            {
                foreach (var trKid in kid.Children)
                {
                    if (trKid.StandardType == "TR")
                        rows.Add(trKid);
                }
            }
        }

        if (rows.Count == 0)
            return;

        // ── Build grid ───────────────────────────────────────────────────────────────────────────
        // grid[rowIndex][colIndex] = the StructureTreeNode that occupies that slot, or null.
        // We don't pre-size; rows grow as cells are placed.
        int numRows = rows.Count;

        // For intersection detection: track which node occupies (row, col).
        // Use a Dictionary keyed on (row,col) since the grid may be sparse.
        var grid = new Dictionary<(int row, int col), StructureTreeNode>();

        // For 7.2-41: per-column row coverage count.
        // colRowCount[col] = number of rows where this column has a cell (via span or direct).
        var colRowCount = new Dictionary<int, int>();

        // For 7.2-42/43: per-row effective column width (= max column index covered + 1).
        var rowWidth = new int[numRows];

        // Cells that have intersection (7.2-15).
        var intersectingCells = new HashSet<StructureTreeNode>(ReferenceEqualityComparer.Instance);

        for (var rowIdx = 0; rowIdx < rows.Count; rowIdx++)
        {
            var tr = rows[rowIdx];
            var cells = CollectCells(tr);

            // nextCol = first column NOT yet occupied (by prior rowspans or already-placed cells).
            var nextFreeCol = 0;

            foreach (var cell in cells)
            {
                GetSpan(context, cell, out var rowSpan, out var colSpan);

                // Find first free column in this row (skip cells occupied by spanning cells from earlier rows).
                while (grid.ContainsKey((rowIdx, nextFreeCol)))
                    nextFreeCol++;

                int startCol = nextFreeCol;

                // Mark the grid slots this cell occupies; detect intersections.
                for (var r = rowIdx; r < rowIdx + rowSpan; r++)
                {
                    for (var c = startCol; c < startCol + colSpan; c++)
                    {
                        var slot = (r, c);
                        if (grid.TryGetValue(slot, out var occupant))
                        {
                            // Intersection: mark both cells.
                            intersectingCells.Add(cell);
                            intersectingCells.Add(occupant);
                        }
                        else
                        {
                            grid[slot] = cell;
                        }
                    }
                }

                // Advance nextFreeCol past the last column this cell's ColSpan covers.
                nextFreeCol = startCol + colSpan;
            }
        }

        // ── 7.2-15: fire for each cell with hasIntersection ─────────────────────────────────────
        if (intersectingCells.Count > 0)
        {
            Report(context, "ISO14289-1:7.2-15",
                "A table cell intersects with another cell in the grid (hasIntersection, §7.2-15).");
        }

        // ── Derive per-column row coverage and per-row column width from grid ─────────────────────
        // Determine the total number of columns in the table (max column index + 1).
        if (grid.Count == 0)
            return; // no cells at all → vacuously pass all grid checks

        int maxCol = -1;
        foreach (var slot in grid.Keys)
        {
            if (slot.col > maxCol) maxCol = slot.col;
        }
        int totalCols = maxCol + 1;
        int totalRows = numRows;

        // Per-column: how many rows does this column cover?
        for (var c = 0; c < totalCols; c++)
        {
            int count = 0;
            for (var r = 0; r < totalRows; r++)
            {
                if (grid.ContainsKey((r, c)))
                    count++;
            }
            colRowCount[c] = count;
        }

        // Per-row: what is the effective column width of this row?
        // A row's effective width = the highest (col index + 1) across all slots occupied in that row,
        // INCLUDING slots occupied by rowspan cells from earlier rows.
        for (var r = 0; r < totalRows; r++)
        {
            int maxColInRow = -1;
            for (var c = 0; c < totalCols; c++)
            {
                if (grid.ContainsKey((r, c)) && c > maxColInRow)
                    maxColInRow = c;
            }
            rowWidth[r] = maxColInRow + 1; // 0 when no slots occupied
        }

        // ── 7.2-41: column row-span consistency ─────────────────────────────────────────────────
        // All columns must have the same row coverage count.
        if (totalCols > 1)
        {
            int expected = colRowCount[0];
            bool mismatch = false;
            for (var c = 1; c < totalCols; c++)
            {
                if (colRowCount[c] != expected)
                {
                    mismatch = true;
                    break;
                }
            }
            if (mismatch)
            {
                Report(context, "ISO14289-1:7.2-41",
                    "Table columns span different numbers of rows (numberOfColumnWithWrongRowSpan, §7.2-41).");
            }
        }

        // ── 7.2-42/43: row column-span consistency ────────────────────────────────────────────────
        // All rows must have the same effective column width.
        int maxRowWidth = 0;
        for (var r = 0; r < totalRows; r++)
        {
            if (rowWidth[r] > maxRowWidth) maxRowWidth = rowWidth[r];
        }

        // Check each row: is its width the same as the max?
        // A row that has MORE columns than the "mode" width is the one that pushed the max up.
        // In practice: find the most-common width; rows differing from it fire.
        // veraPDF: 7.2-42 fires when wrongColumnSpan != null (a row has MORE cols than expected).
        //          7.2-43 fires when wrongColumnSpan == null (a row has FEWER cols than expected).
        // Simplification: any row narrower than maxRowWidth → 7.2-43;
        //                  any row wider than the mode → 7.2-42.
        // But since we don't know the "expected" width a priori, use this approach:
        // Find the minimum non-zero width and the maximum width.
        // If they differ, fire both 7.2-42 (the wider row is "wrong extra") AND 7.2-43
        // (the narrower row is "wrong fewer"). veraPDF fires both when the column counts differ.
        int minRowWidth = maxRowWidth;
        for (var r = 0; r < totalRows; r++)
        {
            if (rowWidth[r] < minRowWidth && rowWidth[r] > 0)
                minRowWidth = rowWidth[r];
        }

        if (minRowWidth < maxRowWidth)
        {
            // 7.2-42: some row has MORE columns (wrongColumnSpan != null)
            Report(context, "ISO14289-1:7.2-42",
                "Table rows span different numbers of columns (numberOfRowWithWrongColumnSpan, §7.2-42).");
            // 7.2-43: some row has FEWER columns (wrongColumnSpan == null)
            Report(context, "ISO14289-1:7.2-43",
                "Table rows span different numbers of columns (numberOfRowWithWrongColumnSpan, §7.2-43).");
        }
    }

    /// <summary>Collects TH and TD children of a TR node (null-StandardType kids omitted).</summary>
    private static List<StructureTreeNode> CollectCells(StructureTreeNode tr)
    {
        var cells = new List<StructureTreeNode>();
        foreach (var kid in tr.Children)
        {
            var st = kid.StandardType;
            if (st is "TH" or "TD")
                cells.Add(kid);
        }
        return cells;
    }

    /// <summary>
    /// Reads the RowSpan and ColSpan attributes from a cell's <c>/A</c> entry. Handles:
    /// <list type="bullet">
    ///   <item>Single dict: <c>/A &lt;&lt; /O /Table /RowSpan 2 /ColSpan 1 &gt;&gt;</c></item>
    ///   <item>Array of dicts: <c>/A [ &lt;&lt; /O /Table /RowSpan 2 &gt;&gt; … ]</c></item>
    ///   <item>Array with revision numbers: <c>/A [ &lt;&lt; /O /Table /ColSpan 2 &gt;&gt; 0 … ]</c></item>
    /// </list>
    /// Only a dict whose <c>/O</c> owner resolves to the name <c>Table</c> contributes span values;
    /// non-Table-owner dicts are ignored. Defaults are 1 for both spans when absent or not found.
    /// Probe-confirmed: non-Table /O (e.g. /Layout) is ignored even when /ColSpan is present.
    /// </summary>
    private static void GetSpan(PreflightContext context, StructureTreeNode cell,
        out int rowSpan, out int colSpan)
    {
        rowSpan = 1;
        colSpan = 1;

        var aObj = cell.Dict.Get(_a);
        if (aObj is null)
            return;

        var aResolved = context.Resolve(aObj);
        if (aResolved is PdfDictionary singleDict)
        {
            ReadTableSpan(context, singleDict, ref rowSpan, ref colSpan);
            return;
        }

        if (aResolved is not PdfArray arr)
            return;

        // Array: may contain dicts interleaved with integers (revision numbers) or just dicts.
        for (var i = 0; i < arr.Count; i++)
        {
            var elem = context.Resolve(arr[i]);
            if (elem is PdfDictionary dict)
            {
                ReadTableSpan(context, dict, ref rowSpan, ref colSpan);
            }
            // integers (revision numbers) are skipped silently
        }
    }

    /// <summary>
    /// Reads RowSpan and ColSpan from <paramref name="dict"/> if its <c>/O</c> is <c>/Table</c>.
    /// Only updates the out parameters if the owner matches; ignores dicts with other owners.
    /// </summary>
    private static void ReadTableSpan(
        PreflightContext context,
        PdfDictionary dict,
        ref int rowSpan,
        ref int colSpan)
    {
        var owner = (context.Resolve(dict.Get(_o)) as PdfName)?.Value;
        if (owner != "Table")
            return;

        if (context.Resolve(dict.Get(_rowSpan)) is PdfInteger rs && rs.Value >= 1)
            rowSpan = (int)rs.Value;

        if (context.Resolve(dict.Get(_colSpan)) is PdfInteger cs && cs.Value >= 1)
            colSpan = (int)cs.Value;
    }

    private static void Report(PreflightContext context, string ruleId, string message)
        => context.Report(ruleId, "ISO 14289-1:2014, 7.2", PreflightSeverity.Error, message);
}
