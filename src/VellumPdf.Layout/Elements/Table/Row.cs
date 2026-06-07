// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>A table row containing one or more cells.</summary>
public sealed class Row
{
    private readonly List<Cell> _cells = [];

    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>Whether this row is a header row (may be repeated on each page).</summary>
    public bool IsHeader { get; init; }

    public ColorRgb? Background { get; init; }

    public Row AddCell(Cell cell) { _cells.Add(cell); return this; }
    public Row AddCell(string text) => AddCell(new Cell(text));
}
