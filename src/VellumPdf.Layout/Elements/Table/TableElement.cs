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
public sealed class TableElement
{
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
    /// <b>Attention</b>: this inset is <b>not</b> validated. <see cref="LineSeparator.Margins"/>
    /// and <see cref="Table.Cell.Padding"/> are the only insets this package checks; every other
    /// one, including this, reaches the geometry as given.
    /// <para>So a non-finite inset is not refused on your behalf. What happens instead depends on
    /// where the arithmetic lands, not on which member you set, and none of the outcomes is a
    /// refusal naming this property: the value can reach the content stream as a token no reader
    /// can parse, or trip a later geometry check that blames something else. Nothing reports it
    /// either way.</para>
    /// <para>Do <b>not</b> pass a negative inset either, and do not read one as a way to position
    /// or resize. It is arithmetic on the available area rather than a placement instruction, so
    /// what a renderer then does with that area is what you get: some carry the content off the
    /// page, others absorb the value and draw exactly as they would at zero. Nothing is refused
    /// and nothing is reported. A later major version will reject both.</para>
    /// </remarks>
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
    /// An entry that cannot be a width is treated as auto. Extra entries past the column
    /// count are ignored. Explicit widths that overrun the available width are scaled down.
    /// Nothing reports any of those.
    /// </remarks>
    public IReadOnlyList<double> ColWidths => _colWidths;

    /// <summary>Sets the column widths (0 = auto) and returns this instance for chaining.</summary>
    /// <remarks>
    /// Same rules as <see cref="ColWidths"/>. This call does not refuse a negative or
    /// non-finite entry; the grid treats it as auto at layout.
    /// </remarks>
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
    /// A table whose rows are all headers cannot paginate on a page with room to spare (#488);
    /// save throws the too-tall <see cref="InvalidOperationException"/>.
    /// </remarks>
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
    /// Same all-header refusal as <see cref="AddRow"/>.
    /// </remarks>
    public Row AddHeaderRow()
    {
        var row = new Row { IsHeader = true };
        _rows.Add(row);
        return row;
    }
}
