// Copyright 2026 Timothy van der Ham (@Tim81)
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

    public TextStyle? DefaultCellStyle { get; init; }
    public double BorderWidth { get; init; } = 0.5;
    public ColorRgb BorderColor { get; init; } = ColorRgb.Black;
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    public IReadOnlyList<Row> Rows => _rows;
    public IReadOnlyList<double> ColWidths => _colWidths;

    public TableElement SetColumnWidths(params double[] widths)
    {
        _colWidths.Clear();
        _colWidths.AddRange(widths);
        return this;
    }

    public Row AddRow(bool isHeader = false)
    {
        var row = new Row { IsHeader = isHeader };
        _rows.Add(row);
        return row;
    }

    public Row AddHeaderRow()
    {
        var row = new Row { IsHeader = true };
        _rows.Add(row);
        return row;
    }
}
