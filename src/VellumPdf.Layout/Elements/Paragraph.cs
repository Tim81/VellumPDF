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
/// the start and end of each line.</para>
/// <para>A run boundary draws a space only where the text has one: a space, tab, line break or
/// other white-space character, except the no-break spaces, at the end of one run or the start of
/// the next. With none, the two runs hold one word and nothing is drawn between them, so the run
/// <c>Bold</c> followed by the run <c>er</c> is drawn as <c>Bolder</c>, and a word can change style
/// part-way. Such a word wraps as one unit, and when it is wider than its line it is cut at a
/// character, in whichever style that character has. An empty run adds nothing to a word, so it
/// does not separate its neighbours. A run ending in a carriage return and the next starting with
/// a line feed are two line breaks, because each run's line breaks are read on their own.</para>
/// <para>The pieces of one word that share one <see cref="TextStyle"/> instance are measured and
/// drawn as one piece, so a word split between runs of one instance is drawn as the same word in a
/// single run. That includes a surrogate pair split between two such runs. Pieces whose styles are
/// equal in value but are different instances stay separate fragments, each with its own text
/// position, though they land at the same positions.</para>
/// <para>A tab is drawn as one space unless <see cref="TabStops"/> holds a stop to place the text
/// after it at; see <see cref="TabStops"/>.</para>
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
    /// makes this <c>init</c> accessor throw <see cref="ArgumentOutOfRangeException"/>. The
    /// default, <see cref="PageBreakMode.None"/>, starts the element where the previous one ended.
    /// <para>It is honoured when the paragraph is added with <see cref="Document.Add(Paragraph)"/>,
    /// or when its <see cref="Rendering.ParagraphRenderer"/> is added with
    /// <see cref="Document.Add(IRenderer)"/> or
    /// <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>. It applies to the top of the
    /// paragraph: the overflow of a split paragraph never breaks again, and a renderer built to
    /// continue the paragraph from a later line breaks like the first piece when it is added
    /// directly. See <see cref="PageBreakMode"/> for what else is ignored and how parity is
    /// counted, and <see cref="PageBreak"/> for how breaks combine.</para>
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
    /// Whether the paragraph stays on the page the next element starts on. The default,
    /// <see langword="false"/>, places the paragraph without looking at what follows.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused, and a request the pages cannot hold is dropped and reported, not
    /// thrown.</b> Consecutive top-level elements that set this, and the element added after the
    /// last of them, form a group. The group is placed as it stands when each of its elements
    /// starts on the page the one before it ends on, which for an element that splits is the page
    /// of its last piece. When one does not, the whole run moves to the next page if the group
    /// holds from there. The element after the run is placed by its own rules, so its
    /// <see cref="KeepTogether"/> or <see cref="MinLinesBeforeBreak"/> decides what counts as a
    /// first piece that fits.
    /// <para>The request is dropped, and the elements are placed as if it were not set, when the
    /// run already starts at the top of a page, because moving it would leave that page empty, and
    /// when the group does not hold from the top of the next page either, as when the run ends
    /// exactly at the foot of a page and the element after it must start on another. A run that
    /// fills more than a page is not dropped for that alone. The save then lists
    /// <see cref="LayoutDiagnosticCode.KeepWithNextRelaxed"/>, a warning, against the first
    /// element of the run. When two requests conflict this one is dropped first, then
    /// <see cref="KeepTogether"/>, then the line counts: a heading that cannot share a page with a
    /// table that must stay whole stays where it is, and the table moves.</para>
    /// <para>A <see cref="PageBreak"/>, or an element whose <c>BreakBefore</c> is set, ends the
    /// group, and the element before it is placed with no constraint on what follows. The flag is
    /// read from a paragraph added with <see cref="Document.Add(Paragraph)"/>, and from a
    /// <see cref="Rendering.ParagraphRenderer"/> added with <see cref="Document.Add(IRenderer)"/>
    /// or <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>. It is ignored on the overflow
    /// of a split paragraph. A custom <see cref="IRenderer"/> sets none, and as the element after
    /// a run it is placed like any other.</para>
    /// <para>The document lays the group out once more before it places it, and once more from the
    /// next page when it does not hold where it stands below the top of a page (at the top of a
    /// page there is no later page to try, and it skips that second pass), so a document that
    /// sets this on many elements makes more layout calls; see <see cref="IRenderer.Layout"/>. The
    /// walk stops at the first element that starts on a later page than the one before it ended
    /// on. A refusal that laying out the next element raises is therefore raised while the group
    /// is laid out, before the elements ahead of it are drawn; it is the one that element raises
    /// when it is placed.
    /// <b>Do not</b> read this as keeping a whole run on one page: a paragraph that splits keeps
    /// only its last piece with the element after it, and the pieces before that fall where the
    /// pages end.</para>
    /// </remarks>
    public bool KeepWithNext { get; init; }

    /// <summary>
    /// Whether the paragraph moves whole to the next page instead of splitting across two, when it
    /// fits there. The default, <see langword="false"/>, splits it wherever the page ends.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused, and a paragraph that cannot be kept whole is split and
    /// reported.</b> A paragraph that would split below the top of a page moves to the top of the
    /// next one when it fits on an empty page. When it does not fit on an empty page, or already
    /// starts at the top of one, it splits as it would without this flag, and the save lists
    /// <see cref="LayoutDiagnosticCode.KeepTogetherRelaxed"/>, a warning, against it. Its line
    /// counts, <see cref="MinLinesBeforeBreak"/> and <see cref="MinLinesAfterBreak"/>, still apply
    /// to the pieces of a paragraph that splits. The request is checked until the paragraph has
    /// split once, so a paragraph that a line count moved to the top of the next page and that
    /// does not fit there is reported as well.
    /// <para>The flag is read where <see cref="KeepWithNext"/> is, and ignored where that is. When
    /// the paragraph would split below the top of a page, the document lays it out once more on an
    /// empty page to see whether it fits there; at the top of a page it makes no such call. A move
    /// adds the retry the document makes after any
    /// <see cref="LayoutResult.Outcome.Nothing"/>; see <see cref="IRenderer.Layout"/>.</para>
    /// </remarks>
    public bool KeepTogether { get; init; }

    /// <summary>
    /// The fewest lines of the paragraph that stay at the foot of a page before a break inside it,
    /// on the page it starts or continues on. The default, 1, lets a single line stay.
    /// </summary>
    /// <remarks>
    /// <b>A value below 1 is refused.</b> This <c>init</c> accessor throws
    /// <see cref="ArgumentOutOfRangeException"/>. There is no upper bound: a value above the
    /// number of lines the paragraph has, which no break can meet. Where the paragraph would split
    /// below the top of a page it moves to the next page, and where it would split at the top of a
    /// page it splits where the page ends and the save lists
    /// <see cref="LayoutDiagnosticCode.MinLinesRelaxed"/>.
    /// <para>Typographic traditions define <i>widow</i> and <i>orphan</i> in opposite ways, so the
    /// two counts are named for what they do. This one controls the lines left at the foot of the
    /// page, which some call orphan control, and <see cref="MinLinesAfterBreak"/> the lines carried
    /// to the next page, which some call widow control.</para>
    /// <para>When the page cannot take that many lines, the paragraph moves to the next page. When
    /// it already starts at the top of a page, the paragraph splits where the page ends, as it
    /// would with the default, and the save lists
    /// <see cref="LayoutDiagnosticCode.MinLinesRelaxed"/>, a warning, against it. When no break
    /// leaves enough lines on both sides but the page it starts on is not a page top, the whole
    /// paragraph moves to the next page, and it is reported only if the counts cannot be met there
    /// either. A paragraph that fits whole on its page is never split by this count. The count
    /// applies to every break inside the paragraph, so it holds for each page a long paragraph
    /// continues onto.</para>
    /// <para>The document reads the count for a paragraph added with
    /// <see cref="Document.Add(Paragraph)"/>, and for a <see cref="Rendering.ParagraphRenderer"/>
    /// added with <see cref="Document.Add(IRenderer)"/> or
    /// <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>. It is ignored for a paragraph that
    /// is laid out in any other way: behind a custom <see cref="IRenderer"/> that wraps a
    /// <see cref="Rendering.ParagraphRenderer"/>, and in a call to
    /// <see cref="Rendering.ParagraphRenderer.Layout"/> that you make, where as many lines as the
    /// area holds are placed. Setting it there cannot make a save throw.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is below 1.
    /// </exception>
    public int MinLinesBeforeBreak
    {
        get => _minLinesBeforeBreak;
        init => _minLinesBeforeBreak = LayoutValidation.MinLines(value, nameof(MinLinesBeforeBreak));
    }

    private int _minLinesBeforeBreak = 1;

    /// <summary>
    /// The fewest lines of the paragraph that a break inside it carries to the next page. The
    /// default, 1, lets a single line go over.
    /// </summary>
    /// <remarks>
    /// <b>A value below 1 is refused.</b> This <c>init</c> accessor throws
    /// <see cref="ArgumentOutOfRangeException"/>. There is no upper bound: a value above the number
    /// of lines the paragraph has, which no break can meet. Where the paragraph would split below
    /// the top of a page it moves to the next page, and where it would split at the top of a page
    /// it splits where the page ends and the save lists
    /// <see cref="LayoutDiagnosticCode.MinLinesRelaxed"/>.
    /// <para>When fewer lines would carry over, the break moves up until that many do, as long as
    /// <see cref="MinLinesBeforeBreak"/> lines still stay. When they cannot, the paragraph moves to
    /// the next page, or, at the top of a page, splits where the page ends and the save lists
    /// <see cref="LayoutDiagnosticCode.MinLinesRelaxed"/>, a warning. See
    /// <see cref="MinLinesBeforeBreak"/> for the naming and for where the count is read.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is below 1.
    /// </exception>
    public int MinLinesAfterBreak
    {
        get => _minLinesAfterBreak;
        init => _minLinesAfterBreak = LayoutValidation.MinLines(value, nameof(MinLinesAfterBreak));
    }

    private int _minLinesAfterBreak = 1;

    /// <summary>
    /// A name that a link or an outside reference can jump to: the save adds a named destination at
    /// the first line of this paragraph. <see langword="null"/>, the default, adds none.
    /// </summary>
    /// <remarks>
    /// <b>A name outside the rule is refused by this <c>init</c> accessor.</b> It throws
    /// <see cref="ArgumentException"/> for an empty name, one longer than 127 characters, or one
    /// with a character outside <c>A-Z a-z 0-9 . _ - :</c>. The rule is the library's own and is
    /// narrower than ISO 32000-2, 7.9.6 needs, so that a name is the same bytes in every encoding
    /// and within the limits ISO 19005-2, 6.1.13 puts on a name (127 bytes) and a string (32,767
    /// bytes). Names are compared byte by byte, so <c>Intro</c> and <c>intro</c> differ.
    /// <para><b>A repeated anchor is refused by the save, before any layout.</b>
    /// <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads throw
    /// <see cref="InvalidOperationException"/> naming the anchor and the kind and zero-based
    /// position, in the order the elements were added, of both elements that hold it. Nothing has
    /// been laid out then, so the document can be saved once the elements are replaced. The same
    /// instance added twice holds its anchor twice.</para>
    /// <para><b>An anchor on content the renderer never draws is refused after the layout.</b> The
    /// save throws <see cref="InvalidOperationException"/> naming the anchor, for example for an
    /// item of a list below the second level, which is neither laid out nor drawn (see
    /// <see cref="ListItem.Children"/>). The layout had started, so the document cannot be saved
    /// again.</para>
    /// <para>The destination is written whether or not a link refers to it, so a reader can open
    /// the file at it by name (<c>file.pdf#nameddest=name</c>). Its position is the top edge of the
    /// first placed line, at the left edge of the page, with the viewer's zoom unchanged. When the
    /// paragraph splits across pages, that is the page its first line is on. The destinations are
    /// written in the order of their names, so the order the elements were added in does not
    /// change a key, and content inserted before an anchored element moves its value and not its
    /// key.</para>
    /// <para>A custom <see cref="IRenderer"/> reports no anchors, so content it draws
    /// has none and cannot be given one. A <see cref="Rendering.ParagraphRenderer"/> built to
    /// continue a paragraph from a later line reports none either, and is refused as an anchor on
    /// content never drawn.</para>
    /// <para>A <see cref="TextStyle.LinkAnchor"/> or a
    /// <see cref="TextRun.PageReference(string, TextStyle)"/> names an anchor; the refusals and
    /// their
    /// limits are on <see cref="TextStyle.LinkAnchor"/>. A reference prints the page this
    /// element's first line is on.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from this <c>init</c> accessor when the name breaks the rule above. It is also
    /// raised from the save, not from this property, as <see cref="ArgumentOutOfRangeException"/>
    /// naming <c>top</c>, when the destination's top is not finite or lies beyond ±3.403 × 10^38,
    /// the range of a PDF real (ISO 32000-1, Table C.1): a top margin below -3.403 × 10^38 or equal
    /// to negative infinity on a paragraph or heading, and also NaN on an image. A page taller than
    /// 3.403 × 10^38 points raises it as well. A paragraph with a NaN top margin never gets that
    /// far: it is refused as too tall to fit, anchored or not. The layout had started, so the
    /// document cannot be saved again.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this property, when another element in the document holds the
    /// same anchor, or when the element is never drawn.
    /// </exception>
    public string? Anchor
    {
        get => _anchor;
        init => _anchor = AnchorNames.Validate(value);
    }

    private string? _anchor;

    /// <summary>Horizontal alignment of the paragraph text.</summary>
    /// <remarks>
    /// <see cref="HorizontalAlignment.Justify"/> stretches every line except the paragraph's last,
    /// which stays left-aligned, and a line that ends at a hard line break is stretched too. A line
    /// with no space in it is not stretched. White space is drawn as the type remarks describe; see
    /// <see cref="HorizontalAlignment.Justify"/> for trailing line breaks. A justified line in the
    /// standard-14 text faces (every standard-14 font but Symbol and ZapfDingbats) ends at the right
    /// edge, including one holding runs with different <see cref="TextStyle"/> instances; see <see cref="HorizontalAlignment.Justify"/> for Symbol and ZapfDingbats.
    /// A justified line in an embedded font ends at the right edge too, and draws every space it
    /// holds.
    /// </remarks>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>
    /// Where the text after a tab character (U+0009) is placed. Null or empty leaves a tab drawn
    /// as a space, as it is for every paragraph without stops.
    /// </summary>
    /// <remarks>
    /// <b>The list must be in strictly increasing order of position, and hold no null.</b> The
    /// setter throws <see cref="ArgumentNullException"/> for a null stop and
    /// <see cref="ArgumentException"/> for a position that is not greater than the one before, so
    /// that a tab always finds the nearest stop to its right by position. The setter copies the
    /// list, so changing the one you passed afterwards has no effect.
    /// <para><b>A stop beyond the content width is refused, before layout.</b> The save throws
    /// <see cref="InvalidOperationException"/> when a top-level paragraph holds a stop whose
    /// position is greater than the page width less the document margins and
    /// <see cref="Margins"/>, because the text could not be placed where the stop says. A stop at
    /// exactly that width is accepted, and is how a right-aligned page number reaches the right
    /// edge; so is one that differs from it by less than 5e-6 points, the scale the file is
    /// written at, because two ways of computing one width can differ by a rounding step. The
    /// check runs before any element is laid out, so the document can be saved again once the page
    /// size or margins are changed. A
    /// <see cref="VellumPdf.Layout.Rendering.ParagraphRenderer"/> that a renderer of your own lays
    /// out in a narrower area is not checked in advance: it treats a stop beyond that width as
    /// absent, and when the paragraph's text holds a tab in a run with a style it reports
    /// <see cref="LayoutDiagnosticCode.TabStopBeyondWidth"/>. A tab then advances as one space only
    /// when no stop within the width remains to its right.</para>
    /// <para><b>A leader needs a size it can be drawn at.</b> When a stop has a leader, any run of
    /// the paragraph with a <see cref="TextStyle.FontSize"/> of zero or less, or one so small that
    /// the leader's line width or dash lengths, which are fractions of the size, would be written
    /// as zero, makes <see cref="InvalidOperationException"/> reach you. A run with empty text,
    /// and a run with a null style, are not checked, and save at any size. The save throws it
    /// before any layout for a paragraph the document lays out itself, and
    /// <see cref="VellumPdf.Layout.Rendering.ParagraphRenderer.Layout(LayoutContext)"/> throws it
    /// for a renderer of yours. The size of a run cannot be changed after the run is built, so
    /// build the paragraph again with a larger size, or with stops that have no leader.</para>
    /// <para>The rule. A tab ends the text before it. The text after it, up to the next tab or
    /// line break, is placed at the first stop to the right of the end of the text before the
    /// tab: <see cref="TabAlignment.Left"/> starts there, <see cref="TabAlignment.Right"/> ends
    /// there, <see cref="TabAlignment.Center"/> centres there and
    /// <see cref="TabAlignment.Decimal"/> puts its first <see cref="TabStop.DecimalCharacter"/>
    /// there. The text before a tab is followed by one space (U+0020), so a reader that
    /// extracts the text sees the words apart; the space counts in the position of the end of
    /// that text. When no stop lies to the right of the pen with the space counted, the stop the
    /// text itself stayed short of is still the stop for that tab; when one does, the tab goes to
    /// that one. A stop the text has already reached or passed is skipped. Text placed at a stop
    /// that would start left of the end of the text before it starts there instead, so the two
    /// never overlap. The leader fills the gap between the two texts, a quarter of the font size
    /// clear of each, and is left out when it would be shorter than half the font size.</para>
    /// <para>A tab with no stop to its right advances the pen by one space: the space written
    /// after the text before the tab is that advance, and a tab that no text comes straight
    /// before, such as one at the start of a line or after another tab, is drawn as a space of its
    /// own. The same holds for a stop at position zero, which no tab can use, because a tab looks
    /// for a stop greater than the pen and the pen starts at zero, and for a paragraph whose
    /// stops all lie beyond the width it is laid out at.</para>
    /// <para>Where the text after the tab goes when it does not fit. Text at a
    /// <see cref="TabAlignment.Right"/>, <see cref="TabAlignment.Center"/> or
    /// <see cref="TabAlignment.Decimal"/> stop must fit as a whole. When it does not fit after the
    /// text before the tab, the paragraph checks whether it fits at the same stop on a fresh line.
    /// If it does, the line ends there (the separator space is dropped when the line ends straight
    /// after it) and the text is placed at the stop on the next line, its leader running from the
    /// left edge. If it fits at its stop nowhere, the line does not end: the text is set as
    /// ordinary text from the end of the text before the tab, so <c>Inleiding</c>, a tab and
    /// <c>1</c> at a centred stop on the right edge draw as <c>Inleiding 1</c>, with no leader.
    /// Text at a <see cref="TabAlignment.Left"/> stop needs room for its first word only. It
    /// starts at the stop and wraps word by word, and each continuation line starts at the left
    /// edge. When the first word does not fit after the text before the tab, the same check as
    /// above applies: if it fits at the stop on a fresh line, which happens when the text before
    /// the tab ran past the stop or close to it, the word is placed there with its leader from
    /// the left edge; if it fits at the stop nowhere, the text is set as ordinary text from the
    /// end of the text before the tab, with no leader. A line that ends straight after the space
    /// written for a
    /// tab drops that space. A line whose tab reached a stop and has nothing after it keeps it:
    /// <c>Name:</c> and a tab at a stop with a leader draw <c>Name: </c>.</para>
    /// <para>Positions are measured from the left edge of the paragraph's content, which is the
    /// content area with <see cref="Margins"/> taken off. A line that holds a tab placed at a stop
    /// starts at that edge whatever the <see cref="Alignment"/> is, and is not justified; a line
    /// with only tabs that use no stop follows the alignment like any other. The
    /// white-space characters other than the tab are drawn as the type remarks describe.
    /// <b>Accepted and placed differently from what the stops suggest:</b> a stop that the text has
    /// passed, a stop at position zero, a tab with no stop to its right, and text that would
    /// overlap the text before it are not reported; they are the rules above. <b>Do not pass</b> a
    /// stop at position zero: it is accepted and never used, and 3.0 will refuse it.</para>
    /// <para>A run whose style is null yields no text, so a tab in it is read as the space the
    /// type remarks describe.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// The list holds a null stop. Raised by the setter.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A position is not greater than the one before it. Raised by the setter.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from the setter, when a stop is beyond the content width, or a leader's run has a font
    /// size that cannot be drawn as described above.
    /// </exception>
    public IReadOnlyList<TabStop>? TabStops
    {
        get;
        init
        {
            if (value is null)
            {
                field = null;
                return;
            }

            var copy = new TabStop[value.Count];
            for (var i = 0; i < copy.Length; i++)
            {
                var stop = value[i] ?? throw new ArgumentNullException(
                    nameof(value), $"The tab stop at index {i} is null.");
                if (i > 0 && !(stop.Position > copy[i - 1].Position))
                {
                    var at = stop.Position.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    throw new ArgumentException(
                        $"The tab stop at index {i} is at {at}, which is not greater than the one " +
                        "before it. Stops must be in strictly increasing order.",
                        nameof(value));
                }
                copy[i] = stop;
            }
            field = Array.AsReadOnly(copy);
        }
    }

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
    /// <para>A null <paramref name="style"/> is not the style set with
    /// <see cref="Document.SetDefaultFont"/>; only <see cref="Document.Add(string, TextStyle)"/>
    /// reads that. The font is then Helvetica, which is not embedded. While
    /// <see cref="Document.Conformance"/> is not
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
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the element is
    /// written as glyph 0, or the document has a conformance claim and the element writes Helvetica
    /// or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
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
    /// embedded font; see <see cref="TextStyle.FontRef"/>. A pair split between two runs that
    /// share one style instance is joined and does not throw; split between runs of different
    /// instances, each half is unpaired and it throws.
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
    /// <para>A run whose text starts with a character that does not separate words continues the
    /// word the text before it ends with, if it ends with one, so
    /// <c>new Paragraph("astro").Add("logie")</c> draws <c>astrologie</c>. The no-break spaces
    /// U+00A0, U+202F and U+2007 do not separate words; every other white-space character does.
    /// Start the text with a space to begin a new word.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>. A pair split between this run and the one
    /// before it is joined, and does not throw, when both share one style instance.
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
