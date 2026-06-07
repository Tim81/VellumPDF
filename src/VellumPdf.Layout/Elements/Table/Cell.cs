// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>A single table cell, optionally spanning multiple columns or rows.</summary>
public sealed class Cell
{
    public string Content { get; }
    public int ColSpan { get; init; } = 1;
    public int RowSpan { get; init; } = 1;
    public TextStyle? Style { get; init; }
    public EdgeInsets Padding { get; init; } = new EdgeInsets(4, 6, 4, 6);
    public ColorRgb? Background { get; init; }
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    public Cell(string content) => Content = content;
}
