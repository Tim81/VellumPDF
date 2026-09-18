// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>
/// A grid-layout table element. Supports:
///   • Fixed column widths or auto-sizing (min/max pre-pass)
///   • Column and row spanning
///   • Cross-page splitting, with the table's leading contiguous run of header rows repeated at
///     the top of each continuation page
///   • Collapsed (shared) border rendering
/// </summary>
/// <remarks>
/// A table that resolves to no columns is refused at save; see <see cref="Rows"/>. A table whose
/// rows are all headers cannot be drawn; see <see cref="AddRow"/>. The table is one element for the
/// page limit described on <see cref="IRenderer.Layout"/>: a table that needs more than
/// <b>50,000</b> page continuations makes the save throw.
/// </remarks>
public sealed class TableElement
{
    /// <summary>Creates an empty table.</summary>
    /// <remarks>
    /// A save before a row holding a cell is added throws <see cref="InvalidOperationException"/>;
    /// see <see cref="Rows"/>.
    /// </remarks>
    public TableElement() { }

    private readonly List<Row> _rows = [];
    private readonly List<double> _colWidths = [];   // 0 = auto

    /// <summary>Text style applied to cells that have no explicit style.</summary>
    /// <remarks>
    /// Null means <see cref="TextStyle.Default"/> for cells that have no style of their own.
    /// </remarks>
    public TextStyle? DefaultCellStyle { get; init; }

    /// <summary>Width of the table border lines, in points.</summary>
    /// <remarks>
    /// A non-finite width is refused. <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> and names the table.
    /// <para><b>Attention</b>: zero does <b>not</b> hide the borders. It asks the device for its
    /// thinnest line, so the grid is still drawn. Each border stays one device pixel while
    /// everything around it shrinks, so the grid reads as proportionally heavier the further the
    /// page is scaled down.</para>
    /// <para>There is at present <b>no</b> way to draw a table without a grid. Every cell is
    /// stroked unconditionally and <see cref="BorderColor"/> is not nullable. If you need a
    /// gridless table, the nearest you can get is a border colour matching the page.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this
    /// property, when the width is not finite. The message names the table.
    /// </exception>
    public double BorderWidth { get; init; } = 0.5;

    /// <summary>Color of the table border lines.</summary>
    /// <remarks>Stored as given. Channels are not clamped; see <see cref="ColorRgb"/>.</remarks>
    public ColorRgb BorderColor { get; init; } = ColorRgb.Black;

    /// <summary>Outer margins applied around the whole table.</summary>
    /// <remarks>
    /// <b>Attention</b>: no edge is checked. Each edge is taken off the area this element is given,
    /// and the element is laid out in whatever box is left, even when that box is empty, inverted
    /// or <c>NaN</c>. A negative or non-finite edge, or edges wider than the area, can therefore
    /// make the save throw an exception about something else, write a <c>NaN</c> or <c>Infinity</c>
    /// token into the content stream, re-wrap, move or mirror the content, or leave the element off
    /// the page. The bottom edge only limits that box: it adds no space before the next element. A
    /// negative or non-finite top edge can also move the elements placed after this one.
    /// <para>Do not pass a negative or non-finite edge. A later major version will refuse
    /// both.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the box the edges leave is too small for the element. The message
    /// says the element is too tall to fit on a page and does not name the margins.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when an edge leaves a later element at a non-finite position, and the
    /// save writes that position outside the content stream, as a heading's bookmark or the
    /// rectangle of a link from <see cref="TextStyle.LinkUri"/>. Negative infinity can do this. The
    /// message says PDF does not support NaN or Infinity as a real number.
    /// </exception>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>The rows in the table, in render order.</summary>
    /// <remarks>
    /// A table that resolves to no columns is refused at save: no rows, or every row empty.
    /// A mix of empty rows and rows that have cells is not that case.
    /// <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> when there is nothing to draw.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save, when this collection is empty or every row has no cells.
    /// </exception>
    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>Configured column widths in points; a value of 0 means auto-size.</summary>
    /// <remarks>
    /// Entry <c>i</c> sets column <c>i</c>. A missing, zero, negative or non-finite entry means
    /// auto, and the auto columns share the width the explicit ones leave. Entries past the
    /// column count are ignored.
    /// <para>Explicit widths that together overrun the available width are all scaled by one
    /// ratio. So one very large entry leaves the other explicit columns near zero, and entries
    /// whose sum overflows to infinity are all scaled to zero (#546). A positive entry under
    /// 5e-6 is kept and written as width 0. None of this is reported.</para>
    /// <para>Do not rely on scaling to fit a table. Pass widths that fit the space you give
    /// it.</para>
    /// </remarks>
    public IReadOnlyList<double> ColWidths => _colWidths;

    /// <summary>Sets the column widths (0 = auto) and returns this instance for chaining.</summary>
    /// <remarks>
    /// Replaces every width. Called with no arguments it clears them, so every column is auto. A
    /// null array is refused only after the existing widths are cleared, so they are lost. The
    /// entries follow the rules on <see cref="ColWidths"/>; none is refused here.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="widths"/> is <see langword="null"/>.
    /// </exception>
    public TableElement SetColumnWidths(params double[] widths)
    {
        _colWidths.Clear();
        _colWidths.AddRange(widths);
        return this;
    }

    /// <summary>
    /// Appends a new row, optionally flagged as a header, and returns it. A header row repeats at
    /// the top of each continuation page only while it belongs to the table's leading contiguous
    /// run of header rows; a header row added after a data row draws once, where it occurs.
    /// </summary>
    /// <remarks>
    /// A table whose rows are all headers cannot be drawn: it lays out as nothing, so the save
    /// throws <see cref="InvalidOperationException"/> even when the table would fit (#488).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when every row of the table is a header row. The message says the element is
    /// too tall to fit on a page (#488), or, when no header row has a cell, that the table resolved
    /// to no columns.
    /// </exception>
    public Row AddRow(bool isHeader = false)
    {
        var row = new Row { IsHeader = isHeader };
        _rows.Add(row);
        return row;
    }

    /// <summary>
    /// Appends a new header row and returns it. See <see cref="AddRow"/> for when a header row
    /// repeats across continuation pages.
    /// </summary>
    /// <remarks>
    /// A table whose rows are all headers cannot be drawn: it lays out as nothing, so the save
    /// throws <see cref="InvalidOperationException"/> even when the table would fit (#488).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when every row of the table is a header row. The message says the element is
    /// too tall to fit on a page (#488), or, when no header row has a cell, that the table resolved
    /// to no columns.
    /// </exception>
    public Row AddHeaderRow()
    {
        var row = new Row { IsHeader = true };
        _rows.Add(row);
        return row;
    }
}
