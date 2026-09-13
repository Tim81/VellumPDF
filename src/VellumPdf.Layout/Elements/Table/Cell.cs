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
    /// The table's column count is always the largest span sum across its rows. Widening a
    /// cell's own span usually widens the grid to match, instead of overrunning it; the exception
    /// is below.
    /// <para>Zero or negative is <b>refused</b>. <see cref="Document.Save(System.IO.Stream)"/>
    /// throws <see cref="InvalidOperationException"/> naming the row and cell, because a span of
    /// zero can leave the grid with no columns at all and nothing to draw into.</para>
    /// <para>A span wider than the columns other rows have already declared usually widens the
    /// grid instead of being clamped down: the column count is fixed at the largest span sum
    /// across every row (above), so this cell's span becomes the reason those columns exist, and
    /// every other row leaves them empty. That is not the only outcome, though. When an
    /// earlier row's <see cref="RowSpan"/> already occupies this row's leading columns, this
    /// cell's own span is clamped to whatever columns are left rather than widening the grid
    /// further: measured, a five-column span with four leading columns already spoken for by such
    /// a <see cref="RowSpan"/> drew one column wide, ending exactly at the table's right edge, and
    /// a three-column span in the equivalent narrower table did the same. Whether a span widens
    /// the grid or is clamped depends on what the rows above it already occupy, not on the span
    /// alone.</para>
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
    /// <para><b>Attention</b>: zero and negative are <b>not</b> refused, and both behave as 1. That is
    /// an accident of how the draw loop tests the span, not a guarantee, so do not write code
    /// that depends on it. A later major version will reject both.</para>
    /// <para>A span reaching past the rows this page draws is reduced to the rows actually
    /// drawn. The <c>/RowSpan</c> attribute written into the tagged structure follows the reduced
    /// figure, not the one you set, so a reader is never told about rows that are not on the
    /// page.</para>
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
    /// <para><b>Attention</b>: a negative inset is not refused, and nothing clips the result. The cell's
    /// text is placed outside the cell, over its neighbour or past the table's edge. You have to
    /// keep the insets positive yourself. A later major version will reject them.</para>
    /// <para>Padding wider than the column is not refused either, and the result is worse than a
    /// collapsed cell. The inner width is clamped to one point, so the text wraps to one glyph per
    /// line, and every line is placed at <c>Left</c>'s own offset, outside the column and usually
    /// outside the page. Measured with <c>Left</c> at 400 in a 260-point column on a 300-point
    /// page: ten glyphs on ten lines, every one at x = 420. Nothing is dropped and nothing
    /// reports it.</para>
    /// <para><see cref="EdgeInsets.Horizontal"/> is <c>Left</c> plus <c>Right</c>, but the drawn
    /// position follows <c>Left</c> alone, not how the total is split. Measured on the same
    /// 260-point column: splitting 400 points evenly, <c>Left</c> = 200 and <c>Right</c> = 200,
    /// lands the text at x = 220, back inside the 300-point page; giving <c>Left</c> the same 400
    /// on its own, whatever <c>Right</c> holds, still lands it at x = 420. Setting all four edges
    /// to 400 does not sit between those two: <c>Top</c> and <c>Bottom</c> grow the row as well,
    /// and saving refuses the result as too tall to fit, with no text drawn at all.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property.
    /// A non-finite inset gives a message naming the row and the cell. A finite inset large
    /// enough to grow the row past the page (as described above for <c>Top</c> and <c>Bottom</c>
    /// set to 400) throws the generic too-tall exception instead, whose message names neither.
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
