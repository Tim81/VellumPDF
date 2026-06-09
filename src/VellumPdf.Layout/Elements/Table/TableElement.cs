// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>
/// A grid-layout table element. Supports:
///   • Fixed column widths or auto-sizing (min/max pre-pass)
///   • Column and row spanning
///   • Cross-page splitting with repeating header rows
///   • Collapsed (shared) border rendering
/// </summary>
public sealed class TableElement
{
    private readonly List<Row> _rows = [];
    private readonly List<double> _colWidths = [];   // 0 = auto

    /// <summary>Text style applied to cells that have no explicit style.</summary>
    public TextStyle? DefaultCellStyle { get; init; }

    /// <summary>Width of the table border lines, in points.</summary>
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

    /// <summary>Appends a new row, optionally flagged as a repeating header, and returns it.</summary>
    public Row AddRow(bool isHeader = false)
    {
        var row = new Row { IsHeader = isHeader };
        _rows.Add(row);
        return row;
    }

    /// <summary>Appends a new repeating header row and returns it.</summary>
    public Row AddHeaderRow()
    {
        var row = new Row { IsHeader = true };
        _rows.Add(row);
        return row;
    }
}
