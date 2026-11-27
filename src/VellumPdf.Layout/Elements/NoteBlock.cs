// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A block that sets paragraphs apart from the text around them: a background, a border and
/// padding drawn around the paragraphs it holds. A note, a warning or a tip is one.
/// </summary>
/// <remarks>
/// <b>The block holds paragraphs and nothing else.</b> <see cref="Add(Paragraph)"/> throws
/// <see cref="ArgumentNullException"/> for a null paragraph and <see cref="ArgumentException"/>
/// for a paragraph whose <see cref="Paragraph.BreakBefore"/> is set, because a page break inside a
/// box has no meaning. A list, a table, a heading and a preformatted block have no overload, so
/// they cannot be added; put them outside the block. The paragraphs are laid out as they are at
/// the top level, with their own runs, links, page references, alignment, tab stops,
/// <see cref="Paragraph.Margins"/>, <see cref="Paragraph.KeepTogether"/>,
/// <see cref="Paragraph.KeepWithNext"/> and line counts, in the width the border and the padding
/// leave. <see cref="Document.SetDefaultFont"/> does not apply to them.
/// <para><b>The box.</b> The block is as wide as the content box. The border lies inside that
/// width, so the text starts at the block's left edge plus <see cref="BorderWidth"/> plus the
/// left edge of <see cref="Padding"/>. The background fills the whole block, border included. A
/// border is <see cref="BorderWidth"/> wide on every side it draws, in <see cref="BorderColor"/>.
/// Between two paragraphs the block adds only the <see cref="Paragraph.SpaceAfter"/> of the first;
/// the last paragraph's gap is dropped, because <see cref="SpaceAfter"/> sets the gap after the
/// block.</para>
/// <para><b>When the block splits.</b> It splits between lines, where a paragraph inside it would
/// split, and the background and the left and right border continue on every page it reaches.
/// The top border and the top padding are drawn on the first page only, and the bottom border and
/// the bottom padding on the last page only, so the frame is open at a page break and reads as one
/// box cut by the page. The last page always holds at least one line of text with the bottom
/// padding, so the block never ends on a page that holds only padding. A paragraph that
/// <see cref="Paragraph.KeepTogether"/> moves whole to the next page, when it fits there and is
/// not the first in its piece. The line counts <see cref="Paragraph.MinLinesBeforeBreak"/> and
/// <see cref="Paragraph.MinLinesAfterBreak"/> hold inside the block, and a count that no break
/// can meet is dropped without a report. A paragraph whose <see cref="Paragraph.KeepWithNext"/>
/// is set moves to the next page with the paragraph after it when that one cannot start on the
/// same page, unless it is the first of its piece.</para>
/// <para><b>Tagging.</b> In a tagged document the block is one structure element that holds the
/// structure elements of its paragraphs, also when it spans pages: <c>Aside</c> in a document
/// whose <see cref="Document.Conformance"/> is <see
/// cref="VellumPdf.Document.PdfConformance.None"/>,
/// which is written as PDF 2.0, and <c>Div</c> under every conformance claim, which is written as
/// PDF 1.7, where <c>Aside</c> is not defined. The background and the border are drawn as an
/// artifact, so they are outside the structure tree and outside extracted text. The block is
/// never tagged as a table.</para>
/// <para><b>Refused at the save, before any layout.</b> <see cref="InvalidOperationException"/>
/// is thrown, and the document stays saveable once you have changed what the message names, when
/// the block holds no paragraph, and when a paragraph has a tab stop beyond the width the border
/// and the padding leave. A border and padding that leave no width, or no room for one line on an
/// empty page, are refused by the save as an element that is too tall to fit.</para>
/// <para><b>Do not pass</b> a colour channel that is not a number from 0 to 1 (ISO 32000-2:2020,
/// 8.6.8) to <see cref="Background"/> or <see cref="BorderColor"/>. It is accepted today and
/// written as given, and a channel that is not finite is refused by the canvas when the colour is
/// drawn; a later major version refuses both from the setter.</para>
/// </remarks>
public sealed class NoteBlock
{
    private readonly List<Paragraph> _paragraphs = [];
    private ColorRgb? _background;
    private ColorRgb _borderColor = ColorRgb.Black;
    private double _borderWidth;
    private EdgeInsets _padding = EdgeInsets.Zero;
    private PageBreakMode _breakBefore;
    private string? _anchor;
    private double _spaceAfter;

    /// <summary>The paragraphs in the order they are drawn.</summary>
    /// <remarks>
    /// Never null and never holds a null. The list reflects later calls to <c>Add</c>, so a
    /// paragraph added after the block went into a document is read by the save.
    /// </remarks>
    public IReadOnlyList<Paragraph> Paragraphs => _paragraphs;

    /// <summary>Creates a block that holds no paragraph.</summary>
    /// <remarks>
    /// A block with no paragraph is refused by the save, before any layout, so add at least one
    /// with <see cref="Add(Paragraph)"/> before saving.
    /// </remarks>
    public NoteBlock()
    {
    }

    /// <summary>Creates a block that holds the paragraphs, in order.</summary>
    /// <remarks>
    /// <b>A null sequence, a null paragraph and a paragraph with a page break are refused.</b> Each
    /// paragraph is checked as <see cref="Add(Paragraph)"/> checks it, and a block with a refused
    /// paragraph is not created.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="paragraphs"/> is <see langword="null"/>, or contains <see langword="null"/>;
    /// <c>ParamName</c> is <c>paragraphs</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A paragraph's <see cref="Paragraph.BreakBefore"/> is not <see cref="PageBreakMode.None"/>;
    /// <c>ParamName</c> is <c>paragraphs</c>.
    /// </exception>
    public NoteBlock(IEnumerable<Paragraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        foreach (var paragraph in paragraphs)
        {
            if (paragraph is null)
                throw new ArgumentNullException(nameof(paragraphs), "A paragraph in the sequence is null.");
            RefuseBreak(paragraph, nameof(paragraphs));
            _paragraphs.Add(paragraph);
        }
    }

    /// <summary>Appends a paragraph and returns this block for chaining.</summary>
    /// <remarks>
    /// <b>A null paragraph is refused here, and so is a paragraph that asks for a page break.</b>
    /// What the paragraph holds is checked by the save, as it is at the top level. The same
    /// instance added twice is drawn twice.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="paragraph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="paragraph"/> has a <see cref="Paragraph.BreakBefore"/> other than
    /// <see cref="PageBreakMode.None"/>. A box is not broken by a page break inside it; add a
    /// <see cref="PageBreak"/> before the block.
    /// </exception>
    public NoteBlock Add(Paragraph paragraph)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        RefuseBreak(paragraph, nameof(paragraph));
        _paragraphs.Add(paragraph);
        return this;
    }

    /// <summary>Appends a paragraph built from the text and returns this block for
    /// chaining.</summary>
    /// <remarks>
    /// Creates the paragraph with <see cref="Paragraph(string, TextStyle)"/> and adds it. A null
    /// <paramref name="style"/> gives <see cref="TextStyle.Default"/>, not the style that
    /// <see cref="Document.SetDefaultFont"/> set.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    public NoteBlock Add(string text, TextStyle? style = null) => Add(new Paragraph(text, style));

    private static void RefuseBreak(Paragraph paragraph, string paramName)
    {
        if (paragraph.BreakBefore != PageBreakMode.None)
            throw new ArgumentException(
                "A paragraph inside a note block cannot ask for a page break. Add a PageBreak before the block, " +
                "or set BreakBefore on the block.",
                paramName);
    }

    /// <summary>The fill behind the paragraphs, padding and border. <see langword="null"/>, the
    /// default, fills nothing.</summary>
    /// <remarks>
    /// Nothing is refused by this accessor. <b>Do not pass</b> a channel outside 0 to 1; see the
    /// type. The fill is drawn as an artifact on every page the block reaches.
    /// </remarks>
    public ColorRgb? Background
    {
        get => _background;
        init => _background = value;
    }

    /// <summary>The colour of the border. The default is black.</summary>
    /// <remarks>
    /// Read only when <see cref="BorderWidth"/> is above zero. <b>Do not pass</b> a channel outside
    /// 0 to 1; see the type.
    /// </remarks>
    public ColorRgb BorderColor
    {
        get => _borderColor;
        init => _borderColor = value;
    }

    /// <summary>The width of the border, in points. The default, 0, draws none.</summary>
    /// <remarks>
    /// <b>A value that is not finite or is below zero is refused.</b> This <c>init</c> accessor
    /// throws <see cref="ArgumentOutOfRangeException"/>, because a negative line width is outside
    /// the domain of ISO 32000-2:2020, 8.4.3.2. Negative zero is accepted and means 0. The border
    /// lies inside the block's width, and its inside edge is where the padding starts. A width
    /// that leaves the content no room is refused by the save, as an element too tall to fit.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is <c>NaN</c>, infinite or below zero.
    /// </exception>
    public double BorderWidth
    {
        get => _borderWidth;
        init => _borderWidth = Require(value, nameof(BorderWidth));
    }

    /// <summary>The space between the border and the paragraphs. The default is none.</summary>
    /// <remarks>
    /// <b>An edge that is not finite or is below zero is refused.</b> This <c>init</c> accessor
    /// throws <see cref="ArgumentOutOfRangeException"/>, so the exception is raised by the
    /// statement that set the value. The top edge is applied on the first page the block reaches
    /// and the bottom edge on the last; the left and right edges on every page. Padding that leaves
    /// the content no width, or no room for one line on an empty page, is refused by the save as
    /// an element too tall to fit.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when an edge is <c>NaN</c>, infinite or below zero.
    /// </exception>
    public EdgeInsets Padding
    {
        get => _padding;
        init
        {
            Require(value.Top, "Padding.Top");
            Require(value.Right, "Padding.Right");
            Require(value.Bottom, "Padding.Bottom");
            Require(value.Left, "Padding.Left");
            _padding = value;
        }
    }

    private static double Require(double value, string name) =>
        double.IsFinite(value) && value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(
                name,
                value,
                string.Create(CultureInfo.InvariantCulture, $"{name} must be finite and not negative."));

    /// <summary>Whether the block starts a new page, or the next right-hand page.</summary>
    /// <remarks>
    /// <b>An undefined value is refused.</b> A number <see cref="PageBreakMode"/> does not name
    /// makes this <c>init</c> accessor throw <see cref="ArgumentOutOfRangeException"/>. The rule is
    /// that of <see cref="Paragraph.BreakBefore"/>: it applies to the top of the block, and the
    /// pieces a split block continues in never break.
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

    /// <summary>
    /// Whether the block stays on the page the next element starts on. The default,
    /// <see langword="false"/>, places the block without looking at what follows.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused, and a request the pages cannot hold is dropped and reported, not
    /// thrown.</b> The group, the order in which requests are dropped, the diagnostic and the extra
    /// layout are those of <see cref="Paragraph.KeepWithNext"/>, and the block behaves as a
    /// paragraph does under it. The flag does not stop the block splitting; set
    /// <see cref="KeepTogether"/> as well to keep it whole when it fits on a page. It is read from
    /// a block added with <see cref="Document.Add(NoteBlock)"/>, and ignored on the overflow of a
    /// split block.
    /// </remarks>
    public bool KeepWithNext { get; init; }

    /// <summary>
    /// Whether the block moves whole to the next page instead of splitting across two, when it
    /// fits there. The default, <see langword="false"/>, splits it wherever the page ends.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused, and a block that cannot be kept whole is split and reported.</b> The
    /// block is measured with its border and padding, so it moves when the whole box, not only its
    /// text, fits on an empty page. When it does not fit there, or already starts at the top of a
    /// page, it splits as it would without this flag, and the save lists
    /// <see cref="LayoutDiagnosticCode.KeepTogetherRelaxed"/>, a warning, against
    /// <see cref="LayoutElementKind.NoteBlock"/>. The rule and the extra layout are those of
    /// <see cref="Paragraph.KeepTogether"/>. This flag is the block's own; the
    /// <see cref="Paragraph.KeepTogether"/> of a paragraph inside it keeps that paragraph whole
    /// within the block, and is not reported when it cannot.
    /// </remarks>
    public bool KeepTogether { get; init; }

    /// <summary>
    /// A name that a link or an outside reference can jump to: the save adds a named destination at
    /// the top of the block. <see langword="null"/>, the default, adds none.
    /// </summary>
    /// <remarks>
    /// <b>A name outside the rule is refused by this <c>init</c> accessor.</b> The rule, the
    /// refusal of a repeated name by the save, and what a link and a page reference do with the
    /// name are those of <see cref="Paragraph.Anchor"/>, whatever the kinds of the two elements
    /// that hold a name. The destination is the top edge of the block's box, which is above the
    /// border and the top padding, on the first page the block reaches; no later piece reports it.
    /// A paragraph inside the block can hold an anchor of its own.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from this <c>init</c> accessor when the name breaks the rule on
    /// <see cref="Paragraph.Anchor"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this property, before any layout, when another element in
    /// the document holds the same anchor.
    /// </exception>
    public string? Anchor
    {
        get => _anchor;
        init => _anchor = AnchorNames.Validate(value);
    }

    /// <summary>
    /// The gap, in points, between this block and the element the document places after it. The
    /// default, 0, leaves none.
    /// </summary>
    /// <remarks>
    /// <b>A value that is not finite or is below zero is refused.</b> This <c>init</c> accessor
    /// throws <see cref="ArgumentOutOfRangeException"/>, so the exception is raised by the
    /// statement that set the value. The gap lies outside the box: the background and the border
    /// stop at the bottom padding. The rule, the keep requests, where the gap is read and its
    /// removal in 3.0 are those of <see cref="Paragraph.SpaceAfter"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is <c>NaN</c>, infinite or below zero.
    /// </exception>
    public double SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = LayoutValidation.SpaceAfter(value);
    }
}
