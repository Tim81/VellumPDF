// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>A single table cell, optionally spanning multiple columns or rows.</summary>
/// <remarks>
/// A <see cref="ColSpan"/> below 1 is refused at save, and a <see cref="RowSpan"/> below 1 is
/// drawn as 1. A null <see cref="Content"/> can make the save throw; see the constructor.
/// <see cref="HorizontalAlignment.Justify"/> is drawn as <see cref="HorizontalAlignment.Left"/>.
/// </remarks>
public sealed class Cell
{
    /// <summary>The text content rendered in the cell.</summary>
    /// <remarks>
    /// An empty string draws no text. A null value can make the save throw; see the constructor.
    /// Spaces are the only break points between words, and a word wider than the cell is broken
    /// between characters. A line feed, carriage return or tab is not a break. In a standard-14
    /// font each is written into the cell as it is; in an embedded font each is drawn as whatever
    /// glyph the font's character map gives it, which is .notdef (glyph 0) when the map has no
    /// entry.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text is <see langword="null"/> and a column is sized
    /// automatically; see the constructor.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public string Content { get; }

    /// <summary>
    /// Number of columns this cell spans. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// The table's column count is the largest span sum across its rows, and every cell's span
    /// counts towards its own row's sum. A span wider than the column count the other rows
    /// produce therefore widens the grid, and those rows leave the new columns empty.
    /// <para>Zero or negative is <b>refused</b>. <see cref="Document.Save(System.IO.Stream)"/>
    /// throws <see cref="InvalidOperationException"/> naming the row and cell, because a span of
    /// zero can leave the grid with no columns at all and nothing to draw into.</para>
    /// <para>The width the cell is drawn at is a separate question from that count. Where an
    /// earlier row's <see cref="RowSpan"/> already occupies this row's leading columns, the cell
    /// is drawn only as wide as the columns left over. Measured: a five-column span with four
    /// leading columns spoken for drew <b>one column</b> wide, ending exactly at the table's
    /// right edge. A three-column span in the equivalent narrower table did the same.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property,
    /// when the span is below one. The message names the row and the cell.
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
    /// A spanning cell is drawn at its own row, across the combined height of the rows it covers,
    /// and those rows skip the columns it occupies. A page break is not placed inside a spanning
    /// group that starts in a data row. A header row placed after the first data row counts as a
    /// data row here. Such groups that share a row are held together as one block, and the save
    /// throws when a group or block is taller than the page leaves below the repeated header rows.
    /// Two kinds of group can still be split across pages. One starts in the table's leading header
    /// rows: on each continuation page its cell is drawn again, only as tall as the header rows it
    /// covers there, and the data rows it covers stop skipping its columns, so their cells move
    /// into those columns. The other is a span whose last row, the zero-based row index plus
    /// RowSpan minus one, overflows <see cref="int"/>, as <see cref="int.MaxValue"/> does from the
    /// third row on.
    /// <para><b>Attention</b>: zero and negative are <b>not</b> refused, and both behave as 1. That
    /// is an accident of how the draw loop tests the span, not a guarantee, so do not write code
    /// that depends on it. A later major version will reject both.</para>
    /// <para>A span reaching past the rows this page draws is reduced to the rows actually
    /// drawn. The <c>/RowSpan</c> attribute written into the tagged structure follows the reduced
    /// figure, not the one you set, so a reader is never told about rows that are not on the
    /// page.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when a spanning group that starts in a
    /// data row, or in a header row placed after the first data row, is taller than the page leaves
    /// below the repeated header rows, unless its last row overflows <see cref="int"/>. Such groups
    /// that share a row count as one. The remarks name the groups that can be split instead.
    /// </exception>
    public int RowSpan { get; init; } = 1;

    /// <summary>Text style for the cell content; falls back to the table default when null.</summary>
    /// <remarks>
    /// Null means <see cref="TableElement.DefaultCellStyle"/>, then
    /// <see cref="TextStyle.Default"/>. Refusals on size, leading and font are on
    /// <see cref="TextStyle"/> and are raised from the save. A <see cref="TextStyle.LinkUri"/> in
    /// it is ignored (#475).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when text in this style holds an unpaired surrogate and is measured in
    /// an embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public TextStyle? Style { get; init; }

    /// <summary>Inner padding between the cell border and its content.</summary>
    /// <remarks>
    /// A non-finite inset is refused. <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> and names the row and the cell. The number would
    /// otherwise reach the content stream as a token that no reader can parse.
    /// <para><b>Attention</b>: a negative inset is not refused, and nothing clips the result. The
    /// cell's text is placed outside the cell, over its neighbour or past the table's edge. You
    /// have to keep the insets positive yourself. A later major version will reject them.</para>
    /// <para>Padding wider than the column is not refused either, and the result is worse than a
    /// collapsed cell. The inner width is clamped to one point, so the text wraps to one glyph per
    /// line, and every line is placed at <c>Left</c>'s own offset, outside the column and usually
    /// outside the page. Measured with <c>Left</c> at 400 in a 260-point column on a 300-point
    /// page: ten glyphs on ten lines, every one at x = 420. Nothing is dropped and nothing
    /// reports it.</para>
    /// <para><see cref="EdgeInsets.Horizontal"/> is <c>Left</c> plus <c>Right</c>, but the drawn
    /// position follows <c>Left</c> alone, not how the total is split. Measured on the same
    /// 260-point column: splitting 400 points evenly, <c>Left</c> = 200 and <c>Right</c> = 200,
    /// lands the text at x = 220, back inside the 300-point page. Giving <c>Left</c> the same 400
    /// lands the text at x = 420 whatever <c>Right</c> holds. The vertical edges are a separate
    /// limit: at 400 on all four the row outgrows the page, and the save refuses it as too tall,
    /// with no text drawn at all.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property.
    /// A non-finite inset gives a message naming the row and the cell. A finite inset large
    /// enough to grow the row past the page (above, with all four edges at 400) throws the
    /// generic too-tall exception instead, whose message names neither.
    /// </exception>
    public EdgeInsets Padding { get; init; } = new EdgeInsets(4, 6, 4, 6);

    /// <summary>Optional background fill color for the cell.</summary>
    /// <remarks>
    /// Null means no fill. A colour is stored as given. Channels are not checked or clamped. Each
    /// is written into the content stream rounded to five decimals, and <c>NaN</c> or
    /// <c>Infinity</c> as that token; see <see cref="ColorRgb"/>.
    /// </remarks>
    public ColorRgb? Background { get; init; }

    /// <summary>Horizontal alignment of the cell content.</summary>
    /// <remarks>
    /// <see cref="HorizontalAlignment.Justify"/> is drawn as
    /// <see cref="HorizontalAlignment.Left"/>. Cell text is wrapped but never stretched; only
    /// paragraph and heading text are justified.
    /// </remarks>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>
    /// Optional per-element language override (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>).
    /// When set and the document is tagged, written as <c>/Lang</c> on the struct element.
    /// </summary>
    /// <remarks>
    /// The string is not validated: it is trimmed and written, so an ill-formed tag reaches the
    /// file. An empty or whitespace-only string is not written, and nothing is written when the
    /// document is not tagged.
    /// <para>Do not pass a tag that is not well-formed BCP 47. A later major version will refuse
    /// one.</para>
    /// </remarks>
    public string? Language { get; init; }

    /// <summary>Creates a cell with the given text content.</summary>
    /// <remarks>
    /// A null <paramref name="content"/> is stored. The save throws when it sizes a column
    /// automatically, which it does for a column without a positive finite width in
    /// <see cref="TableElement.ColWidths"/>. In a table where every column has a positive finite
    /// width, the cell is drawn empty.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="content"/> is <see langword="null"/> and the table
    /// sizes a column automatically; see <see cref="TableElement.ColWidths"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Cell(string content) => Content = content;
}
