// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A paragraph that also registers a document bookmark (outline entry) at the
/// position where it is drawn. The bookmark title defaults to the heading text.
/// </summary>
/// <remarks>
/// Laid out by the same code as <see cref="Paragraph"/>, so wrapping and
/// <see cref="HorizontalAlignment.Justify"/> behave the same. Every heading also adds a bookmark
/// and, in a tagged document, an <c>Hn</c> structure element chosen by <see cref="Level"/>.
/// <para>A heading holds one string in one style, or a sequence of <see cref="TextRun"/>s that
/// style and link parts of it, as a <see cref="Paragraph"/> does; see <see cref="Runs"/>. Runs
/// are drawn as the <see cref="Paragraph"/> remarks describe, including that a run boundary
/// draws a space only where the text has one. However many runs it holds, a tagged document gets
/// one structure element for the heading.</para>
/// </remarks>
public sealed class Heading
{
    /// <summary>Heading text (also used as the bookmark title unless <see cref="BookmarkTitle"/> is set).</summary>
    /// <remarks>
    /// Never null; the constructors refuse a null. White space is drawn as on
    /// <see cref="Paragraph"/>. An empty string draws no text, but the heading still adds a
    /// bookmark, titled by <see cref="BookmarkTitle"/> or else empty, and, in a tagged document,
    /// its structure element.
    /// <para>On a heading built from runs this is the text of every run, concatenated in order and
    /// as stored, as <see cref="Paragraph.Text"/> is. The bookmark title is that string, so the
    /// runs <c>De </c>, <c>Save</c> and <c>-methode</c> give <c>De Save-methode</c>. Nothing is
    /// added at a run boundary and nothing is collapsed, whatever is drawn there.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>. A pair split between two runs that share one
    /// style instance is joined and does not throw.
    /// </exception>
    public string Text { get; }

    /// <summary>The heading's text style.</summary>
    /// <remarks>
    /// A null style passed to the string constructor becomes 14pt Helvetica; any other is stored as
    /// given. Refusals on size, leading, font and text colour are on <see cref="TextStyle"/> and
    /// are raised from the save.
    /// <para>On a heading built from runs this is the style of the first run, as
    /// <see cref="Paragraph.Style"/> is. It only reports that run: each run is drawn in its own
    /// style, and the save does not check the style of a run that draws no text, one that is
    /// empty or holds only white space. A heading built from an empty sequence has the 14pt
    /// Helvetica style of an empty string heading.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused, or a channel of its text
    /// colour is not finite; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/>
    /// and <see cref="TextStyle.Color"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the heading's text holds an unpaired surrogate and is measured in
    /// an embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public TextStyle Style => _runs[0].Style;

    /// <summary>Outline nesting level: 0 = top-level, 1 = sub-heading, etc.</summary>
    /// <remarks>
    /// Zero-based: level 0 is tagged <c>H1</c>, level 4 is <c>H5</c>, and every level at or
    /// above 5 is tagged <c>H6</c>. That ceiling is this library's, not the format's: ISO 32000-2
    /// Table 366 defines <c>Hn</c> for any unsigned integer from 1 upward. Its NOTE 2 says
    /// <c>H7</c> can be used for a seventh-level heading. That note is informative, not a
    /// requirement.
    /// <para><b>Attention</b>: a negative level is <b>not</b> refused, and it does not clamp the
    /// way you would expect. The mapping's catch-all sends it to <c>H6</c>, the deepest tag, where
    /// you almost certainly meant the shallowest. A later major version will reject it.</para>
    /// <para>The raw value also reaches the outline builder, which hangs this heading's bookmark
    /// under the most recent earlier heading whose level is exactly one less, and puts it at the
    /// top level when there is none; see <see cref="DrawContext.AddOutlineEntry(string, int, double)"/>. So a level
    /// expresses depth only where the heading above it is one shallower: after a level 0 heading,
    /// levels 5 and 500 both land at the top level, beside it rather than under it. Every level
    /// from 5 upward is the same <c>H6</c> tag as well, so assistive technology cannot tell them
    /// apart either. Do not use a large level to express depth.</para>
    /// </remarks>
    public int Level { get; init; }

    /// <summary>
    /// Override the bookmark title. When null the heading text is used.
    /// </summary>
    /// <remarks>Null means use <see cref="Text"/>. Empty is an empty bookmark title.</remarks>
    public string? BookmarkTitle { get; init; }

    /// <summary>Whether the heading's bookmark is shown open (the default) or closed in the outline.</summary>
    /// <remarks>
    /// <b>No value is refused.</b> The value takes effect only when the bookmark gets children: a
    /// later bookmark at exactly <see cref="Level"/> + 1 whose most recent earlier bookmark at
    /// <see cref="Level"/> is this one, whether from a heading or from a custom renderer's
    /// <see cref="DrawContext.AddOutlineEntry(string, int, double, bool)"/>. Level 0 is always
    /// top-level and never has a parent, so a bookmark at level -1 never gets children: the
    /// bookmarks after it at level 0 sit beside it. Without children the bookmark has no
    /// <c>/Count</c>, because ISO 32000-2:2020, Table 151, requires it only for an item with
    /// descendants, and the value is not written. A closed bookmark writes a negative
    /// <c>/Count</c>.
    /// <para>A heading that splits across pages has one bookmark, and it carries this value.</para>
    /// </remarks>
    public bool BookmarkExpanded { get; init; } = true;

    /// <summary>Margins around the heading.</summary>
    /// <inheritdoc cref="Paragraph.Margins" path="/remarks"/>
    /// <inheritdoc cref="Paragraph.Margins" path="/exception"/>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Whether the heading starts a new page, or the next right-hand page.</summary>
    /// <remarks>
    /// <b>An undefined value is refused.</b> A number <see cref="PageBreakMode"/> does not name
    /// makes this <c>init</c> accessor throw <see cref="ArgumentOutOfRangeException"/>. The
    /// default, <see cref="PageBreakMode.None"/>, starts the element where the previous one ended.
    /// <para>It is honoured when the heading is added with <see cref="Document.Add(Heading)"/>; its
    /// renderer is internal, so there is no other route. The overflow of a split heading and the
    /// paragraph the heading draws its text with never break. See <see cref="PageBreakMode"/> for
    /// what else is ignored and how parity is counted, and <see cref="PageBreak"/> for how breaks
    /// combine.</para>
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

    /// <summary>
    /// Whether the heading stays on the page the next element starts on. The default,
    /// <see langword="false"/>, places the heading without looking at what follows.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused, and a request the pages cannot hold is dropped and reported, not
    /// thrown.</b> The group, the order in which requests are dropped, the diagnostic and the extra
    /// layout are those of <see cref="Paragraph.KeepWithNext"/>.
    /// <para>The flag is read from a heading added with <see cref="Document.Add(Heading)"/>; its
    /// renderer is internal, so there is no other route. It is ignored on the overflow of a split
    /// heading.</para>
    /// </remarks>
    public bool KeepWithNext { get; init; }

    /// <summary>
    /// Whether the heading moves whole to the next page instead of splitting across two, when it
    /// fits there. The default, <see langword="false"/>, splits it wherever the page ends.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused, and a heading that cannot be kept whole is split and reported.</b> A
    /// heading that would split below the top of a page moves to the top of the next one when it
    /// fits on an empty page. When it does not fit on an empty page, or already starts at the top
    /// of one, it splits as it would without this flag, and the save lists
    /// <see cref="LayoutDiagnosticCode.KeepTogetherRelaxed"/>, a warning, against it. A heading
    /// has no line counts, so one that splits is cut where the page ends.
    /// <para>The flag is read from a heading added with <see cref="Document.Add(Heading)"/>, and
    /// ignored on the overflow of a split heading. The extra layout it costs is that of
    /// <see cref="Paragraph.KeepTogether"/>.</para>
    /// </remarks>
    public bool KeepTogether { get; init; }

    /// <summary>
    /// A name that a link or an outside reference can jump to: the save adds a named destination at
    /// the first line of this heading. <see langword="null"/>, the default, adds none.
    /// </summary>
    /// <remarks>
    /// The rule, the refusals and the position are those of <see cref="Paragraph.Anchor"/>. A
    /// heading that splits across pages is anchored on the page its first line is on.
    /// </remarks>
    /// <inheritdoc cref="Paragraph.Anchor" path="/exception"/>
    public string? Anchor
    {
        get => _anchor;
        init => _anchor = AnchorNames.Validate(value);
    }

    private string? _anchor;

    /// <summary>Horizontal alignment of the heading text.</summary>
    /// <remarks>
    /// Same as <see cref="Paragraph.Alignment"/>.
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

    /// <summary>Creates a heading with the given text and optional style (defaults to 14pt).</summary>
    /// <remarks>
    /// A null <paramref name="style"/> becomes 14pt Helvetica. A null <paramref name="text"/> is
    /// refused here. An empty string is not
    /// null and is accepted. The heading holds one run, <see cref="Runs"/>, of that text and style.
    /// <para>A null <paramref name="style"/> is not the style set with
    /// <see cref="Document.SetDefaultFont"/>. The font is then Helvetica, which is not embedded.
    /// While <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses an element that falls back to Helvetica under either
    /// claim.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <inheritdoc cref="Paragraph.Paragraph(string, TextStyle)" path="/exception[@cref='T:System.ArgumentException' or @cref='T:System.IndexOutOfRangeException' or @cref='T:System.InvalidOperationException']"/>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the element is
    /// written as glyph 0, or the document has a conformance claim and the element writes Helvetica
    /// or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
    /// </exception>
    public Heading(string text, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        _runs = [new TextRun(text, style ?? DefaultStyle())];
    }

    /// <summary>Creates a heading from a sequence of runs, each with its own style.</summary>
    /// <remarks>
    /// <b>A null sequence, a null run and a run with a null style are refused here.</b> The first
    /// two throw <see cref="ArgumentNullException"/> and the third <see cref="ArgumentException"/>;
    /// <c>ParamName</c> is <c>runs</c> and the message of the last two names the index of the run.
    /// <see cref="TextRun"/> stores a null style, and a paragraph saves one on a run that draws no
    /// text; this constructor refuses it whatever the run's text, so an empty run needs a style
    /// too. An empty sequence is accepted and builds a heading with one empty run in 14pt
    /// Helvetica, as an empty string does. The sequence is copied: changing it afterwards changes
    /// nothing. A <see langword="null"/> literal binds to the string constructor, not to this one.
    /// <para>Each run is drawn in its own style, and <see cref="Style"/> does not style the others.
    /// A run is a link only through its own <see cref="TextStyle.LinkUri"/>. The annotation
    /// covers that run and also a space that opens the next run, because the space belongs to the
    /// run before it, as in a <see cref="Paragraph"/>.</para>
    /// <para>The style of a run that draws no text, one that is empty or holds only white space,
    /// is not checked by the save: a size or colour it would refuse on a run that draws text does
    /// not throw here.</para>
    /// <para>A tagged document gets one <c>Hn</c> structure element, and <see cref="Language"/>
    /// applies to all of the heading. <b>Per-run language is not written.</b> Do not build a
    /// heading from runs in other languages expecting each to be marked as such.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="runs"/> is <see langword="null"/> or contains <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A run's <see cref="TextRun.Style"/> is <see langword="null"/>. It is also raised from
    /// <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// constructor, when a run's text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>. A pair split between two runs that share one
    /// style instance is joined and does not throw.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style of a run that draws text has its size or leading
    /// refused, or a channel of its text colour is not finite; see
    /// <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and
    /// <see cref="TextStyle.Color"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when a run's style holds a <see cref="VellumPdf.Fonts.Standard14"/>
    /// value the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    [OverloadResolutionPriority(-1)]
    public Heading(IEnumerable<TextRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = TextRunList.Copy(runs, nameof(runs), DefaultStyle());
        Text = TextRunList.Concat(_runs);
    }

    /// <summary>The runs the heading is drawn from, in order.</summary>
    /// <remarks>
    /// Never empty and never holds a null, and every run has a style. A heading built from a
    /// string holds one run of that text in <see cref="Style"/>. The list is a read-only view of a
    /// copy: changing the sequence the constructor was given does not change it, and a cast of
    /// this property cannot change it either.
    /// <para><b>Per-run language is not written.</b> Every run is part of the one structure element
    /// of the heading, and <see cref="Language"/> is the only language it carries.</para>
    /// </remarks>
    public IReadOnlyList<TextRun> Runs => _runs.AsReadOnly();

    private readonly List<TextRun> _runs;

    private static TextStyle DefaultStyle() => new() { FontSize = 14 };

    internal string ResolvedBookmarkTitle => BookmarkTitle ?? Text;
}
