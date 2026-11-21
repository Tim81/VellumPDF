// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>A table row containing one or more cells.</summary>
/// <remarks>
/// <see cref="TableElement.AddRow(bool)"/> and <see cref="TableElement.AddHeaderRow"/> create a row
/// and add it. A row built with the constructor, to set <see cref="Background"/>, is added with
/// <see cref="TableElement.Add(Row)"/>.
/// </remarks>
public sealed class Row
{
    /// <summary>Creates an empty row.</summary>
    /// <remarks>
    /// The row is part of no table until it is passed to <see cref="TableElement.Add(Row)"/>, and a
    /// table does not draw a row it does not hold. Set <see cref="IsHeader"/> and
    /// <see cref="Background"/> in the object initialiser: both are <see langword="init"/>-only.
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
    /// and the table splits between rows like any other. A tagged table marks a header row's
    /// cells that hold a line as header cells either way; a cell of a cut row that has no line on
    /// a page is tagged as an empty data cell there, see <see cref="TableElement"/>.
    /// </remarks>
    public bool IsHeader { get; init; }

    /// <summary>Optional background fill color for the row.</summary>
    /// <remarks>
    /// Null means no fill. The colour takes effect only when the row is added with
    /// <see cref="TableElement.Add(Row)"/>: <see cref="TableElement.AddRow(bool)"/> and
    /// <see cref="TableElement.AddHeaderRow"/> create rows with no background, and the property
    /// cannot be set afterwards. <see cref="TableElement.Add(Row)"/> refuses a channel that is not
    /// finite or lies outside 0 to 1, because ISO 32000-2:2020, 8.6.4.3 says each DeviceRGB
    /// component is a number in that range. Setting the property refuses nothing, and a row that is
    /// never added is never checked.
    /// <para>The fill is drawn cell by cell, in the rectangle of each cell the row draws, ahead of
    /// the cell's border and text. A cell that a <see cref="Cell.RowSpan"/> from an earlier row
    /// pushes past the last column is not drawn, so it is not filled either; see
    /// <see cref="AddCell(Cell)"/>. A cell's own <see cref="Cell.Background"/> replaces the row's
    /// for that cell. A column the row has no cell for, because the row is shorter than the table
    /// or a <see cref="Cell.RowSpan"/> from an earlier row covers it, gets no fill from this row. A
    /// cell that spans rows is filled over its whole height with the background of the row it
    /// starts in, and a cell that spans columns over its whole width. The row's fill is drawn again
    /// with the row when a leading run of header rows repeats on a continuation page.</para>
    /// <para>The check in <see cref="TableElement.Add(Row)"/> is the only one. A row that reaches a
    /// table another way, such as through a downcast of <see cref="TableElement.Rows"/> to
    /// <c>List&lt;Row&gt;</c>, is not checked: a channel that is not finite is refused from the
    /// save, and a finite channel outside 0 to 1 is written as given. <b>Do not</b> add rows that
    /// way.</para>
    /// </remarks>
    public ColorRgb? Background { get; init; }

    /// <summary>Adds a cell to the row. Returns this row.</summary>
    /// <remarks>
    /// A null <paramref name="cell"/> is refused here. A cell added after the row is in a table is
    /// drawn by that table's next save. In a row that a cell
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
