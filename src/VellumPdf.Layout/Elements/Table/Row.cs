// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>A table row containing one or more cells.</summary>
public sealed class Row
{
    private readonly List<Cell> _cells = [];

    /// <summary>The cells in this row, in column order.</summary>
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>Whether this row is a header row (may be repeated on each page).</summary>
    public bool IsHeader { get; init; }

    /// <summary>Optional background fill color for the row.</summary>
    public ColorRgb? Background { get; init; }

    /// <summary>Adds a cell to the row. Returns this row.</summary>
    public Row AddCell(Cell cell) { _cells.Add(cell); return this; }

    /// <summary>Adds a text cell to the row. Returns this row.</summary>
    public Row AddCell(string text) => AddCell(new Cell(text));
}
