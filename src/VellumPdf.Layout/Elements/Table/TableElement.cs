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
    public TextStyle? DefaultCellStyle { get; init; }

    /// <summary>Width of the table border lines, in points.</summary>
    /// <remarks>
    /// A non-finite width is refused. <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> and names the table.
    /// <para><b>Attention</b>: zero does <b>not</b> hide the borders. It asks the device for its
    /// thinnest line, so the grid is still drawn, and it grows heavier as the page is scaled
    /// down.</para>
    /// <para>There is at present <b>no</b> way to draw a table without a grid. Every cell is
    /// stroked unconditionally and <see cref="BorderColor"/> is not nullable. If you need a
    /// gridless table, the nearest you can get is a border colour matching the page.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property, when the width is not finite. The message names the table.
    /// </exception>
    public double BorderWidth { get; init; } = 0.5;

    /// <summary>Color of the table border lines.</summary>
    public ColorRgb BorderColor { get; init; } = ColorRgb.Black;

    /// <summary>Outer margins applied around the whole table.</summary>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>The rows in the table, in render order.</summary>
    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>Configured column widths in points; a value of 0 means auto-size.</summary>
    public IReadOnlyList<double> ColWidths => _colWidths;

    /// <summary>Sets the column widths (0 = auto) and returns this instance for chaining.</summary>
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
    public Row AddHeaderRow()
    {
        var row = new Row { IsHeader = true };
        _rows.Add(row);
        return row;
    }
}
