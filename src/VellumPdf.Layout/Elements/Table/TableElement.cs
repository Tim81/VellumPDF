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
/// <remarks>
/// A table that resolves to no columns is refused at save; see <see cref="Rows"/>. A table whose
/// rows are all headers has no repeatable header and draws each row once; see
/// <see cref="AddRow(bool)"/>.
/// The table is one element for the page limit described on <see cref="IRenderer.Layout"/>: a
/// table that needs more than <b>50,000</b> page continuations makes the save throw.
/// <para>A row that is taller than a page is cut between its lines and continues on the next
/// page. It starts on the page the rows before it are on when at least one line of it fits
/// there. Each cell is cut at its own line, every slice is drawn as a whole cell with its
/// background, border and padding, and the repeated header rows are drawn above each slice. A
/// tagged table gets one <c>Table</c> per page, so the cells of a cut row appear as a <c>TD</c>
/// on each page it spans, and a cell with no line on a page is an empty <c>TD</c>. A row that
/// fits a page of its own is never cut: it moves to the next page whole.</para>
/// <para><b>These are refused, and never cut.</b> <see cref="Document.Save(System.IO.Stream)"/>
/// throws <see cref="InvalidOperationException"/> whose message starts with the standard
/// too-tall text and ends with a sentence naming the rows, for: header rows that together do not
/// fit a page, because they repeat on every page; a row that declares a
/// <see cref="Cell.RowSpan"/> above one, or lies under such a span, and does not fit a page,
/// because the group is held together; and a row with a cell whose vertical padding and one line
/// do not fit the page under the repeated header rows, because that cell would make no progress.
/// A table of nothing but header rows has no repeated header, so its rows are cut like any other.
/// Rows are cut only when the document lays the table out: a
/// <see cref="Rendering.Table.TableRenderer"/> given a context built through the public
/// constructor of <see cref="LayoutContext"/> still returns <see cref="LayoutResult.Nothing()"/>
/// for a row taller than its area.</para>
/// </remarks>
public sealed class TableElement
{
    /// <summary>Creates an empty table.</summary>
    /// <remarks>
    /// A save before a row holding a cell is added throws <see cref="InvalidOperationException"/>;
    /// see <see cref="Rows"/>.
    /// </remarks>
    public TableElement() { }

    private readonly List<Row> _rows = [];
    private readonly List<double> _colWidths = [];   // 0 = auto

    /// <summary>Text style applied to cells that have no explicit style.</summary>
    /// <remarks>
    /// Null means <see cref="TextStyle.Default"/> for cells that have no style of their own.
    /// Refusals on size, leading, font and text colour are on <see cref="TextStyle"/> and are
    /// raised from the save. A <see cref="TextStyle.LinkUri"/> in it is ignored.
    /// <para>A table with no default cell style does not take
    /// <see cref="Document.SetDefaultFont"/>; its cells take <see cref="TextStyle.Default"/>. The
    /// font is then Helvetica, which is not embedded. While <see cref="Document.Conformance"/> is
    /// not <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses an element that falls back to Helvetica under either
    /// claim.</para>
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
    public TextStyle? DefaultCellStyle { get; init; }

    /// <summary>Width of the table border lines, in points.</summary>
    /// <remarks>
    /// A non-finite width is refused, and so is a negative one. <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> and names the table. The non-finite width is refused
    /// when the table is laid out; the negative one where a cell border is drawn, so a table that is
    /// laid out and not drawn, such as one with an infinite top margin, saves with a negative width.
    /// <para><b>Attention</b>: zero does <b>not</b> hide the borders. It asks the device for its
    /// thinnest line, so the grid is still drawn. Each border stays one device pixel while
    /// everything around it shrinks, so the grid reads as proportionally heavier the further the
    /// page is scaled down.</para>
    /// <para>There is at present <b>no</b> way to draw a table without a grid. Every cell is
    /// stroked unconditionally and <see cref="BorderColor"/> is not nullable. If you need a
    /// gridless table, the nearest you can get is a border colour matching the page.</para>
    /// <para>A negative width is refused as well. ISO 32000-2, 8.4.3.2 says a line width
    /// "shall be a non-negative number", so a negative one would write an operand the standard forbids. The save
    /// throws <see cref="InvalidOperationException"/>, naming the table, the width and the clause.
    /// Zero stays accepted; see <see cref="LineSeparator.LineWidth"/> for why it is a caller
    /// warning and not a refusal.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this
    /// property, when the width is not finite, or is negative and a cell border is drawn. The
    /// message names the table.
    /// </exception>
    public double BorderWidth { get; init; } = 0.5;

    /// <summary>Color of the table border lines.</summary>
    /// <remarks>
    /// Stored as given. Channels are not clamped. A channel that is not finite is refused where it is
    /// written; see <see cref="Document"/> and <see cref="ColorRgb"/>. <b>Do not pass</b> one: 3.0
    /// refuses it.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save, when a channel is <c>NaN</c> or infinite and a border is drawn. The message
    /// says PDF does not support NaN or Infinity as a real number. A finite channel outside 0 to 1 is
    /// not refused.
    /// </exception>
    public ColorRgb BorderColor { get; init; } = ColorRgb.Black;

    /// <summary>Outer margins applied around the whole table.</summary>
    /// <inheritdoc cref="Paragraph.Margins" path="/remarks"/>
    /// <inheritdoc cref="Paragraph.Margins" path="/exception"/>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Whether the table starts a new page, or the next right-hand page.</summary>
    /// <remarks>
    /// <b>An undefined value is refused.</b> A number <see cref="PageBreakMode"/> does not name
    /// makes this <c>init</c> accessor throw <see cref="ArgumentOutOfRangeException"/>. The
    /// default, <see cref="PageBreakMode.None"/>, starts the element where the previous one ended.
    /// <para>It is honoured when the table is added with <see cref="Document.Add(TableElement)"/>,
    /// or when its <see cref="Rendering.Table.TableRenderer"/> is added with
    /// <see cref="Document.Add(IRenderer)"/> or
    /// <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>. It applies to the top of the table:
    /// the overflow of a split table never breaks again, and a renderer built to continue the table
    /// from a later row breaks like the first piece when it is added directly. See
    /// <see cref="PageBreakMode"/> for what else is ignored and how parity is counted, and
    /// <see cref="PageBreak"/> for how breaks combine.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is not one of
    /// <see cref="PageBreakMode.None"/>, <see cref="PageBreakMode.Page"/> and
    /// <see cref="PageBreakMode.Recto"/>.
    /// </exception>
    public PageBreakMode BreakBefore
    {
        get => _breakBefore;
        init => _breakBefore = PageBreakModes.Validate(value);
    }

    private PageBreakMode _breakBefore;

    /// <summary>The rows in the table, in render order.</summary>
    /// <remarks>
    /// A table that resolves to no columns is refused at save: no rows, or every row empty.
    /// A mix of empty rows and rows that have cells is not that case.
    /// <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> when there is nothing to draw.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save, when this collection is empty or every row has no cells.
    /// </exception>
    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>Configured column widths in points; a value of 0 means auto-size.</summary>
    /// <remarks>
    /// Entry <c>i</c> sets column <c>i</c>. A missing, zero, negative or non-finite entry means
    /// auto. Entries past the column count are ignored.
    /// <para>Each auto column first takes a share of the width the explicit entries leave, weighted
    /// by its content and at least its longest word plus the cell's horizontal padding. When the
    /// explicit widths exceed the width the auto columns leave them, they are all scaled by one
    /// ratio to that width, even when they fit the available width on their own. With no auto
    /// column, that width is the whole available width. So one very large entry leaves the other
    /// explicit columns near zero: <c>1e200, 60, 70</c> resolves to about 451, 0 and 0 on an A4
    /// page with the default margins, and nothing reports it. That is a contract tightening for 3.0, not
    /// a refusal in a patch, because the table still renders. Entries whose sum overflows to
    /// infinity are accepted, and every explicit column is written at width 0 while an auto column
    /// keeps its content width, so cells that start at the same x are drawn on top of one
    /// another. <b>Do not pass</b> widths whose sum overflows. Nothing reports
    /// it, because <see cref="Document.Diagnostics"/> has no code for it yet, and 3.0 will throw
    /// <see cref="InvalidOperationException"/>.
    /// When the auto columns take all of the available width or more, every column,
    /// auto ones included, is scaled by one ratio instead. A positive entry under 5e-6 is kept and
    /// written as width 0. None of this is reported.</para>
    /// <para>Do not rely on scaling to fit a table. Pass widths that fit the space you give
    /// it.</para>
    /// </remarks>
    public IReadOnlyList<double> ColWidths => _colWidths;

    /// <summary>Sets the column widths (0 = auto) and returns this instance for chaining.</summary>
    /// <remarks>
    /// Replaces every width. Called with no arguments it clears them, so every column is auto. A
    /// null array is refused only after the existing widths are cleared, so they are lost. The
    /// entries follow the rules on <see cref="ColWidths"/>; none is refused here, and a set whose
    /// sum overflows to infinity is accepted, with every explicit column written at width 0; see
    /// <see cref="ColWidths"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="widths"/> is <see langword="null"/>. <c>ParamName</c> is <c>collection</c>,
    /// not <c>widths</c>.
    /// </exception>
    public TableElement SetColumnWidths(params double[] widths)
    {
        _colWidths.Clear();
        _colWidths.AddRange(widths);
        return this;
    }

    /// <summary>
    /// Appends a new row, optionally flagged as a header, and returns it. A header row repeats at
    /// the top of each continuation page only while it belongs to the table's leading contiguous
    /// run of header rows and a row that is not a header follows that run; a header row added after
    /// a data row draws once, where it occurs.
    /// </summary>
    /// <remarks>
    /// A table whose rows are all headers has no repeatable header: each row draws once, in order,
    /// and the table splits between rows like any other.
    /// </remarks>
    public Row AddRow(bool isHeader = false)
    {
        var row = new Row { IsHeader = isHeader };
        _rows.Add(row);
        return row;
    }

    /// <summary>
    /// Appends a row you built, so that its <see cref="Row.Background"/> takes effect, and returns
    /// it.
    /// </summary>
    /// <remarks>
    /// <b>A null row is refused, and so is a row whose <see cref="Row.Background"/> has a channel
    /// that is not finite or lies outside 0 to 1.</b> This call throws
    /// <see cref="ArgumentNullException"/> or <see cref="ArgumentOutOfRangeException"/>, and the
    /// table is left as it was. The range is that of ISO 32000-2:2020, 8.6.4.3, which says each
    /// DeviceRGB component is a number from 0.0 to 1.0. The check is on the value as given, not on
    /// the token written, so 1.000001 is refused although it would be written as <c>1</c>. A
    /// background of exactly 0 or 1 in a channel is accepted.
    /// <para>The table holds the row itself, not a copy. A cell added to the row afterwards is
    /// drawn, and adding the same row twice draws it twice. A row with no cells is accepted, as
    /// <see cref="AddRow(bool)"/> accepts one. <see cref="Row.IsHeader"/> is honoured as it is for
    /// <see cref="AddRow(bool)"/>. A cell's own <see cref="Cell.Background"/> is drawn in place of
    /// the row's; see <see cref="Row.Background"/> for which cells get the row's fill.</para>
    /// </remarks>
    /// <param name="row">The row to append.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="row"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A channel of <c>row.Background</c> is <c>NaN</c>, infinite, below 0 or above 1.
    /// <c>ParamName</c> is <c>row</c>, <c>ActualValue</c> is the channel's value, and the message
    /// names the channel.
    /// </exception>
    public Row Add(Row row)
    {
        ArgumentNullException.ThrowIfNull(row);
        LayoutValidation.ValidateRowBackground(row.Background, nameof(row));
        _rows.Add(row);
        return row;
    }

    /// <summary>
    /// Appends a new header row and returns it. See <see cref="AddRow(bool)"/> for when a header row
    /// repeats across continuation pages.
    /// </summary>
    /// <remarks>
    /// A table whose rows are all headers has no repeatable header: each row draws once, in order,
    /// and the table splits between rows like any other.
    /// </remarks>
    public Row AddHeaderRow()
    {
        var row = new Row { IsHeader = true };
        _rows.Add(row);
        return row;
    }
}
