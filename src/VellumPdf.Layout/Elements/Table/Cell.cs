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
    /// <para>Zero or negative is <b>refused</b>. <see cref="Document.Save(System.IO.Stream)"/>
    /// throws <see cref="InvalidOperationException"/> naming the row and cell, because a span of
    /// zero can leave the grid with no columns at all and nothing to draw into.</para>
    /// <para>A span wider than the columns other rows have already declared is never clamped
    /// down: the column count is fixed at the largest span sum across every row (above), so this
    /// cell's span becomes the reason those columns exist, and every other row simply leaves them
    /// empty.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property, when the
    /// span is below one. The message names the row and the cell.
    /// </exception>
    /// <exception cref="OverflowException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property,
    /// when a row's <c>ColSpan</c> values sum past <see cref="int.MaxValue"/> while the table's
    /// column count is resolved.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property,
    /// when a resolved column count near <see cref="int.MaxValue"/> asks for a per-column width
    /// array too large to allocate.
    /// </exception>
    public int ColSpan { get; init; } = 1;

    /// <summary>
    /// Number of rows this cell spans. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// A spanning cell is drawn once, at its own row, across the combined height of the rows it
    /// covers, and those rows skip the columns it occupies. A page break is never placed inside a
    /// spanning group; a group too tall for one page raises rather than splitting.
    /// <para>Attention: zero or negative is <b>not</b> refused. Both behave as 1.
    /// That is an accident of how the draw loop tests the span, not a guarantee, so do not write
    /// code that depends on it. A later major version will reject them.</para>
    /// <para>A span reaching past the rows this page draws is reduced to the rows actually
    /// drawn. The <c>/RowSpan</c> attribute written into the tagged structure follows the reduced
    /// figure, not the one you set, so a reader is never told about rows that are not on the page
    /// (#493).</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when a spanning group is taller than
    /// one page. A page break is never placed inside such a group, so it raises instead of
    /// splitting.
    /// </exception>
    public int RowSpan { get; init; } = 1;

    /// <summary>Text style for the cell content; falls back to the table default when null.</summary>
    public TextStyle? Style { get; init; }

    /// <summary>Inner padding between the cell border and its content.</summary>
    /// <remarks>
    /// A non-finite inset is refused. <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> and names the row and the cell. The number would
    /// otherwise reach the content stream as a token that no reader can parse.
    /// <para>Attention: a negative inset is not refused, and nothing clips the result. The cell's
    /// text is placed outside the cell, over its neighbour or past the table's edge. You have to
    /// keep the insets positive yourself. A later major version will reject them.</para>
    /// <para>Padding wider than the column is not refused either, and the result is worse than a
    /// collapsed cell. The inner width is clamped to one point, so the text wraps to one glyph per
    /// line, and every line is placed at <c>Left</c>'s own offset, outside the column and usually
    /// outside the page. Measured with <c>Left</c> at 400 in a 260-point column on a 300-point
    /// page: ten glyphs on ten lines, every one at x = 420. Nothing is dropped and nothing
    /// reports it.</para>
    /// <para><see cref="EdgeInsets.Horizontal"/> is <c>Left</c> plus <c>Right</c>, so an
    /// insets value of 400 across both sides puts the text at x = 220 rather than 420. It is
    /// <c>Left</c> alone that decides where a line starts.</para>
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
