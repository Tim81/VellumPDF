// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Elements.Table;

/// <summary>A single table cell, optionally spanning multiple columns or rows.</summary>
/// <remarks>
/// A cell holds one string, or a sequence of runs that each have their own style; see
/// <see cref="Cell(IEnumerable{TextRun})"/>.
/// A <see cref="ColSpan"/> below 1 is refused at save, and a <see cref="RowSpan"/> below 1 is
/// drawn as 1. A null text given to the constructor is stored, and refused where a column is
/// sized automatically; see the constructor.
/// <see cref="HorizontalAlignment.Justify"/> is drawn as <see cref="HorizontalAlignment.Left"/>.
/// </remarks>
public sealed class Cell
{
    /// <summary>The text content rendered in the cell.</summary>
    /// <remarks>
    /// For a cell built from runs this is the text of every run, joined without a separator. It is
    /// what the rules below read, so a line break, a tab or a space in any run is one in the cell.
    /// An empty string draws no text. The value is null when the constructor was given a null;
    /// the cell then draws no text, and the save refuses it only where a column is sized
    /// automatically, as the constructor describes.
    /// Spaces are the only break points between words, and a word wider than the cell is broken
    /// between characters.
    /// <para><b>A carriage return and line feed pair, a lone carriage return and a lone line feed
    /// each end a line.</b> An empty line between two of them is a blank line one leading tall, and
    /// so is the one after a trailing break: the text <c>a</c> followed by a line feed is two lines
    /// here, where a <see cref="Elements.Paragraph"/> draws one. Runs of spaces are kept as
    /// written, where a paragraph collapses them. Every other white-space control character, which
    /// is a tab, a form feed, a line tabulation, a next-line character, U+2028 and U+2029, is one
    /// space, so it reaches no font as a control character. None of these characters is reported
    /// as <see cref="VellumPdf.Layout.LayoutDiagnosticCode.NotdefGlyph"/> or
    /// <see cref="VellumPdf.Layout.LayoutDiagnosticCode.UnencodableCharacter"/>, so
    /// <see cref="Document.StrictDiagnostics"/> does not refuse a cell for one. The no-break
    /// spaces are written as they are, as in a paragraph. The other space separators, such as
    /// U+2003, are written as they are too, where a paragraph reads them as spaces. A column sized
    /// automatically takes its minimum from the widest word and its maximum from the widest line,
    /// each line of the cell measured on its own, where 2.3.3 measured the whole text as one line.
    /// The width the column is then given is shared out among the columns as it was, and can be
    /// more than the maximum or less than the minimum.</para>
    /// <para>A control character that is not white space, such as U+0001, is written as it is: in a
    /// standard-14 font as its WinAnsi code, and in an embedded font as whatever glyph the font's
    /// character map gives it, which is .notdef (glyph 0) when the map has no entry. The save lists
    /// each such character as <see cref="VellumPdf.Layout.LayoutDiagnosticCode.NotdefGlyph"/>, and
    /// <see cref="Document.StrictDiagnostics"/> refuses it; see
    /// <see cref="TextStyle.FontRef"/>.</para>
    /// <para>A cell has no tab stops, so a tab is one space.</para>
    /// <para><b>A cell is laid out unbroken, with its tabs and line breaks left out of what is
    /// drawn, when reading them would leave a row that cannot be cut no room on a page, or would
    /// take the table more page continuations than one element may take.</b> This is the one
    /// place that states the rule; the other members that mention it point here. The
    /// rows that are never cut between their lines are the repeated header rows and the rows
    /// joined by a <see cref="RowSpan"/>. The header rows have no room when they do not fit a
    /// page, or when they leave a row below them less than one line of one of its cells. A span
    /// group has no room when it is taller than what a page leaves under the repeated header
    /// rows. A span declared in a header row makes no group of the data rows under it, because a
    /// page may end between them; each of those rows is judged by its own height, and a span
    /// declared in a data row is a group. The cause is a line break, which adds a line, or any
    /// other control white space, because a tab is one space wide where the font measures the tab
    /// character at its own width: a tab can tip a table that holds no line break. Such a cell is
    /// wrapped at spaces alone and sized from its whole text, each tab and line break in it
    /// measured at the width its font gives it and left out of what is drawn. The words they
    /// separated run together in the drawing and in extracted text, and no line is wider than it
    /// was measured, though a glyph wider than its column can still extend past it, as in any
    /// cell. The save lists the cell as
    /// <see cref="VellumPdf.Layout.LayoutDiagnosticCode.CellWhiteSpaceDropped"/>, a warning that
    /// <see cref="Document.StrictDiagnostics"/> saves.</para>
    /// <para>Which cells give them up is decided in order: the header rows are judged first, then
    /// each group of rows joined by a <see cref="RowSpan"/>, each on the heights the decisions
    /// before it leave. A group is relieved in three steps, each taken only while the group still
    /// has no room: the cells of the group that hold a carriage return or a line feed; then those
    /// of the header rows; then every cell that holds any control white space and sits in the
    /// group's rows, in the header rows, or in an automatically sized column that a cell of
    /// those rows is in. The header rows are relieved in the same way: by their own cells that
    /// hold a carriage return or a line feed, and then by every cell that holds any control white
    /// space and sits in them or in an automatically sized column that a cell of them is in. A
    /// cell that holds only a line feed can be reached by any step.</para>
    /// <para><b>When those steps leave a header or a group with no room, a fourth step lays out
    /// unbroken every cell that holds control white space and spans a column that is sized
    /// automatically, wherever its row is.</b> The width of every column follows from the
    /// cells of the automatic columns, whichever rows they are in, so a cell in a column that no
    /// cell of the group is in can still have narrowed the group's columns by the width its
    /// breaks leave. With all of those cells unbroken every column is as wide as when the table
    /// measured each cell as one line, and each header or group that still had no room is as tall
    /// as that measured it, so one of them is refused only if it was too tall measured so. A
    /// group that fits keeps its breaks, and so does a cell no step reaches.</para>
    /// <para>A header row, or a group in an automatically sized column, that gives up a cell can
    /// change other rows: the column's width is shared with every row that has a cell in it. The
    /// groups after it are judged on the heights that result, so every group is judged on the
    /// heights the decisions before it leave, however many groups the table has.</para>
    /// <para>The limit is the 50,000 page continuations of <see cref="IRenderer.Layout"/>. A table
    /// whose line breaks, read, would take more lays every cell out unbroken from its first page,
    /// as it was measured before breaks were honoured. It is saved in the pages that measurement
    /// takes, and refused with the limit's message when that takes more than the limit too. The
    /// save lists each cell that holds control white space as
    /// <see cref="VellumPdf.Layout.LayoutDiagnosticCode.CellWhiteSpaceDropped"/>. The count
    /// depends on where the table starts: a table that keeps its breaks at the top of a page can
    /// give them up when it starts lower and first moves to the next page. <b>Do not pass</b> a
    /// cell with more lines than a document can hold and expect them drawn: shorten the text or
    /// split the table.</para>
    /// <para><b>A table that is refused when every cell is measured as one line is refused, with
    /// the message that measurement gives.</b> <see cref="Document.Save(System.IO.Stream)"/>
    /// throws <see cref="InvalidOperationException"/>, the too-tall exception, though every header
    /// and group has room with its breaks read: reading the breaks can leave a column wider or
    /// narrower than the one-line measurement does and so tip a group the other way, and the
    /// cells above give up their breaks only to save a table that measurement saves. <b>Do not
    /// pass</b> a table in the expectation that it stays refused: a release that measures the
    /// columns a <see cref="RowSpan"/> occupies as they are drawn can save some of them.</para>
    /// <para>A row that can be cut between its lines is cut when it is taller than a page. A
    /// repeated header row or a span group is never cut, and the save refuses it when it does not
    /// fit; a table whose rows are all headers repeats none of them, so its rows are cut like any
    /// other. <b>Do not pass</b> a line break in a repeated header row or a span group that has no
    /// room and expect the lines to be drawn: move the text to a row that can be cut, or shorten
    /// it. A table you lay out without the document, through a <see cref="LayoutContext"/> built
    /// with the public constructor or narrowed with <see cref="LayoutContext.WithArea"/>, or from a
    /// renderer of yours that forwards to its <see cref="Rendering.Table.TableRenderer"/>, reads no
    /// cell's breaks; see <see cref="Rendering.Table.TableRenderer.Layout"/>.</para>
    /// </remarks>
    /// <inheritdoc cref="Heading.Text" path="/exception[@cref='T:System.ArgumentException']"/>
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
    /// data row here, and so does every row of a table whose rows are all headers. Such groups
    /// that share a row are held together as one block, and the save throws when a group or block
    /// is taller than the space the page leaves below the repeated header rows. A row that declares
    /// a span, or lies under one, is never cut between its lines either; see
    /// <see cref="TableElement"/>. Two kinds of group
    /// can still be split across pages. One starts in the table's leading header rows, when a row
    /// that is not a header follows them: on each continuation page its cell is drawn again, only
    /// as tall as the header rows it covers there, and the data rows it covers stop skipping its
    /// columns, so their cells move into those columns. The other is a span whose last row, the
    /// zero-based row index plus RowSpan minus one, overflows <see cref="int"/>, as
    /// <see cref="int.MaxValue"/> does from the third row on.
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
    /// data row, in a header row placed after the first data row, or in a table whose rows are all
    /// headers, is taller than the space the page leaves below the repeated header rows, unless its
    /// last row overflows <see cref="int"/>. Such groups that share a row count as one. The message
    /// names the rows of the group and the height the page leaves. The remarks name the groups that
    /// can be split instead.
    /// </exception>
    public int RowSpan { get; init; } = 1;

    /// <summary>Text style for the cell content; falls back to the table default when null.</summary>
    /// <remarks>
    /// Null means <see cref="TableElement.DefaultCellStyle"/>, then
    /// <see cref="TextStyle.Default"/>. Refusals on size, leading, font and text colour are on
    /// <see cref="TextStyle"/> and are raised from the save. A <see cref="TextStyle.LinkUri"/> in
    /// it is ignored (#475).
    /// <para><b>A cell built from runs does not read this style for its text.</b> Each run is drawn
    /// in its own style, and neither this property nor <see cref="TableElement.DefaultCellStyle"/>
    /// styles a run. Setting it on such a cell is not refused. When no run holds text, the cell is
    /// the string cell of the empty string and takes this style, then the table's default.</para>
    /// <para>A cell with no style, in a table with no <see cref="TableElement.DefaultCellStyle"/>,
    /// does not take <see cref="Document.SetDefaultFont"/>; it takes
    /// <see cref="TextStyle.Default"/>. An empty cell reaches this too. The font is then Helvetica,
    /// which is not embedded. While <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses text the font cannot draw, and a Standard 14 font, under
    /// either claim.</para>
    /// </remarks>
    /// <inheritdoc cref="Heading.Style" path="/exception[@cref='T:System.InvalidOperationException' or @cref='T:System.IndexOutOfRangeException']"/>
    /// <inheritdoc cref="ListElement.DefaultStyle" path="/exception[@cref='T:System.ArgumentException']"/>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the element is
    /// written as glyph 0, or the document has a conformance claim and the element writes Helvetica
    /// or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
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
    /// with no text drawn at all. A row is cut between its lines only when its cells can each hold
    /// a line and their vertical padding on a page; see <see cref="TableElement"/>.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property.
    /// A non-finite inset gives a message naming the row and the cell. A finite vertical inset
    /// so large that the cell cannot hold one line and its padding on a page (above, with all four
    /// edges at 400) throws the too-tall exception, whose message ends with a sentence. That
    /// sentence names the row and the cell for any row that is not in the repeated header and is
    /// not held in a <see cref="RowSpan"/> group, which includes every row of a table made only of
    /// header rows. For a cell in the repeated header rows it names only those rows, and for a row
    /// held in a <see cref="RowSpan"/> group it names the rows of the group, with no cell; see
    /// <see cref="TableElement"/>.
    /// </exception>
    public EdgeInsets Padding { get; init; } = new EdgeInsets(4, 6, 4, 6);

    /// <summary>Optional background fill color for the cell.</summary>
    /// <remarks>
    /// Null means no fill. A colour is stored as given. Channels are not clamped. A channel that is not
    /// finite is refused where it is written; see <see cref="Document"/> and <see cref="ColorRgb"/>. A
    /// cell that a span from an earlier row pushes past the last column is never drawn, so its colour is not written and not refused. <b>Do not pass</b> a non-finite channel: 3.0
    /// refuses it.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save, when a channel is <c>NaN</c> or infinite and the cell is drawn. The message
    /// says PDF does not support NaN or Infinity as a real number. A finite channel outside 0 to 1 is
    /// not refused.
    /// </exception>
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

    /// <summary>The runs the cell was built from, or null for a cell built from a string.</summary>
    /// <remarks>
    /// The list is a read-only copy of the sequence given to
    /// <see cref="Cell(IEnumerable{TextRun})"/>, so changing that sequence afterwards changes
    /// nothing here. An empty sequence reads as one empty run in
    /// <see cref="TextStyle.Default"/>, which the cell does not draw in: see <see cref="Style"/>.
    /// </remarks>
    public IReadOnlyList<TextRun>? Runs { get; }

    private readonly CellRunText? _runText;

    // Set by the constructor that takes runs, when one of them is a page reference.
    private readonly TextRun? _firstReference;

    /// <summary>Whether a run of the cell is a page reference, whose label is not known until the pagination is.</summary>
    internal bool HoldsReferences => _firstReference is not null;

    /// <summary>
    /// What the layout wraps, measures and draws: the runs with each reference replaced by its
    /// label. For a cell with no reference, the runs as the constructor read them.
    /// </summary>
    internal CellRunText? RunText =>
        _firstReference is { } first
            ? PageReferences.CurrentTable(first.ReferenceAnchor!).ViewOf(this).RunText
            : _runText;

    /// <summary>
    /// The text the layout reads where it reads <see cref="Content"/>: the same string, with each
    /// reference's label in it.
    /// </summary>
    internal string LaidOutContent =>
        _firstReference is { } first
            ? PageReferences.CurrentTable(first.ReferenceAnchor!).ViewOf(this).Content
            : Content;

    /// <summary>Creates a cell with the given text content.</summary>
    /// <remarks>
    /// A null <paramref name="content"/> is stored, and the cell draws no text. The save refuses it with
    /// <see cref="ArgumentNullException"/> (<c>ParamName</c> <c>content</c>) when it sizes a column
    /// automatically, which it does for a column without a positive finite width in
    /// <see cref="TableElement.ColWidths"/>, and then for every cell of the table; a table where every
    /// column has such a width draws the cell empty. <b>Do not pass</b> a null: 3.0 refuses
    /// it here.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
    /// this call, when <paramref name="content"/> is <see langword="null"/> and a column is sized
    /// automatically. The document cannot be saved again.
    /// </exception>
    /// <inheritdoc cref="Paragraph.Add(string, TextStyle)" path="/exception[@cref='T:System.ArgumentException']"/>
    public Cell(string content)
    {
        Content = content;
    }

    /// <summary>Creates a cell from a sequence of runs, each with its own style.</summary>
    /// <remarks>
    /// <b>A null sequence, a null run and a run with a null style are refused here.</b>
    /// The first two throw <see cref="ArgumentNullException"/> and the third <see
    /// cref="ArgumentException"/>; <c>ParamName</c> is <c>runs</c> and the message of the last two
    /// names the index of the run. <see cref="TextRun"/> stores a null style; this constructor
    /// refuses it whatever the run's text, so an empty run needs a style too. An empty sequence is
    /// accepted and builds a cell with one empty run in <see cref="TextStyle.Default"/>, which
    /// draws no text. The sequence is copied. A <see langword="null"/> literal binds to the string
    /// constructor, not to this one.
    /// <para>The text of every run is read as one string, by the rules <see cref="Content"/> gives:
    /// a carriage return and line feed pair, a lone carriage return and a lone line feed each end a
    /// line, any other white-space control character is a space, and a space is the only break
    /// point between words. A run boundary inside a word draws no space, so a run <c>astro</c>
    /// followed by a run <c>logie</c> is one word, and the word is measured in pieces, each in the
    /// style of its run. A space is measured in the style of the run that holds it. Adjacent runs
    /// that share one <see cref="TextStyle"/> instance and hold text are one piece of text, drawn
    /// in one show operator, so a cell of such runs draws the string cell of the text they hold in
    /// that style, except that the run cell links the text when that style has a
    /// <see cref="TextStyle.LinkUri"/> and the string cell drops the link.</para>
    /// <para>Every line of the cell is as tall as the largest <see
    /// cref="TextStyle.EffectiveLeading"/> among the runs that hold text, and the first baseline
    /// lies one largest font size of those runs below the top of the cell's content, padding
    /// included. Every later baseline is one line height below the one before it, so a line that
    /// holds only small text is as tall as its neighbours and sits as low as they do. A run that
    /// holds only a line end or white space counts, as any run that holds text does.</para>
    /// <para>A run is a link through its own <see cref="TextStyle.LinkUri"/>. The annotation covers
    /// the width the run's text is drawn at on each line it is on, as tall as the line, and is cut
    /// to the cell's box. A run that draws no glyph, or whose text measures zero wide, as Symbol
    /// text does, gets none. <b>The links of a cell are not tagged</b>, whatever the document's
    /// conformance, and no exception reports it. A <see cref="TextStyle.LinkUri"/> on
    /// <see cref="Style"/> is still ignored (#475).</para>
    /// <para><b>A run that holds text is refused when its colour has a channel that is not finite
    /// or lies outside 0 to 1.</b> The constructor throws
    /// <see cref="ArgumentOutOfRangeException"/>, because ISO 32000-2:2020, 8.6.4.3 says each
    /// DeviceRGB component is a number from 0.0 to 1.0. A run counts as holding text when its text
    /// is not empty, white space and line ends included, whether or not anything of it is drawn.
    /// A font size that is not finite is refused at the save, from the style of every run that
    /// holds text. The style of a run with no text is not read. When no run holds
    /// text the cell reads <see cref="Style"/> or the table's default as the string cell does, so
    /// in a PDF/A or PDF/UA document set a style with an embedded font there; a run's own style
    /// does not reach it.</para>
    /// <para>A cell laid out unbroken, as <see cref="Content"/> describes, reads its runs as it
    /// reads a string: wrapped at spaces alone and measured with its control characters, which are
    /// left out of what is drawn. The limit <see cref="Content"/> states counts the characters of
    /// every run. <b>Do not pass</b> a cell with more lines than a document can hold and expect
    /// them drawn.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="runs"/> is <see langword="null"/> or contains <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A channel of the text colour of a run that holds text is <c>NaN</c>, infinite, below 0 or
    /// above 1. <c>ParamName</c> is <c>runs</c>, <c>ActualValue</c> is the channel's value, and the
    /// message names the run's index and the channel.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A run's <see cref="TextRun.Style"/> is <see langword="null"/>. It is also raised from
    /// <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// constructor, when a run's text holds an unpaired surrogate and is measured in an embedded
    /// font, and when a run's font is an embedded one that belongs to another document; see
    /// <see cref="TextStyle.FontRef"/>. A pair split between two runs that share one style
    /// instance is joined and does not throw; split between runs of different instances, each half
    /// is unpaired and it throws.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style of a run that holds text has a font size that is not
    /// finite; see <see cref="TextStyle.FontSize"/>. The message names the row and the cell. It is
    /// also raised when <see cref="Document.StrictDiagnostics"/> is set and either text of the cell
    /// is written as glyph 0, or the document has a conformance claim and the cell writes
    /// Helvetica or another Standard 14 font.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when a run's style holds a <see cref="VellumPdf.Fonts.Standard14"/>
    /// value the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    [OverloadResolutionPriority(-1)]
    public Cell(IEnumerable<TextRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var copy = TextRunList.Copy(runs, nameof(runs), TextStyle.Default);
        for (var i = 0; i < copy.Count; i++)
            if (copy[i].Text.Length > 0)
                LayoutValidation.ValidateRunColor(copy[i].Style.Color, i, nameof(runs));
        Runs = copy.AsReadOnly();
        Content = TextRunList.Concat(copy);
        _runText = new CellRunText(copy);
        _firstReference = copy.Find(static r => r.IsReference);
    }
}
