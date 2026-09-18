// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A block of text. Supports uniform style (single run) and mixed-style inline runs.
/// Wraps text across lines and paginates automatically.
/// </summary>
/// <remarks>
/// Refusals on size, leading and font are on <see cref="TextStyle"/> and are raised from the save.
/// Null text and null runs are stored and make the save throw; see the constructors and
/// <see cref="Add"/>.
/// <para>Text is laid out word by word. A carriage return, a line feed or both start a new line.
/// One before the first word leaves an empty first line; at the end of the text the first is
/// dropped and each further one adds an empty line. Any other sequence of white space, except
/// U+00A0 NO-BREAK SPACE, is drawn as one space, and is dropped at the start and end of the text.
/// The boundary between two runs is drawn as a space too, unless a line break falls on it, so a
/// word cannot change style part-way: the run <c>Bold</c> followed by the run <c>er</c> is drawn as
/// <c>Bold er</c>.</para>
/// </remarks>
public sealed class Paragraph
{
    private readonly List<TextRun> _runs;

    // ── Back-compat single-run properties ────────────────────────────────────

    /// <summary>The text of every run, concatenated in order.</summary>
    /// <remarks>
    /// On a single-run paragraph this is that run's text. This is the text as stored; the type
    /// remarks say how its white space and run boundaries are drawn. A null run in the sequence
    /// given to <see cref="Paragraph(IEnumerable{TextRun})"/> makes this getter throw.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// A run in <see cref="Runs"/> is <see langword="null"/>.
    /// </exception>
    public string Text => _runs.Count == 1 ? _runs[0].Text : string.Concat(_runs.Select(r => r.Text));

    /// <summary>The style of the first (or only) run.</summary>
    /// <remarks>
    /// Reads the first run only. A null first run makes this getter throw.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// The first run in <see cref="Runs"/> is <see langword="null"/>.
    /// </exception>
    public TextStyle Style => _runs.Count > 0 ? _runs[0].Style : TextStyle.Default;

    /// <summary>All inline text runs in this paragraph (always at least one entry).</summary>
    /// <remarks>
    /// Never empty: a zero-run input becomes one empty run. Null entries are kept.
    /// </remarks>
    public IReadOnlyList<TextRun> Runs => _runs;

    /// <summary>Margins around the paragraph.</summary>
    /// <remarks>
    /// <b>Attention</b>: no edge is checked. Each edge is taken off the area this element is given,
    /// and the element is laid out in whatever box is left, even when that box is empty, inverted
    /// or <c>NaN</c>. A negative or non-finite edge, or edges wider than the area, can therefore
    /// make the save throw an exception about something else, write a <c>NaN</c> or <c>Infinity</c>
    /// token into the content stream, re-wrap, move or mirror the content, or leave the element off
    /// the page. The bottom edge only limits that box: it adds no space before the next element. A
    /// negative or non-finite top edge can also move the elements placed after this one.
    /// <para>Do not pass a negative or non-finite edge. A later major version will refuse
    /// both.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the box the edges leave is too small for the element. The message
    /// says the element is too tall to fit on a page and does not name the margins.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when an edge leaves this element or a later one at a non-finite
    /// position, and the save writes that position outside the content stream, as a heading's
    /// bookmark or the rectangle of a link from <see cref="TextStyle.LinkUri"/>. Negative infinity
    /// can do this, and so can <c>NaN</c> on the left edge of linked text. The message says PDF
    /// does not support NaN or Infinity as a real number.
    /// </exception>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Horizontal alignment of the paragraph text.</summary>
    /// <remarks>
    /// <see cref="HorizontalAlignment.Justify"/> stretches every line except the paragraph's last,
    /// which stays left-aligned, and a line that ends at a hard line break is stretched too. A line
    /// with no space in it is not stretched; see <see cref="HorizontalAlignment.Justify"/> for
    /// which characters count. With a standard-14 font the stretch covers only half the space left
    /// on the line (#548).
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
    /// <paramref name="text"/> is stored, and the save throws when it lays out the paragraph.
    /// <para>Do not pass null text. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the text holds an unpaired surrogate and is measured in an
    /// embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Paragraph(string text, TextStyle? style = null)
    {
        _runs = [new TextRun(text, style ?? TextStyle.Default)];
    }

    /// <summary>Creates a mixed-style paragraph from a sequence of runs.</summary>
    /// <remarks>
    /// An empty sequence becomes one empty run at <see cref="TextStyle.Default"/>. A null sequence
    /// throws <see cref="ArgumentNullException"/>. A null run inside the sequence is kept.
    /// <see cref="Text"/> then throws, and so does <see cref="Style"/> when the null run is the
    /// first. The save throws for a null run anywhere in the sequence, and <see cref="Add"/>
    /// without a style throws when the null run is the first.
    /// <para>Do not put null in the sequence. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="runs"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when <paramref name="runs"/> contains <see langword="null"/>, a run
    /// whose text is null, or a run whose style is null and whose text holds a character other than
    /// white space. U+00A0 NO-BREAK SPACE counts as such a character; a tab and other white space
    /// do not.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when a run's text holds an unpaired surrogate and is measured in an
    /// embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when a run's style has its size or leading refused; see
    /// <see cref="TextStyle.FontSize"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when a run's style holds a <see cref="VellumPdf.Fonts.Standard14"/>
    /// value the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Paragraph(IEnumerable<TextRun> runs)
    {
        _runs = [.. runs];
        if (_runs.Count == 0)
            _runs = [new TextRun(string.Empty, TextStyle.Default)];
    }

    // ── Fluent builder ───────────────────────────────────────────────────────

    /// <summary>Appends a run with the given text and optional style. Returns this paragraph.</summary>
    /// <remarks>
    /// A null <paramref name="style"/> uses the first run's style, <see cref="Style"/>, which is
    /// itself null when the first run was built with a null style; the run added then has a null
    /// style too, with the consequences described on <see cref="TextRun"/>. Reading
    /// <see cref="Style"/> throws when the first run is null, so this call then throws
    /// <see cref="NullReferenceException"/> itself. A null <paramref name="text"/> is stored, and
    /// the save throws when it lays out the paragraph.
    /// <para>Do not pass null text. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="text"/> is <see langword="null"/>, or when
    /// <paramref name="style"/> and the first run's style are both null and <paramref name="text"/>
    /// holds a character other than white space; see <see cref="TextRun"/>. It is raised from this
    /// call instead when <paramref name="style"/> is null and the first run is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value the
    /// enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Paragraph Add(string text, TextStyle? style = null)
    {
        _runs.Add(new TextRun(text, style ?? Style));
        return this;
    }
}
