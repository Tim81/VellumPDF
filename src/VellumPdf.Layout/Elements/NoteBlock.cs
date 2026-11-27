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
/// <b>The block holds paragraphs, and a paragraph in it sets no keep request, no line count and no
/// margin.</b> <see cref="Add(Paragraph)"/> throws <see cref="ArgumentNullException"/> for a null
/// paragraph, and <see cref="ArgumentException"/> naming the property for a paragraph whose
/// <see cref="Paragraph.BreakBefore"/>, <see cref="Paragraph.KeepWithNext"/> or
/// <see cref="Paragraph.KeepTogether"/> is set, whose <see cref="Paragraph.MinLinesBeforeBreak"/> or
/// <see cref="Paragraph.MinLinesAfterBreak"/> is not 1, or whose <see cref="Paragraph.Margins"/> has
/// an edge other than 0. Inside a block the lines are placed in order and the block splits between
/// any two of them, as a paragraph with the default counts splits; a count of 1 asks for nothing,
/// so the default is the only value that keeps that rule. A margin would add to the room each piece
/// needs, so the block's <see cref="Padding"/> indents the paragraphs and the
/// <see cref="Paragraph.SpaceAfter"/> of each spaces them. A paragraph with no text is a blank
/// line, as at the top level. The block's own <see cref="BreakBefore"/>, <see cref="KeepWithNext"/>
/// and <see cref="KeepTogether"/> keep the meaning they have on a paragraph and are decided by the
/// document's own routines, which see the block as one element. A list, a table, a heading and a
/// preformatted block have no overload, so they cannot be added; put them outside the block. The
/// paragraphs are laid out as they are at the top level, with their own runs, links, page
/// references, alignment, tab stops and language, in the width the border and the padding leave.
/// <see cref="Document.SetDefaultFont"/> does not apply to them.
/// <para><b>The box.</b> The block is as wide as the content box. The border lies inside that
/// width, so the text starts at the block's left edge plus <see cref="BorderWidth"/> plus the
/// left edge of <see cref="Padding"/>. The background fills the whole block, border included. A
/// border is <see cref="BorderWidth"/> wide on every side, in <see cref="BorderColor"/>. Between
/// two paragraphs the block adds only the <see cref="Paragraph.SpaceAfter"/> of the first; the
/// last paragraph's gap is dropped, because <see cref="SpaceAfter"/> sets the gap after the
/// block.</para>
/// <para><b>When the block splits.</b> Every page the block reaches holds a whole box: the
/// background, all four borders, and the top and the bottom padding. The paragraphs are laid out
/// in the page's room less those borders and paddings, line by line, and split wherever that room
/// ends, so a tall padding leaves little room on each page. The box on a page ends below the last
/// line placed there.</para>
/// <para><b>When the save refuses a block as too tall.</b> The save throws
/// <see cref="InvalidOperationException"/> naming the block as too tall to fit in exactly two
/// cases: an empty page cannot hold the top and the bottom border and padding plus one line of the
/// tallest line any paragraph of the block holds, or the border and padding leave the paragraphs no
/// width. A refusal of this kind comes after the layout has started, so the document cannot be
/// saved again.</para>
/// <para><b>Tagging.</b> In a tagged document the block is one <c>Div</c> structure element that
/// holds the structure elements of its paragraphs, also when it spans pages, under every
/// <see cref="Document.Conformance"/>. The background and the border are drawn as an artifact, so
/// they are outside the structure tree and outside extracted text. The block is never tagged as a
/// table. <c>Aside</c>, the PDF 2.0 element for a callout, needs a structure namespace that this
/// library does not write yet.</para>
/// <para><b>Refused at the save, before any layout.</b> <see cref="InvalidOperationException"/>
/// is thrown, and the document stays saveable once you have changed what the message names, when
/// the block holds no paragraph, and when a paragraph has a tab
/// stop beyond the width the border and the padding leave.</para>
/// <para><b>3.0.</b> Keep requests, line counts and margins of the paragraphs inside a block are
/// refused in 2.x because the block is laid out by its own routine. 3.0 gives every block element
/// one box model (#704), which honours the rules of the elements inside it, and will accept
/// them.</para>
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
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the block still holds no paragraph.
    /// </exception>
    public NoteBlock()
    {
    }

    /// <summary>Creates a block that holds the paragraphs, in order.</summary>
    /// <remarks>
    /// <b>A null sequence, a null paragraph and a paragraph the block refuses are refused.</b> Each
    /// paragraph is checked as <see cref="Add(Paragraph)"/> checks it, and a block with a refused
    /// paragraph is not created.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="paragraphs"/> is <see langword="null"/>, or contains <see langword="null"/>;
    /// <c>ParamName</c> is <c>paragraphs</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A paragraph sets <see cref="Paragraph.BreakBefore"/>, <see cref="Paragraph.KeepWithNext"/> or
    /// <see cref="Paragraph.KeepTogether"/>, has a <see cref="Paragraph.MinLinesBeforeBreak"/> or
    /// <see cref="Paragraph.MinLinesAfterBreak"/> other than 1, or has an edge of
    /// <see cref="Paragraph.Margins"/> other than 0; <c>ParamName</c> is <c>paragraphs</c> and the
    /// message names the property.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this constructor, for the block, in the cases
    /// <see cref="Document.Add(NoteBlock)"/> lists, and for each paragraph it holds, with the
    /// exceptions <see cref="Document.Add(Paragraph)"/> documents for a paragraph, whatever their
    /// type.
    /// </exception>
    public NoteBlock(IEnumerable<Paragraph> paragraphs)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        foreach (var paragraph in paragraphs)
        {
            if (paragraph is null)
                throw new ArgumentNullException(nameof(paragraphs), "A paragraph in the sequence is null.");
            RefuseUnsupported(paragraph, nameof(paragraphs));
            _paragraphs.Add(paragraph);
        }
    }

    /// <summary>Appends a paragraph and returns this block for chaining.</summary>
    /// <remarks>
    /// <b>A null paragraph is refused here, and so is a paragraph that sets a page break, a keep
    /// request, a line count or a margin.</b> What else the paragraph holds is checked by the
    /// save, as it is at the top level. The same instance added twice is drawn twice.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="paragraph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="paragraph"/> sets <see cref="Paragraph.BreakBefore"/>,
    /// <see cref="Paragraph.KeepWithNext"/> or <see cref="Paragraph.KeepTogether"/>, has a
    /// <see cref="Paragraph.MinLinesBeforeBreak"/> or <see cref="Paragraph.MinLinesAfterBreak"/>
    /// other than 1, or has an edge of <see cref="Paragraph.Margins"/> other than 0; the message
    /// names the property. A box is split between any two lines and has no place for these; set
    /// <see cref="BreakBefore"/>, <see cref="KeepWithNext"/> or <see cref="KeepTogether"/> on the
    /// block instead, indent with <see cref="Padding"/> and space paragraphs with
    /// <see cref="Paragraph.SpaceAfter"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this call, for the block, in the cases
    /// <see cref="Document.Add(NoteBlock)"/> lists, and for the paragraph, with the exceptions
    /// <see cref="Document.Add(Paragraph)"/> documents for a paragraph, whatever their type.
    /// </exception>
    public NoteBlock Add(Paragraph paragraph)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        RefuseUnsupported(paragraph, nameof(paragraph));
        _paragraphs.Add(paragraph);
        return this;
    }

    /// <summary>Appends a paragraph built from the text and returns this block for
    /// chaining.</summary>
    /// <remarks>
    /// Creates the paragraph with <see cref="Paragraph(string, TextStyle)"/> and adds it. With
    /// <paramref name="style"/> null the paragraph takes <see cref="TextStyle.Default"/>, not the
    /// style <see cref="Document.SetDefaultFont"/> set. A null <paramref name="text"/> is refused
    /// here.
    /// <para>The default style is Helvetica, which is not embedded. While
    /// <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of a block in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. 3.0 refuses text the font cannot draw, and a Standard 14 font, under either
    /// claim.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this call, for the block, in the cases
    /// <see cref="Document.Add(NoteBlock)"/> lists, and for the paragraph, with the exceptions
    /// <see cref="Document.Add(Paragraph)"/> documents for a paragraph, whatever their type.
    /// </exception>
    public NoteBlock Add(string text, TextStyle? style = null) => Add(new Paragraph(text, style));

    private static void RefuseUnsupported(Paragraph paragraph, string paramName)
    {
        var property = paragraph.BreakBefore != PageBreakMode.None ? nameof(Paragraph.BreakBefore)
            : paragraph.KeepWithNext ? nameof(Paragraph.KeepWithNext)
            : paragraph.KeepTogether ? nameof(Paragraph.KeepTogether)
            : paragraph.MinLinesBeforeBreak != 1 ? nameof(Paragraph.MinLinesBeforeBreak)
            : paragraph.MinLinesAfterBreak != 1 ? nameof(Paragraph.MinLinesAfterBreak)
            : null;
        if (property is not null)
            throw new ArgumentException(
                $"A paragraph inside a note block cannot set {property}. The block splits between any two " +
                "lines, so a page break, a keep request or a line count has no place in it. Set the " +
                "block's own BreakBefore, KeepWithNext or KeepTogether, or leave the paragraph at its defaults.",
                paramName);

        var margins = paragraph.Margins;
        var margin = margins.Top != 0 ? "Margins.Top"
            : margins.Bottom != 0 ? "Margins.Bottom"
            : margins.Left != 0 ? "Margins.Left"
            : margins.Right != 0 ? "Margins.Right"
            : null;
        if (margin is not null)
            throw new ArgumentException(
                $"A paragraph inside a note block cannot set {margin}. The block's padding indents the " +
                "paragraphs and their SpaceAfter spaces them; use those instead.",
                paramName);
    }

    /// <summary>The fill behind the paragraphs, padding and border. <see langword="null"/>, the
    /// default, fills nothing.</summary>
    /// <remarks>
    /// <b>A channel that is not a finite number from 0 to 1 is refused.</b> This <c>init</c>
    /// accessor throws <see cref="ArgumentOutOfRangeException"/>, because ISO 32000-2:2020, 8.6.4.3
    /// gives each DeviceRGB component that range. The fill is drawn on every page the block
    /// reaches, as an artifact in a tagged document.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when a channel is <c>NaN</c>, infinite, below 0 or
    /// above 1.
    /// </exception>
    public ColorRgb? Background
    {
        get => _background;
        init => _background = value is { } c ? LayoutValidation.ValidateUnitColor(c, nameof(Background)) : null;
    }

    /// <summary>The colour of the border. The default is black.</summary>
    /// <remarks>
    /// <b>A channel that is not a finite number from 0 to 1 is refused</b>, as for
    /// <see cref="Background"/>, whether or not <see cref="BorderWidth"/> draws a border. The
    /// colour is read only when <see cref="BorderWidth"/> is above zero.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when a channel is <c>NaN</c>, infinite, below 0 or
    /// above 1.
    /// </exception>
    public ColorRgb BorderColor
    {
        get => _borderColor;
        init => _borderColor = LayoutValidation.ValidateUnitColor(value, nameof(BorderColor));
    }

    /// <summary>The width of the border, in points. The default, 0, draws none.</summary>
    /// <remarks>
    /// <b>A value that is not finite or is below zero is refused.</b> This <c>init</c> accessor
    /// throws <see cref="ArgumentOutOfRangeException"/>, because a border narrower than nothing
    /// has no place in the box. Negative zero is accepted and means 0. The border
    /// lies inside the block's width, and its inside edge is where the padding starts. A width
    /// that leaves the content no room is refused by the save, as an element too tall to fit; see
    /// the type.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is <c>NaN</c>, infinite or below zero.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this accessor, when the width leaves the paragraphs no room;
    /// see the type.
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
    /// statement that set the value. Every edge applies on every page the block reaches, so the top
    /// and the bottom edge take room from each page. Padding that leaves the paragraphs no width,
    /// or no room for the tallest line, is refused by the save as an element too tall to fit; see
    /// the type.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when an edge is <c>NaN</c>, infinite or below zero.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this accessor, when the padding leaves the paragraphs no
    /// room; see the type.
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
    /// <see cref="Paragraph.KeepTogether"/>.
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
