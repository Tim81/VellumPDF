// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A block of text. Supports uniform style (single run) and mixed-style inline runs.
/// Wraps text across lines and paginates automatically.
/// </summary>
/// <remarks>
/// Refusals on size, leading, font and text colour are on <see cref="TextStyle"/> and are raised
/// from the save. Null text and a null run are refused where they are passed, with
/// <see cref="ArgumentNullException"/>; see the constructors and <see cref="Add"/>.
/// <para>Text is laid out word by word. A carriage return, a line feed, or a carriage return
/// followed by a line feed starts a new line. One before the first word leaves an empty first line;
/// at the end of the text the first trailing break is dropped and each further one adds an empty
/// line. A break right after a word too wide for its line adds an empty line too, whether or not
/// the break is the last. Any other sequence of white space, except the no-break spaces U+00A0,
/// U+202F NARROW NO-BREAK SPACE and U+2007 FIGURE SPACE, is drawn as one space, and is dropped at
/// the start and end of each line. The boundary between two runs is
/// drawn as a space too, unless a line break falls on it, so a word cannot change style part-way:
/// the run <c>Bold</c> followed by the run <c>er</c> is drawn as <c>Bold er</c>.</para>
/// <para>A line cannot wrap at a no-break space, so
/// <c>Pourquoi ?</c> written with U+202F before the question mark does not wrap between the two.
/// The no-break spaces are the three white-space characters that UAX #14 puts in its GL class, and
/// the only ones; a tab, an en or em space and a thin space still break. In a Standard-14 font
/// U+202F and U+2007 are drawn as U+00A0, which WinAnsiEncoding has and they do not, so they show
/// as a space of that width and report no <see cref="Document.TextEncodingWarnings"/> entry. In an
/// embedded font they use the font's own glyph, or U+00A0's, or a plain space's, when the font
/// lacks it. They are not stretched by <see cref="HorizontalAlignment.Justify"/>, as U+00A0 never
/// was.</para>
/// <para>A paragraph whose text holds no character other than white space, where the no-break
/// spaces count as characters other than white space, is laid out at the leading of
/// <see cref="TextStyle.Default"/>, whatever its style: one line for each line break it holds, and
/// one when it holds none. Its size and leading are neither used nor checked.</para>
/// </remarks>
public sealed class Paragraph
{
    private readonly List<TextRun> _runs;

    // ── Back-compat single-run properties ────────────────────────────────────

    /// <summary>The text of every run, concatenated in order.</summary>
    /// <remarks>
    /// On a single-run paragraph this is that run's text. This is the text as stored; the type
    /// remarks say how its white space and run boundaries are drawn. It never throws: no run can be
    /// null, because the constructor and <see cref="Add"/> refuse one.
    /// </remarks>
    public string Text => _runs.Count == 1 ? _runs[0].Text : string.Concat(_runs.Select(r => r.Text));

    /// <summary>The style of the first (or only) run.</summary>
    /// <remarks>
    /// Reads the first run only. It never throws, for the reason given on <see cref="Text"/>.
    /// </remarks>
    public TextStyle Style => _runs.Count > 0 ? _runs[0].Style : TextStyle.Default;

    /// <summary>All inline text runs in this paragraph (always at least one entry).</summary>
    /// <remarks>
    /// Never empty: a zero-run input becomes one empty run. No entry is null; a null run is refused
    /// by the constructor.
    /// </remarks>
    public IReadOnlyList<TextRun> Runs => _runs;

    /// <summary>Margins around the paragraph.</summary>
    /// <remarks>
    /// A non-finite edge is not checked before the layout; see <see cref="Document"/>. The save throws <see cref="ArgumentException"/> from
    /// <see cref="VellumPdf.Canvas.PdfCanvas"/> only where it would write the value.
    /// <b>Do not pass</b> a
    /// non-finite edge: 3.0 refuses it.
    /// <para><b>Attention</b>: no other edge is checked. Each finite edge is taken off the area this
    /// element is given, and the element is laid out in whatever box is left, even when that box is
    /// empty or inverted. A negative edge, or edges wider than the area, can therefore make the save throw an exception
    /// about something else, re-wrap, move or mirror the content, or leave the element off the page. The bottom edge
    /// only limits that box: it adds no space before the next element. A negative top edge can also move the elements
    /// placed after this one.</para>
    /// <para>Do not pass a negative edge. 3.0 will refuse it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the box the edges leave is too small for the element, in
    /// which case the message says the element is too tall to fit on a page and does not name the
    /// margins.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when a non-finite edge, or finite edges whose arithmetic overflows, put a
    /// value the save writes at <c>NaN</c> or infinity, or leave a later element at a
    /// non-finite position, and the save writes that position outside the content stream, as a
    /// heading's bookmark or the rectangle of a link from <see cref="TextStyle.LinkUri"/>. The
    /// message says PDF does not support NaN or Infinity as a real number.
    /// </exception>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Whether the paragraph starts a new page, or the next right-hand page.</summary>
    /// <remarks>
    /// <b>An undefined value is refused.</b> A number <see cref="PageBreakMode"/> does not name
    /// makes this <c>init</c> accessor throw <see cref="ArgumentOutOfRangeException"/>. The default,
    /// <see cref="PageBreakMode.None"/>, starts the element where the previous one ended, as before.
    /// <para>The mode is read from the element the document holds. It is honoured when the element is
    /// added with <see cref="Document.Add(Paragraph)"/>, or when its built-in renderer is added with
    /// <see cref="Document.Add(IRenderer)"/>; it is not honoured when the element sits inside a list
    /// item or a table cell. It applies to the top of the element, so on a split element only the
    /// first piece is affected. A break at the start of the document, or straight after another
    /// break, adds no page; see <see cref="Elements.PageBreak"/> for how breaks combine and
    /// <see cref="PageBreakMode.Recto"/> for the blank page.</para>
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

    /// <summary>Horizontal alignment of the paragraph text.</summary>
    /// <remarks>
    /// <see cref="HorizontalAlignment.Justify"/> stretches every line except the paragraph's last,
    /// which stays left-aligned, and a line that ends at a hard line break is stretched too. A line
    /// with no space in it is not stretched. White space is drawn as the type remarks describe; see
    /// <see cref="HorizontalAlignment.Justify"/> for trailing line breaks. A justified line in the
    /// standard-14 text faces (every standard-14 font but Symbol and ZapfDingbats) ends at the right
    /// edge, including one holding runs with different <see cref="TextStyle"/> instances; see <see cref="HorizontalAlignment.Justify"/> for Symbol and ZapfDingbats.
    /// </remarks>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>
    /// Optional per-element language override (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>).
    /// When set and the document is tagged, written as <c>/Lang</c> on the struct element.
    /// </summary>
    /// <remarks>
    /// The string is not validated: it is trimmed and written, so an ill-formed tag reaches the
    /// file. An empty or whitespace-only string is not written, and nothing is written when the
    /// document is not tagged. It is applied by <see cref="Document.Add(Paragraph)"/>; a
    /// <see cref="VellumPdf.Layout.Rendering.ParagraphRenderer"/> built directly ignores it.
    /// <para>Do not pass a tag that is not well-formed BCP 47. A later major version will refuse
    /// one.</para>
    /// </remarks>
    public string? Language { get; init; }

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>Creates a single-run paragraph with uniform style.</summary>
    /// <remarks>
    /// A null <paramref name="style"/> becomes <see cref="TextStyle.Default"/>. A null
    /// <paramref name="text"/> is refused here. An empty string is not
    /// null and is accepted.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the text holds an unpaired surrogate and is measured in an
    /// embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style's size or leading is refused, or a channel of its text
    /// colour is not finite; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/>
    /// and <see cref="TextStyle.Color"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Paragraph(string text, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        _runs = [new TextRun(text, style ?? TextStyle.Default)];
    }

    /// <summary>Creates a mixed-style paragraph from a sequence of runs.</summary>
    /// <remarks>
    /// An empty sequence becomes one empty run at <see cref="TextStyle.Default"/>. A null sequence
    /// throws <see cref="ArgumentNullException"/>, and so does a null run inside it.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="runs"/> is <see langword="null"/> or contains <see langword="null"/>; the
    /// message names the index of the run. <c>ParamName</c> is <c>runs</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when a run's text holds an unpaired surrogate and is measured in an
    /// embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when a run's style has its size or leading refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when a run's style holds a <see cref="VellumPdf.Fonts.Standard14"/>
    /// value the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Paragraph(IEnumerable<TextRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = [.. runs];
        for (var i = 0; i < _runs.Count; i++)
        {
            if (_runs[i] is null)
                throw new ArgumentNullException(nameof(runs), $"The run at index {i} is null.");
        }
        if (_runs.Count == 0)
            _runs = [new TextRun(string.Empty, TextStyle.Default)];
    }

    // ── Fluent builder ───────────────────────────────────────────────────────

    /// <summary>Appends a run with the given text and optional style. Returns this paragraph.</summary>
    /// <remarks>
    /// A null <paramref name="style"/> uses the first run's style, <see cref="Style"/>. A null
    /// <paramref name="text"/> is refused here.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the style's size or leading is refused, or a channel of its text colour
    /// is not finite; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and
    /// <see cref="TextStyle.Color"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value the
    /// enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Paragraph Add(string text, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        _runs.Add(new TextRun(text, style ?? Style));
        return this;
    }
}
