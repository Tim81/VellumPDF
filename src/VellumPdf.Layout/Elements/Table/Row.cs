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
    /// is; see <see cref="TableElement.Rows"/>.
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
    /// <see cref="TableElement.AddHeaderRow"/>, the only members that add a row, has no background.
    /// Those create the row without a background, and this property can only be set when a row is
    /// constructed. Set <see cref="Cell.Background"/> on each cell instead (#543).
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
    /// save throws when it sizes a column automatically, which it does for a column without a
    /// positive finite width in <see cref="TableElement.ColWidths"/>. In a column with a positive
    /// finite width, the cell is drawn empty. In a row that a cell above covers through
    /// <see cref="Cell.RowSpan"/>, the row's cells fill the columns left, in order, and a cell past
    /// the last column is not drawn, though its text is still measured (#487).
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="text"/> is <see langword="null"/> and the table sizes a
    /// column automatically; see <see cref="TableElement.ColWidths"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the text holds an unpaired surrogate and is measured in an embedded
    /// font, as layout does; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Row AddCell(string text) => AddCell(new Cell(text));
}
