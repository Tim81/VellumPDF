// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>A table row containing one or more cells.</summary>
/// <remarks>
/// An empty row among filled rows is not refused. A table with no cells at all is; see
/// <see cref="TableElement.Rows"/>.
/// </remarks>
public sealed class Row
{
    /// <summary>Creates an empty row.</summary>
    /// <remarks>
    /// An empty row among filled rows is not refused. See <see cref="Cells"/>.
    /// </remarks>
    public Row() { }

    private readonly List<Cell> _cells = [];

    /// <summary>The cells in this row, in column order.</summary>
    /// <remarks>
    /// An empty row among rows that have cells is not refused. A table is refused only when
    /// no row contributes a cell; see <see cref="TableElement.Rows"/>.
    /// </remarks>
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>Whether this row is a header row (may be repeated on each page).</summary>
    /// <remarks>
    /// A table whose rows are all headers cannot paginate on a page with room to spare (#488).
    /// Save throws the too-tall <see cref="InvalidOperationException"/>. Only the leading
    /// contiguous run of header rows repeats on continuation pages.
    /// </remarks>
    public bool IsHeader { get; init; }

    /// <summary>Optional background fill color for the row.</summary>
    /// <remarks>Null means no fill. A colour is stored as given; see <see cref="ColorRgb"/>.</remarks>
    public ColorRgb? Background { get; init; }

    /// <summary>Adds a cell to the row. Returns this row.</summary>
    /// <remarks>A null <paramref name="cell"/> is stored. Layout then dereferences it.</remarks>
    public Row AddCell(Cell cell) { _cells.Add(cell); return this; }

    /// <summary>Adds a text cell to the row. Returns this row.</summary>
    /// <remarks>A null <paramref name="text"/> becomes a cell with null content.</remarks>
    public Row AddCell(string text) => AddCell(new Cell(text));
}
