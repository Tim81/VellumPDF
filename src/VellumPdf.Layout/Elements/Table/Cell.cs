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
    /// <para>A span reaching past the columns left in its row is clamped to them, so the cell
    /// ends at the table's right edge rather than beyond it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property, when the
    /// span is below one. The message names the row and the cell.
    /// </exception>
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
    /// <para>A span reaching past the rows this page draws is reduced to the rows actually
    /// drawn, and the <c>/RowSpan</c> attribute written into the tagged structure follows the
    /// reduced figure rather than the one set here, so a reader is never told about rows that
    /// are not on the page (#493).</para>
    /// </remarks>
    public int RowSpan { get; init; } = 1;

    /// <summary>Text style for the cell content; falls back to the table default when null.</summary>
    public TextStyle? Style { get; init; }

    /// <summary>Inner padding between the cell border and its content.</summary>
    /// <remarks>
    /// <b>A non-finite inset is refused.</b> <see cref="Document.Save(System.IO.Stream)"/>
    /// throws <see cref="InvalidOperationException"/> naming the row and the cell, because the
    /// number would reach the content stream as a token no reader can parse.
    /// <para><b>Do not pass a negative inset.</b> It is not refused, and it does not clip: the
    /// cell's text is placed outside the cell, over its neighbour or past the table edge. A
    /// later major version will reject it.</para>
    /// <para><b>Do not pass padding wider than the column.</b> It is not refused, and what
    /// happens is worse than a collapsed cell: the inner width is clamped to one point, so the
    /// text wraps to one glyph per line and every line is placed at the left padding's own
    /// offset, which is outside the column and usually outside the page. Measured with 400 points
    /// of horizontal padding in a 260-point column on a 300-point page: ten glyphs, ten lines,
    /// every one at x = 420. Nothing is dropped and nothing reports it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property, when any inset is not finite. The message names the row and the cell.
    /// </exception>
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
