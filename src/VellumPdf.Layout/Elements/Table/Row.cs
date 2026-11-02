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
    /// No member of <see cref="TableElement"/> accepts a row built here. Use
    /// <see cref="TableElement.AddRow"/>.
    /// </remarks>
    public Row() { }

    private readonly List<Cell> _cells = [];

    /// <summary>The cells in this row, in column order.</summary>
    /// <remarks>
    /// An empty row among rows that have cells is not refused. A table in which no row has a cell
    /// is; see <see cref="TableElement.Rows"/>. No cell is null: <see cref="AddCell(Cell)"/> refuses
    /// a null (#523).
    /// </remarks>
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>Whether this row is a header row (may be repeated on each page).</summary>
    /// <remarks>
    /// Only the leading run of header rows repeats on each continuation page. A header row after a
    /// data row is drawn once, where it is. A table whose rows are all headers cannot be drawn: it
    /// lays out as nothing, so the save throws <see cref="InvalidOperationException"/> even when
    /// the table would fit (#488).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when every row of the table is a header row. The message says the
    /// element is too tall to fit on a page (#488), or, when no header row has a cell, that the
    /// table resolved to no columns.
    /// </exception>
    public bool IsHeader { get; init; }

    /// <summary>Optional background fill color for the row.</summary>
    /// <remarks>
    /// <b>Attention</b>: a row added through <see cref="TableElement.AddRow"/> or
    /// <see cref="TableElement.AddHeaderRow"/>, the only members that add a row, has no background,
    /// and this property can only be set when a row is constructed. Set
    /// <see cref="Cell.Background"/> on each cell instead (#543).
    /// </remarks>
    public ColorRgb? Background { get; init; }

    /// <summary>Adds a cell to the row. Returns this row.</summary>
    /// <remarks>
    /// A null <paramref name="cell"/> is refused here (#523); it used to be stored, and the save
    /// threw <see cref="NullReferenceException"/> when it laid out the table. In a row that a cell
    /// above covers through <see cref="Cell.RowSpan"/>, the row's cells fill the columns left over,
    /// in order, and a cell past the last column is not drawn, though its text is still measured
    /// (#487).
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="cell"/> is <see langword="null"/>.
    /// </exception>
    public Row AddCell(Cell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        _cells.Add(cell);
        return this;
    }

    /// <summary>Adds a text cell to the row. Returns this row.</summary>
    /// <remarks>
    /// Creates the cell with <see cref="Cell(string)"/> and adds it, so a line break in the text is
    /// not honoured; see <see cref="Cell.Content"/>. A null <paramref name="text"/> is added as an
    /// empty cell (#547). It used to be stored as it was, and the save threw
    /// <see cref="NullReferenceException"/> when it sized a column automatically, but not in a
    /// table where every column has a positive finite width in
    /// <see cref="TableElement.ColWidths"/>, where the cell was drawn empty. In a row that a cell
    /// above covers through <see cref="Cell.RowSpan"/>, the row's cells fill the columns left over,
    /// in order, and a cell past the last column is not drawn, though its text is still measured
    /// (#487).
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Row AddCell(string text) => AddCell(new Cell(text));
}
