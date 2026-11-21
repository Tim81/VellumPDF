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
    /// a null.
    /// </remarks>
    public IReadOnlyList<Cell> Cells => _cells;

    /// <summary>Whether this row is a header row (may be repeated on each page).</summary>
    /// <remarks>
    /// Only the leading run of header rows repeats on each continuation page, and only when a row
    /// that is not a header follows it. A header row after a data row is drawn once, where it is.
    /// A table whose rows are all headers has no run to repeat: each row is drawn once, in order,
    /// and the table splits between rows like any other. A tagged table marks every header row's
    /// cells as header cells either way.
    /// </remarks>
    public bool IsHeader { get; init; }

    /// <summary>Optional background fill color for the row.</summary>
    /// <remarks>
    /// <b>Attention</b>: a row added through <see cref="TableElement.AddRow"/> or
    /// <see cref="TableElement.AddHeaderRow"/>, the only members that add a row, has no background,
    /// and this property can only be set when a row is constructed. Set
    /// <see cref="Cell.Background"/> on each cell instead (#543).
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
    /// this property, when a channel is <c>NaN</c> or infinite and the row's cell has no
    /// <see cref="Cell.Background"/> of its own. <see cref="VellumPdf.Canvas.PdfCanvas"/> refuses it
    /// and <c>ParamName</c> is null.
    /// </exception>
    public ColorRgb? Background { get; init; }

    /// <summary>Adds a cell to the row. Returns this row.</summary>
    /// <remarks>
    /// A null <paramref name="cell"/> is refused here. In a row that a cell
    /// above covers through <see cref="Cell.RowSpan"/>, the row's cells fill the columns left over,
    /// in order, and a cell past the last column is not drawn, though its text is still measured.
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
    /// empty cell. In a row that a cell
    /// above covers through <see cref="Cell.RowSpan"/>, the row's cells fill the columns left over,
    /// in order, and a cell past the last column is not drawn, though its text is still measured.
    /// </remarks>
    /// <inheritdoc cref="Paragraph.Add(string, TextStyle)" path="/exception[@cref='T:System.ArgumentException']"/>
    public Row AddCell(string text) => AddCell(new Cell(text));
}
