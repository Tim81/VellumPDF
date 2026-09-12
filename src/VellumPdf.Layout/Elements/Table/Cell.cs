// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>A single table cell, optionally spanning multiple columns or rows.</summary>
public sealed class Cell
{
    /// <summary>The text content rendered in the cell.</summary>
    public string Content { get; }

    /// <summary>
    /// Number of columns this cell spans. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// The table's column count is the largest span sum across its rows, so widening a cell widens
    /// the grid rather than overrunning it.
    /// <para><b>Zero or negative is refused.</b> <see cref="Document.Save(System.IO.Stream)"/>
    /// throws <see cref="InvalidOperationException"/> naming the row and cell, because a span of
    /// zero can leave the grid with no columns at all and nothing to draw into.</para>
    /// </remarks>
    public int ColSpan { get; init; } = 1;

    /// <summary>
    /// Number of rows this cell spans. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// A spanning cell is drawn once, at its own row, across the combined height of the rows it
    /// covers, and those rows skip the columns it occupies. A page break is never placed inside a
    /// spanning group; a group too tall for one page raises rather than splitting.
    /// <para><b>Do not pass zero or a negative value.</b> It is not currently refused and behaves
    /// as 1, which is an accident of how the draw loop tests the span rather than a guarantee. A
    /// later major version will reject it, so do not write code that depends on the current
    /// behaviour.</para>
    /// </remarks>
    public int RowSpan { get; init; } = 1;

    /// <summary>Text style for the cell content; falls back to the table default when null.</summary>
    public TextStyle? Style { get; init; }

    /// <summary>Inner padding between the cell border and its content.</summary>
    public EdgeInsets Padding { get; init; } = new EdgeInsets(4, 6, 4, 6);

    /// <summary>Optional background fill color for the cell.</summary>
    public ColorRgb? Background { get; init; }

    /// <summary>Horizontal alignment of the cell content.</summary>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>
    /// Optional per-element language override (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>).
    /// When set and the document is tagged, written as <c>/Lang</c> on the struct element.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>Creates a cell with the given text content.</summary>
    public Cell(string content) => Content = content;
}
