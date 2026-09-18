// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>A table row containing one or more cells.</summary>
/// <remarks>
/// A table creates its rows through <see cref="TableElement.AddRow"/> and
/// <see cref="TableElement.AddHeaderRow"/>. No member of <see cref="TableElement"/> accepts a row
/// built with the constructor.
/// </remarks>
public sealed class Row
{
    /// <summary>Creates an empty row.</summary>
    /// <remarks>
    /// A row built here cannot join a table, because no member of <see cref="TableElement"/>
    /// accepts one. Use <see cref="TableElement.AddRow"/>.
    /// </remarks>
    public Row() { }

    private readonly List<Cell> _cells = [];

    /// <summary>The cells in this row, in column order.</summary>
    /// <remarks>
    /// An empty row among rows that have cells is not refused. A table in which no row has a cell
    /// is; see <see cref="TableElement.Rows"/>.
    /// </remarks>
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>Whether this row is a header row (may be repeated on each page).</summary>
    /// <remarks>
    /// Only the leading run of header rows repeats on each continuation page. A header row after a
    /// data row is drawn once, where it is. A table whose rows are all headers cannot be drawn: it
    /// lays out as nothing, so the save throws the too-tall <see cref="InvalidOperationException"/>
    /// even when the table would fit (#488).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when every row of the table is a header row. The message says the
    /// element is too tall to fit on a page (#488).
    /// </exception>
    public bool IsHeader { get; init; }

    /// <summary>Optional background fill color for the row.</summary>
    /// <remarks>
    /// <b>Attention</b>: no row in a table can have a background. Rows join a table only through
    /// <see cref="TableElement.AddRow"/> and <see cref="TableElement.AddHeaderRow"/>, which create
    /// them without one, and this property can only be set when the row is constructed. Set
    /// <see cref="Cell.Background"/> on each cell instead (#543).
    /// </remarks>
    public ColorRgb? Background { get; init; }

    /// <summary>Adds a cell to the row. Returns this row.</summary>
    /// <remarks>
    /// A null <paramref name="cell"/> is stored, and the save throws when it lays out the table.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="cell"/> is <see langword="null"/>.
    /// </exception>
    public Row AddCell(Cell cell) { _cells.Add(cell); return this; }

    /// <summary>Adds a text cell to the row. Returns this row.</summary>
    /// <remarks>
    /// Creates the cell with <see cref="Cell(string)"/> and adds it, so a line break in the text is
    /// not honoured; see <see cref="Cell.Content"/>. A null <paramref name="text"/> is stored. The
    /// save throws when it sizes a column automatically, which it does for any column without an
    /// explicit width; with every column width set, the cell is drawn empty.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="text"/> is <see langword="null"/> and the table has a
    /// column without an explicit width.
    /// </exception>
    public Row AddCell(string text) => AddCell(new Cell(text));
}
