// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Two-phase renderer for <see cref="Paragraph"/>.
/// Phase 1 (Layout): greedy word-wrap across all runs; produces a list of LineFragment arrays.
/// Phase 2 (Draw):   emits Tm/Tj/Tw PDF operators, switching font+colour only on change.
/// Supports Standard-14 fonts (Latin-1 ShowText) and embedded TrueType (hex glyph run).
/// Justification: Tw for Standard-14 lines; explicit per-word Tm for embedded-font lines.
/// </summary>
/// <remarks>
/// The document creates one for each <see cref="Paragraph"/>. The document calls
/// <see cref="Layout"/> during a save, so the exceptions on that method reach you from the save. A
/// negative start line makes <see cref="Draw"/> throw.
/// </remarks>
public sealed partial class ParagraphRenderer : IRenderer, IPageBreakSource, IKeepSource, IPlacementSource
{
    private readonly Paragraph _para;

    // Computed during Layout:
    private List<LineFragment[]>? _lines;
    private List<LineEnd>? _lineEnds;  // one per entry of _lines

    /// <summary>
    /// True on the piece a split returns when the paragraph's line counts could not be met at the
    /// top of a page and the split was made where the page ends. The document reads it to report.
    /// </summary>
    internal bool MinLinesRelaxed { get; init; }

    /// <summary>How many times this instance wrapped its text; the cache keeps it at one.</summary>
    internal int WrapCalls { get; private set; }

    /// <summary>
    /// How many fragments this instance visited taking the line height; the wrap makes it one pass.
    /// </summary>
    internal int LeadingVisits { get; private set; }
    private double _lineHeight;      // max EffectiveLeading across the paragraph
    private bool _lineHeightTaken;   // _lineHeight is taken: max over all of _lines, or the default
    private LayoutBox _occupied;

    // How many runs with a null style yielded no word, found where the wrap is made and cached with
    // it. A split paragraph shares its wrap between its pieces, so only the piece that starts at
    // line 0 reports them; see Draw.
    private int _nullStyleRuns;

    // How many of the paragraph's tab stops lie beyond the width the wrap was made at, counted
    // only when its text holds a tab; reported once from Draw, like _nullStyleRuns.
    private int _tabStopsBeyond;

    // Pagination window: only lines[_startLine.._endLine) are in this renderer.
    private readonly int _startLine;
    private int _endLine;  // exclusive

    /// <summary>Creates a renderer for the paragraph, optionally starting at <paramref name="startLine"/> for pagination.</summary>
    /// <remarks>
    /// A null <paramref name="para"/> is refused here. Nothing else is checked. A
    /// negative <paramref name="startLine"/> makes <see cref="Layout"/> count that many lines more
    /// than the text holds, and makes <see cref="Draw"/> throw. A start past the last line lays out
    /// as <see cref="LayoutResult.Outcome.Nothing"/>, so a document saving it throws
    /// <see cref="InvalidOperationException"/> saying the element is too tall to fit. A renderer
    /// built directly ignores the paragraph's <see cref="Paragraph.Language"/>.
    /// <para>Do not pass a start outside the paragraph's lines. 3.0 will throw
    /// from this call.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="para"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised later from <see cref="Draw"/>, not from this constructor, when
    /// <paramref name="startLine"/> is negative.
    /// </exception>
    public ParagraphRenderer(Paragraph para, int startLine = 0)
    {
        ArgumentNullException.ThrowIfNull(para);
        _para = para;
        _startLine = startLine;
    }

    // Explicit, because the renderer is a public class and an implicit member would be public API.
    // The mode belongs to the element, so a renderer added through Document.Add(IRenderer) honours
    // it as well.
    PageBreakMode IPageBreakSource.BreakBefore => _para.BreakBefore;

    // Explicit, as above, and for the same reason: a renderer added through Document.Add(IRenderer)
    // keeps the element's request.
    bool IKeepSource.KeepWithNext => _para.KeepWithNext;

    bool IKeepSource.KeepTogether => _para.KeepTogether;

    // Explicit, as above. The paragraph's anchor lands where its first line does, so only the piece
    // that starts at line 0 reports it; the overflow of a split paragraph starts later and reports
    // nothing, which is what keeps the destination on the page the first line is on.
    void IPlacementSource.ReportPlacements(PlacementSink sink)
    {
        if (_startLine == 0 && _para.Anchor is { } anchor)
            sink.ReportAnchor(_para, anchor, _occupied);
    }

    // ── Phase 1: Layout ───────────────────────────────────────────────────────

    /// <summary>Word-wraps the paragraph and fits as many lines as the area allows, splitting at line boundaries on overflow.</summary>
    /// <remarks>
    /// Overflow returns <see cref="LayoutResult.Partial"/> at a line boundary. When no line fits
    /// the area it returns <see cref="LayoutResult.Outcome.Nothing"/>; the document then retries on
    /// a new page, and, if that fails too, its save throws <see cref="InvalidOperationException"/>
    /// saying the element is too tall to fit.
    /// <para><see cref="Paragraph.MinLinesBeforeBreak"/> and <see cref="Paragraph.MinLinesAfterBreak"/>
    /// are read only when <see cref="DocumentRenderer"/> places this renderer, or the pieces it
    /// returns, itself. Below the top of a page this method then returns
    /// <see cref="LayoutResult.Outcome.Nothing"/> when the counts cannot be met there. A call you make, with a context
    /// you built or made with <see cref="LayoutContext.WithArea"/>, places as many lines as the area
    /// holds and never reads them, so a renderer of yours that wraps this one cannot make the save
    /// throw by setting them.</para>
    /// <para>When the document calls this method, the exceptions it lists reach you from the
    /// save.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A run that yields text has a <see cref="TextStyle.FontSize"/> or <see cref="TextStyle.Color"/>
    /// that is refused; see those members. It is also raised when the paragraph has a tab stop with
    /// a leader and a run has a font size the leader cannot be drawn at; see
    /// <see cref="Paragraph.TabStops"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Text this method measures in an embedded font holds an unpaired surrogate; see
    /// <see cref="TextStyle.FontRef"/>. It is also raised, by <see cref="VellumPdf.Canvas.PdfCanvas"/>,
    /// when a non-finite edge of <see cref="Paragraph.Margins"/> would be written; see
    /// <see cref="Document"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// A run that yields text has a null style; <c>ParamName</c> is <c>Style</c>. See
    /// <see cref="TextRun"/>.
    /// </exception>
    public LayoutResult Layout(LayoutContext context)
    {
        var area = context.Area.Deflate(_para.Margins);
        if (area.Width <= 0)
            return LayoutResult.Nothing();

        EnsureWrapped(area.Width);

        // The line height is a property of the wrap, so it is taken once per wrap: every later
        // Layout on this renderer, and every piece a split hands on, reads the same _lines.
        // Walking the whole wrap on each call cost O(lines) per page the paragraph crossed. The
        // flag is set only after the walk completes, so a style the walk refuses is refused again
        // on the next call, as it was when the walk ran every time.
        if (!_lineHeightTaken)
        {
            TakeLineHeight();
            _lineHeightTaken = true;
        }

        var maxLines = (int)Math.Floor(area.Height / _lineHeight);
        if (maxLines <= 0)
            return LayoutResult.Nothing();

        var remaining = _lines.Count - _startLine;
        if (remaining <= 0)
            return LayoutResult.Nothing();

        if (remaining <= maxLines)
        {
            _endLine = _lines.Count;
            _occupied = area.WithHeight(remaining * _lineHeight);
            return LayoutResult.Full(_occupied);
        }

        // Partial: the lines that fit go here, unless the paragraph asks for more on one side of
        // the break. The break moves up until enough lines carry over, and what is left on this
        // page must still reach its own minimum. When it cannot, below the top of a page the
        // paragraph moves whole, and at the top of one nothing is gained by moving it, so it splits
        // where the page ends and the document reports that the count was dropped.
        var fit = maxLines;
        var relaxed = false;
        if (context.HonourLineCounts && (_para.MinLinesBeforeBreak > 1 || _para.MinLinesAfterBreak > 1))
        {
            var kept = Math.Min(maxLines, remaining - _para.MinLinesAfterBreak);
            if (kept >= _para.MinLinesBeforeBreak)
                fit = kept;
            else if (!context.AtPageTop)
                return LayoutResult.Nothing();
            else
                relaxed = true;
        }

        _endLine = _startLine + fit;
        _occupied = area.WithHeight(fit * _lineHeight);

        var overflow = new ParagraphRenderer(_para, _endLine)
        {
            _lines = _lines,
            _lineEnds = _lineEnds,
            _lineHeight = _lineHeight,
            _lineHeightTaken = true,
            _nullStyleRuns = _nullStyleRuns,
            _tabStopsBeyond = _tabStopsBeyond,
            StructType = StructType,
            ParentStructElem = ParentStructElem,
            ElementLanguage = ElementLanguage,
            RunOwner = RunOwner,
        };
        var split = new ParagraphRenderer(_para, _startLine)
        {
            _lines = _lines,
            _lineEnds = _lineEnds,
            _endLine = _endLine,
            _lineHeight = _lineHeight,
            _lineHeightTaken = true,
            _occupied = _occupied,
            _nullStyleRuns = _nullStyleRuns,
            _tabStopsBeyond = _tabStopsBeyond,
            MinLinesRelaxed = relaxed,
            StructType = StructType,
            ParentStructElem = ParentStructElem,
            ElementLanguage = ElementLanguage,
            RunOwner = RunOwner,
        };
        return LayoutResult.Partial(_occupied, split, overflow);
    }

    // Where the last successful Layout left this renderer, which Draw reads when the Layout it
    // makes for itself answers Nothing. A caller that lays the paragraph out only to measure it,
    // and must not change what a later Draw does, reads this first and puts it back (ListRenderer,
    // for the page of its own it lays each entry out on). Removed in 3.0 (#688) with that analysis.
    internal (int EndLine, LayoutBox Occupied) Placement => (_endLine, _occupied);

    internal void RestorePlacement((int EndLine, LayoutBox Occupied) placement) =>
        (_endLine, _occupied) = placement;

    // Wraps once, at the width of the first Layout call, and keeps the lines for every later call
    // and for the pieces a split returns.
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_lines), nameof(_lineEnds))]
    private void EnsureWrapped(double width)
    {
        if (_lines is not null && _lineEnds is not null) return;

        // The document checks this before any layout for its own paragraphs; a renderer of
        // yours that lays one out meets it here.
        LayoutValidation.ValidateLeaderSizes(_para);
        var stops = StopsWithin(_para, width, out _tabStopsBeyond);
        (_lines, _lineEnds) = WordWrap(_para.Runs, width, out var nullStyleRuns, stops);
        _nullStyleRuns = nullStyleRuns;
        WrapCalls++;
    }

    /// <summary>
    /// The number of lines <see cref="Layout"/> would wrap this paragraph into when it is offered a
    /// box <paramref name="boxWidth"/> wide, wrapping it now when it has not been wrapped. Zero
    /// where <see cref="Layout"/> returns Nothing for lack of width.
    /// </summary>
    internal int LineCountAt(double boxWidth)
    {
        // The subtraction Deflate makes, so the width is the one Layout would wrap at.
        var width = boxWidth - _para.Margins.Left - _para.Margins.Right;
        if (width <= 0) return 0;
        EnsureWrapped(width);
        return _lines.Count;
    }

    // Line height = max EffectiveLeading across every fragment in every line. _lineHeight is
    // written as the walk goes, so a walk that throws leaves what it had reached.
    private void TakeLineHeight()
    {
        _lineHeight = 0;
        foreach (var line in _lines!)
            foreach (var frag in line)
            {
                // A non-finite size makes every height derived from it non-finite, every height
                // comparison false, and the paragraph reports that it placed nothing -- which the
                // document renderer reads as "too tall" (#481).
                LayoutValidation.ValidateStyle(frag.Style, RunOwner);
                _lineHeight = Math.Max(_lineHeight, frag.Style.EffectiveLeading);
                LeadingVisits++;
            }
        if (_lineHeight <= 0)
            _lineHeight = TextStyle.Default.EffectiveLeading;
    }

    // ── Phase 2: Draw ─────────────────────────────────────────────────────────

    /// <summary>
    /// Structure type used when tagging is enabled. "P" for normal paragraphs.
    /// Set to "H1"…"H6" by <see cref="HeadingRenderer"/> before calling Draw.
    /// </summary>
    internal string StructType { get; set; } = "P";

    /// <summary>
    /// How each wrapped line ended, once <see cref="Layout"/> has run; null before.
    /// </summary>
    internal IReadOnlyList<LineEnd>? LineEnds => _lineEnds;

    /// <summary>
    /// When non-null, the struct elem produced by this renderer is added as a child
    /// of <see cref="ParentStructElem"/> instead of being registered at the document
    /// structure tree root. Used by table/list renderers to build nested hierarchies.
    /// </summary>
    internal PdfStructElem? ParentStructElem { get; set; }

    /// <summary>
    /// Per-element language override forwarded from <see cref="Paragraph.Language"/>
    /// (or <see cref="Heading.Language"/> via <see cref="HeadingRenderer"/>).
    /// Written as <c>/Lang</c> on the struct element when non-null.
    /// </summary>
    internal string? ElementLanguage { get; set; }

    /// <summary>
    /// What a refused run is called in the message: a paragraph's own run by default, and the heading
    /// or list item that built this renderer, so a caller is not pointed at a paragraph they did not
    /// add. Carried to both halves of a split, as the other per-element fields are.
    /// </summary>
    internal string RunOwner { get; set; } = "A paragraph run";

    /// <summary>Emits the wrapped lines as PDF text operators, applying alignment, justification, links and tagging.</summary>
    /// <remarks>See <see cref="IRenderer.Draw"/>.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The renderer was constructed with a negative start line.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A position written here is <c>NaN</c> or infinite, which
    /// <see cref="VellumPdf.Canvas.PdfCanvas"/> refuses (<c>ParamName</c> is null).
    /// </exception>
    public void Draw(DrawContext ctx)
    {
        if (_lines is null) return;

        // Reported from the drawing pass, once per paragraph: a page-counting pass lays the same
        // renderer out again, and every piece of a split shares the wrap but only the first starts
        // at line 0.
        if (_startLine == 0 && _nullStyleRuns > 0)
        {
            ctx.Diagnostics?.Report(
                LayoutDiagnosticCode.NullTextRunStyle,
                0,
                "A paragraph run with a null style yields no text and was saved without a style.",
                _nullStyleRuns,
                perElement: true);
        }

        if (_startLine == 0 && _tabStopsBeyond > 0)
        {
            ctx.Diagnostics?.Report(
                LayoutDiagnosticCode.TabStopBeyondWidth,
                0,
                "A tab stop lies beyond the width the paragraph was laid out at, so it was treated as absent.",
                _tabStopsBeyond,
                perElement: true);
        }

        var area = _occupied;
        var canvas = ctx.Canvas;
        var leading = _lineHeight;
        List<(LineLeader Leader, double BaselineY)>? leaders = null;
        var isLastLine = false;

        // Tagged PDF: BDC must open BEFORE BeginText so the BDC…EMC actually encloses BT…ET.
        int mcid = -1;
        if (ctx.Tagged)
            mcid = canvas.BeginMarkedContent(StructType);

        canvas.BeginText();

        // Track current font/colour to suppress redundant operators.
        TextEmitter.SelectedFont? currentFont = null;
        ColorRgb? currentColor = null;

        for (var lineIdx = _startLine; lineIdx < _endLine; lineIdx++)
        {
            var fragments = _lines![lineIdx];
            if (fragments.Length == 0) continue;

            isLastLine = lineIdx == _lines.Count - 1;

            // Per-line metrics
            var lineWidth = fragments.Sum(f => f.Width);
            var maxFontSize = fragments.Max(f => f.Style.FontSize);

            // Justify only non-last lines
            // A line with a tab placed at a stop is positioned from the left edge and is not
            // stretched: its pieces already sit where the stops put them.
            var tabbed = HoldsPlacedFragment(fragments);
            var isJustified = _para.Alignment == HorizontalAlignment.Justify && !isLastLine && !tabbed;
            var slack = area.Width - lineWidth;

            // Count inter-word gaps in this line (gaps between fragments that end/start with space-adjacent text)
            // We count total word-tokens in the line to compute spacing.
            var wordGapCount = CountWordGaps(fragments);

            // The Tw path spreads the slack over SPACE CHARACTERS, not over word gaps. A gap is a
            // pair of adjacent tokens where either is a space, so every space is counted twice by
            // CountWordGaps, which is right for the embedded path (it adds the extra after both
            // neighbours of a space) and wrong here: Tw applies once per space, so dividing by the
            // gap count handed out half the slack and the line stopped short of the right edge
            // (#548). The divisor must match what Tw draws.
            //
            // Symbol and ZapfDingbats measure every glyph as zero (#470), so the slack on a line that
            // holds either is the whole line width, not the room the text leaves. Handing all of it
            // out per space, as above, pushed such a line twice as far past the right edge as the old
            // divisor did. There is no right answer without metrics, so these lines keep the divisor
            // they always had and are no worse than before. Real metrics would fix both.
            var zeroMeasured = isJustified && HoldsZeroMeasuredFont(fragments);
            var spaceCount = isJustified ? CountSpaces(fragments) : 0;
            var divisor = zeroMeasured ? wordGapCount : spaceCount;
            var wordSpacing = divisor > 0 ? slack / divisor : 0.0;

            // Baseline X for left, center, right — for Justify treat as Left (we use Tw or Tm).
            //
            // Center and Right are floored at zero. The precondition is a single rune wider than
            // the box, not a word: HardBreakWord flushes whenever lineW + charW > maxWidth, so
            // every line it emits stays within maxWidth except the one line consisting of a
            // single over-wide rune, and that is the only way (area.Width - lineWidth) goes
            // negative here. Measured: "W" at 160pt in a 150pt box gives x 50, the floor; "WWW" at
            // 30pt in the same box is one line at x 82.52, an ordinary fit that never reaches
            // HardBreakWord at all. No placement keeps a single over-wide rune's line fully inside
            // the box, so the floor keeps the one thing it can: the line's origin stays inside,
            // and the overflow goes right instead of the line hanging off the left edge. This is
            // the same clamp the running-band fix (#469) chose not to make on its own alignment
            // formula, and the two are not the same decision: there the fitted width is proven not
            // to exceed the box, so a floor would assert the opposite of the proof standing next
            // to it; here a single over-wide rune is proven capable of exceeding it, so nothing is
            // asserted false by keeping the origin legal.
            //
            // The floor moves no bytes for a line that already fits, but the reason is the number
            // format rather than an equality of sums. For a word of one piece, the acceptance
            // check, lineWidth + prevSpaceW + wordW, and the fragment width the merge arm records,
            // last.Width + prevSpaceW + pieceWidths[0], associate identically, both left to right,
            // so they cannot diverge from each other on their own. For a word of several pieces
            // wordW is the sum of the piece widths while each fragment keeps its own width, so the
            // two can differ in the last bit. What can also diverge is the running total
            // itself: lineWidth += prevSpaceW + wordW groups the space and word together before
            // adding them to the total, and that total becomes the left operand of the next
            // word's own check, so the check and the recorded fragment width can part company
            // only from the third word on. What that cannot do is move a byte: the offset is added
            // to area.X before PdfCanvas formats it to five decimals, so a divergence at that scale
            // is absorbed unless the origin itself lies within that divergence of a five-decimal
            // rounding boundary -- at an origin of 50.000005, a difference as small as 1e-13
            // changes "50.00001" to "50" -- and 0 is the boundary instance the corpus behind this
            // release covers, and it is not hypothetical there: at a zero-margin origin this floor
            // emits "0" where the unfloored formula emitted "-0", measured on 93 of 1,600 generated
            // single-line zero-margin paragraphs. Both parse as zero, so the change is invisible to
            // a viewer, but it is a changed byte on a line that fits, and the first version of this
            // comment claimed there were none.
            //
            // The 211-document corpus behind this release does not show it, and the reason is worth
            // knowing rather than reassuring: its zero-margin case is a line filling its box
            // exactly, which emits "0" on both sides, so the corpus covers the geometry and not the
            // divergence. Six of its documents do differ, every one a chart, image or list case
            // named in the changelog, none of them reaching this arm.
            //
            // Justify is excluded by the switch's own `_ => 0` arm above, not because an over-wide
            // line has no inter-word gap to count -- the caveat below shows an ordinary wrapped
            // line can reach this floor too.
            //
            // One caveat on that proof: _lines is cached at the width of the first Layout call,
            // while xOffset above uses area.Width from whichever call is current, so the two
            // widths are the same only because no caller lays one ParagraphRenderer instance out
            // twice at different widths. Grepping `new ParagraphRenderer` across src/ returns four
            // call sites, not three: Document.cs, HeadingRenderer and ListRenderer, plus this
            // file's own Partial arm above, whose overflow renderer carries the cached _lines
            // forward and calls Layout again on whatever area a later page offers. That call site
            // does not widen the risk in practice, because DocumentRenderer holds one PageSize and
            // one set of margins for the whole document, so the later area's width matches the
            // first. DocumentRenderer reuses one ContentArea for both of its own passes,
            // ListRenderer lays out every item at one constant width, and HeadingRenderer
            // delegates here without relaying it out. A caller that did lay this renderer out at
            // two different widths -- a future per-page geometry, or a direct caller of this
            // public renderer -- could bring an ordinary wrapped line to this arm over-wide, and
            // the floor would move it to the left edge. That is the same trade this fix makes
            // deliberately, reached by a different route.
            double xOffset = tabbed ? 0 : _para.Alignment switch
            {
                HorizontalAlignment.Center => Math.Max(0, (area.Width - lineWidth) / 2),
                HorizontalAlignment.Right => Math.Max(0, area.Width - lineWidth),
                _ => 0,  // Left and Justify start at 0
            };

            var linePdfY = ctx.ToPdfY(area.Y) - maxFontSize - (lineIdx - _startLine) * leading;
            var curX = area.X + xOffset;

            // Layout-space top of this text line for link annotation rects.
            var lineLayoutTop = area.Y + (lineIdx - _startLine) * leading;

            // Check if ANY fragment on this line uses an embedded font.
            var hasEmbedded = fragments.Any(f => f.Style.FontRef.IsEmbedded);

            if (isJustified && hasEmbedded)
            {
                // Embedded fonts: Tw does not work with 2-byte CID encoding.
                // Position each word-token explicitly with Tm.
                var spans = DrawJustifiedEmbeddedLine(canvas, ctx, fragments, curX, linePdfY, area.Width, slack, ref currentFont, ref currentColor);
                RegisterLineLinks(ctx, fragments, spans, lineLayoutTop, leading);
            }
            else
            {
                // Standard-14 path (or non-justified): use Tw for word spacing.
                if (spaceCount > 0)
                    canvas.SetWordSpacing(wordSpacing);

                var linkStartX = curX;
                foreach (var frag in fragments)
                {
                    if (frag.StartX is { } placedX)
                        curX = area.X + placedX;
                    if (frag.Leader is { } leader)
                        (leaders ??= []).Add((leader, linePdfY));

                    // A fragment with no text exists only to carry a leader.
                    if (frag.Text.Length > 0)
                    {
                        TextEmitter.SelectFont(ctx, canvas, frag.Style, ref currentFont);
                        SwitchColor(canvas, frag.Style, ref currentColor);

                        canvas.SetTextMatrix(1, 0, 0, 1, curX, linePdfY);
                        TextEmitter.Show(canvas, frag.Style, frag.Text);
                    }

                    // Tw widens every space the fragment holds, so the fragment is drawn that much
                    // wider than the width it was measured at. The next fragment starts after the
                    // drawn width, not the measured one, or it overprints the end of this one
                    // (#555); the link rectangle covers the same drawn width. wordSpacing is zero
                    // on a line that is not justified, so ordinary lines advance as before.
                    var drawnWidth = frag.Width + (CountSpaces(frag.Text) * wordSpacing);

                    // Register link annotation for this fragment if it has a URI.
                    if (frag.Text.Length > 0 && frag.Style.LinkUri is not null)
                    {
                        var fragBox = new LayoutBox(curX, lineLayoutTop, drawnWidth, leading);
                        ctx.AddUriLinkAnnotation(fragBox, frag.Style.LinkUri);
                    }

                    curX += drawnWidth;
                }

                if (spaceCount > 0)
                    canvas.SetWordSpacing(0);
            }
        }

        canvas.EndText();

        // Tagged PDF: close the BDC sequence after ET and register the /StructElem.
        if (ctx.Tagged && mcid >= 0)
        {
            canvas.EndMarkedContent();
            var elem = new PdfStructElem(StructType) { Mcid = mcid };
            if (ElementLanguage is not null)
                elem.Language = ElementLanguage;
            if (ParentStructElem is not null)
            {
                // Nested mode: add as child of the provided parent (table/list use case).
                ctx.StampStructElemPage(elem);
                ParentStructElem.AddChild(elem);
            }
            else
            {
                // Top-level mode: register at the document structure tree root.
                ctx.RegisterStructElem(elem);
            }
        }

        // Leaders follow the paragraph's own marked content, so they stay out of its structure
        // element.
        if (leaders is not null)
            DrawLeaders(canvas, ctx.Tagged, area.X, leaders);
    }

    // ── Link annotation registration ──────────────────────────────────────────

    /// <summary>
    /// For each fragment of an embedded-font justified line that carries a
    /// <see cref="TextStyle.LinkUri"/>, registers a link annotation over the span
    /// <see cref="DrawJustifiedEmbeddedLine"/> measured while drawing it: from the x of the
    /// fragment's first glyph to the x at which the next fragment's first glyph is drawn, or, for
    /// the last fragment of the line, to where its last glyph ends. That is the rectangle the
    /// <c>Tw</c> path gives a standard-14 fragment, so a space that closes the fragment is covered
    /// together with the stretch added after it. A fragment that drew no glyph run on the line gets
    /// no rectangle, because there is no drawn text to cover. A fragment that opens the line draws
    /// its spaces and gets a rectangle. A fragment of only spaces that does not open the line gets
    /// none, because the wrap gave those spaces the style of the fragment before it.
    /// </summary>
    private static void RegisterLineLinks(
        DrawContext ctx,
        LineFragment[] fragments,
        FragmentSpan[] spans,
        double lineTop,
        double lineHeight)
    {
        for (var i = 0; i < fragments.Length; i++)
        {
            if (fragments[i].Style.LinkUri is null || !spans[i].Drawn)
                continue;

            var box = new LayoutBox(spans[i].Start, lineTop, spans[i].End - spans[i].Start, lineHeight);
            ctx.AddUriLinkAnnotation(box, fragments[i].Style.LinkUri!);
        }
    }

    /// <summary>
    /// The horizontal extent one fragment occupies on a justified embedded-font line, as drawn.
    /// <paramref name="Drawn"/> is set when a glyph run of the fragment was shown; a fragment with
    /// no word token never sets it, and its extent stays zero.
    /// </summary>
    private readonly record struct FragmentSpan(double Start, double End, bool Drawn = false);

    // ── Justified embedded draw ───────────────────────────────────────────────

    private static FragmentSpan[] DrawJustifiedEmbeddedLine(
        PdfCanvas canvas,
        DrawContext ctx,
        LineFragment[] fragments,
        double startX,
        double linePdfY,
        double lineAreaWidth,
        double slack,
        ref TextEmitter.SelectedFont? currentFont,
        ref ColorRgb? currentColor)
    {
        // Tokenise fragments into word-tokens; inter-token gaps get extra spacing.
        // A "word gap" is any transition between tokens where the left token ends without
        // trailing space and the right starts without leading space — we add slack/gaps there.
        var owners = new List<int>();
        var tokens = SplitToWordTokens(fragments, owners, drawEverySpace: true);

        // Every pair of neighbours that holds a space takes one share of the slack per space in it,
        // so a space with a neighbour on each side stretches by two shares, as Tw stretches it by
        // one. A space that closes the line has no neighbour on its right and takes one share, where
        // Tw gives it a whole one; that was so before. A space that opens the line has no
        // neighbour on its left to carry its first share, so it takes that share after itself.
        var shares = 0;
        for (var t = 0; t < tokens.Count - 1; t++)
            shares += GapShares(tokens[t], tokens[t + 1]);
        var leadingSpace = tokens.Count > 1 && tokens[0].Text == " ";
        if (leadingSpace)
            shares++;
        var extraPerGap = shares > 0 ? slack / shares : 0.0;
        var curX = startX;

        // Where each fragment is drawn: its first token's x, and the x at which the next
        // fragment with a token starts (or, for the last one, where its own last token ends).
        var spans = new FragmentSpan[fragments.Length];
        var open = -1;

        for (var t = 0; t < tokens.Count; t++)
        {
            var (text, style, width) = tokens[t];
            var owner = owners[t];
            if (owner != open)
            {
                if (open >= 0)
                    spans[open] = spans[open] with { End = curX };
                spans[owner] = new FragmentSpan(curX, curX, true);
                open = owner;
            }

            TextEmitter.SelectFont(ctx, canvas, style, ref currentFont);
            SwitchColor(canvas, style, ref currentColor);
            canvas.SetTextMatrix(1, 0, 0, 1, curX, linePdfY);
            TextEmitter.Show(canvas, style, text);
            curX += width;

            // A fragment ends where its last glyph ends, until a later fragment starts.
            spans[owner] = spans[owner] with { End = curX };

            // Add extra spacing after this token if there's a gap before the next
            if (t < tokens.Count - 1)
                curX += extraPerGap * (GapShares(tokens[t], tokens[t + 1]) + (t == 0 && leadingSpace ? 1 : 0));
        }

        return spans;
    }

    // ── Font / colour switching helpers ──────────────────────────────────────

    private static void SwitchColor(PdfCanvas canvas, TextStyle style, ref ColorRgb? current)
    {
        if (current.HasValue && current.Value == style.Color)
            return;
        canvas.SetFillColorRgb(style.Color.R, style.Color.G, style.Color.B);
        current = style.Color;
    }

    // ── Word-gap / word-token helpers (shared by measure and draw) ───────────

    /// <summary>
    /// Tokenises <paramref name="fragments"/> into word-tokens (non-space text) and
    /// space-tokens (" "), using the same split logic for both the gap-count path and
    /// the draw path so the two always agree on the number of inter-word gaps.
    /// </summary>
    private static List<(string Text, TextStyle Style, double Width)> SplitToWordTokens(
        LineFragment[] fragments,
        List<int>? owners = null,
        bool drawEverySpace = false)
    {
        var tokens = new List<(string, TextStyle, double)>();
        for (var f = 0; f < fragments.Length; f++)
        {
            var frag = fragments[f];
            var parts = frag.Text.Split(' ');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Length > 0)
                {
                    var w = frag.Style.FontRef.MeasureString(part, frag.Style.FontSize);
                    tokens.Add((part, frag.Style, w));
                    owners?.Add(f);
                }
                else if (!drawEverySpace)
                {
                    // Empty between two spaces: no visible token, and the space after it is
                    // not counted. The gap count behind the Tw divisor for a zero-measured font
                    // keeps this reading.
                    continue;
                }

                // If there is a space after this part (not the last split segment),
                // insert a space token so the gap is explicit and countable. With
                // drawEverySpace an empty part still yields its space, so a leading space and
                // each space of a run of spaces is a token, drawn and advanced like any other.
                if (i < parts.Length - 1)
                {
                    var sw = frag.Style.FontRef.MeasureString(" ", frag.Style.FontSize);
                    tokens.Add((" ", frag.Style, sw));
                    owners?.Add(f);
                }
            }
        }
        return tokens;
    }

    /// <summary>
    /// Whether a fragment of the line is set in Symbol or ZapfDingbats, which have no width metrics
    /// and measure as zero.
    /// </summary>
    private static bool HoldsZeroMeasuredFont(LineFragment[] fragments)
    {
        foreach (var frag in fragments)
        {
            var font = frag.Style.FontRef;
            if (!font.IsEmbedded && font.Standard14 is Standard14.Symbol or Standard14.ZapfDingbats)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Counts the space characters (U+0020) a line draws, which is what <c>Tw</c> acts on. Other
    /// white space is not counted: <c>Tw</c> applies to the single-byte code 32 alone.
    /// </summary>
    private static int CountSpaces(LineFragment[] fragments)
    {
        var count = 0;
        foreach (var frag in fragments) count += CountSpaces(frag.Text);
        return count;
    }

    private static int CountSpaces(string text)
    {
        var count = 0;
        foreach (var c in text)
            if (c == ' ') count++;
        return count;
    }

    /// <summary>
    /// The shares of a justified line's slack that fall between two neighbouring tokens: one per
    /// space token among the two. A space between two words holds two such shares, one on each side.
    /// </summary>
    private static int GapShares(
        (string Text, TextStyle Style, double Width) left,
        (string Text, TextStyle Style, double Width) right) =>
        (left.Text == " " ? 1 : 0) + (right.Text == " " ? 1 : 0);

    private static bool IsWordGap(
        (string Text, TextStyle Style, double Width) left,
        (string Text, TextStyle Style, double Width) right) =>
        left.Text == " " || right.Text == " ";

    /// <summary>
    /// Counts the number of inter-word gaps on a line by tokenising via
    /// <see cref="SplitToWordTokens"/> — the same model used by the draw path —
    /// so the count is always consistent with the gaps actually distributed.
    /// </summary>
    private static int CountWordGaps(LineFragment[] fragments)
    {
        var tokens = SplitToWordTokens(fragments);
        var count = 0;
        for (var t = 0; t < tokens.Count - 1; t++)
        {
            if (IsWordGap(tokens[t], tokens[t + 1]))
                count++;
        }
        return count;
    }

    // ── Word-wrap ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces whitespace characters that are word-split points with a plain ASCII space
    /// so that word splitting via <c>Split(' ')</c> handles them uniformly.
    /// <para>
    /// Characters that are <em>not</em> normalised (pass through unchanged):
    /// <list type="bullet">
    ///   <item><c>\n</c> — consumed as the hard-break sentinel by the caller.</item>
    ///   <item>The no-break spaces, which must keep words together: U+00A0 NO-BREAK SPACE,
    ///   U+202F NARROW NO-BREAK SPACE and U+2007 FIGURE SPACE. These are the white-space
    ///   characters in UAX #14 line-breaking class GL; see <see cref="NoBreakSpaces"/>.</item>
    ///   <item>Any non-whitespace character.</item>
    /// </list>
    /// All other characters for which <see cref="char.IsWhiteSpace(char)"/> returns
    /// <see langword="true"/> (tab, thin space, en/em space, …) are mapped to <c>' '</c>.
    /// </para>
    /// </summary>
    private static string NormaliseWhitespace(string s)
    {
        // Fast path: if there is nothing but regular printable chars and plain spaces, return as-is.
        var needsNorm = false;
        foreach (var c in s)
        {
            if (c != '\n' && !NoBreakSpaces.IsNoBreakSpace(c) && char.IsWhiteSpace(c))
            {
                needsNorm = true;
                break;
            }
        }
        if (!needsNorm) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            // \n is the hard-break sentinel; a no-break space is a non-breaking word character.
            if (c != '\n' && !NoBreakSpaces.IsNoBreakSpace(c) && char.IsWhiteSpace(c))
                sb.Append(' ');
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private enum TokenKind : byte
    {
        /// <summary>The first piece of a word.</summary>
        WordStart,

        /// <summary>
        /// A further piece of the word before it, from a run whose text continued it.
        /// </summary>
        WordContinue,

        /// <summary>A line break; it carries no text.</summary>
        HardBreak,

        /// <summary>
        /// A tab character that a tab stop places the next text at; it carries no text and the
        /// style of the run that held it. Produced only when the paragraph has tab stops.
        /// </summary>
        Tab,
    }

    /// <summary>
    /// Greedy word-wrap across all runs. A word is a maximal sequence of characters other than
    /// U+0020 and line feed, counted across the runs, so it can change style part-way; each style
    /// change inside it is a fragment boundary, and nothing is drawn between the pieces.
    /// <para>
    /// Hard line breaks: <c>\n</c>, <c>\r\n</c>, and lone <c>\r</c> all produce explicit line
    /// breaks. The normalisation is per run, so a <c>\r</c> ending one run and a <c>\n</c> starting
    /// the next are two breaks. Input is normalised so that <c>\r\n</c> and lone <c>\r</c> are
    /// replaced with <c>\n</c> before splitting, preventing stray <c>\r</c> glyphs in Standard-14
    /// output.
    /// </para>
    /// <para>
    /// Word splitting uses <see cref="NormaliseWhitespace"/> to convert Unicode whitespace
    /// (tab, thin space, en/em space, etc.) to plain spaces, then splits on space. The no-break
    /// spaces (U+00A0, U+202F and U+2007) are intentionally <em>not</em> split points because they
    /// are semantically a non-breaking join between words: <c>Pourquoi ?</c> written with U+202F
    /// before the question mark cannot wrap between the two.
    /// </para>
    /// </summary>
    /// <returns>
    /// The lines, and for each line how it ended. A line that holds a word's last piece after a
    /// forced cut ends where the word ends, so what follows decides its kind.
    /// </returns>
    internal static (List<LineFragment[]> Lines, List<LineEnd> Ends) WordWrap(
        IReadOnlyList<TextRun> runs, double maxWidth, out int nullStyleRuns,
        IReadOnlyList<TabStop>? stops = null)
    {
        nullStyleRuns = 0;
        var tabbed = stops is not null;
        var lines = new List<LineFragment[]>();
        var ends = new List<LineEnd>();

        // Flatten all runs into a sequence of word pieces and line breaks. Pieces of one word that
        // share a style instance are joined here, before anything is measured, so that two runs
        // sharing an instance measure and draw as the single run holding their text would. A piece
        // from another instance stays a piece of its own even when its values are equal.
        var tokens = new List<(string Text, TextStyle? Style, TokenKind Kind)>();
        var openWord = false;
        foreach (var run in runs)
        {
            // Normalise line endings: \r\n => \n, lone \r => \n.
            var normalised = run.Text.Replace("\r\n", "\n").Replace('\r', '\n');

            // Split on \n first to honour hard line breaks.
            var hardLines = normalised.Split('\n');
            for (var hl = 0; hl < hardLines.Length; hl++)
            {
                // A line break ends the word before it. An empty hard line (two consecutive \n)
                // still forces a break.
                if (hl > 0)
                {
                    openWord = false;
                    tokens.Add((string.Empty, null, TokenKind.HardBreak));
                }

                // With tab stops, a tab ends the word and the text before it, and is a token of
                // its own; the text between two tabs is read as below. A run with no style cannot
                // hold a placed tab, so its tabs stay the separators they are without stops.
                var tabParts = tabbed && run.Style is not null ? hardLines[hl].Split('\t') : null;
                for (var tp = 0; tp < (tabParts?.Length ?? 1); tp++)
                {
                    if (tp > 0)
                    {
                        openWord = false;
                        tokens.Add((string.Empty, run.Style, TokenKind.Tab));
                    }

                    // Normalise all Unicode whitespace (except \n and the three no-break spaces)
                    // to plain spaces, then split on space so tabs, en/em spaces, etc. all act as
                    // word separators. A space ends the word before it; a run that starts or ends
                    // without one continues or leaves the word open for the next run.
                    var segments = NormaliseWhitespace(tabParts is null ? hardLines[hl] : tabParts[tp]).Split(' ');
                    for (var si = 0; si < segments.Length; si++)
                    {
                        if (si > 0)
                            openWord = false;

                        var segment = segments[si];
                        if (segment.Length == 0)
                            continue;

                        // Only a run that yields a word is refused; empty text, or text of word
                        // separators alone, saves with a null style.
                        if (run.Style is null)
                            throw new ArgumentNullException("Style", "A paragraph run with text has a null style.");

                        if (openWord && ReferenceEquals(tokens[^1].Style, run.Style))
                            tokens[^1] = (tokens[^1].Text + segment, run.Style, tokens[^1].Kind);
                        else
                            tokens.Add((segment, run.Style, openWord ? TokenKind.WordContinue : TokenKind.WordStart));
                        openWord = true;
                    }
                }
            }

            // A null style that yielded a word threw above, so what is left here yielded none.
            if (run.Style is null) nullStyleRuns++;
        }

        if (tokens.Count == 0)
        {
            lines.Add([]);
            ends.Add(LineEnd.Last);
            return (lines, ends);
        }

        if (tabbed)
            return WrapWithTabs(tokens, maxWidth, stops!, lines, ends);

        // Greedy wrap
        var lineFrags = new List<(string Word, TextStyle Style, double Width)>();
        var lineWidth = 0.0;
        var pieceWidths = new List<double>();

        // The line that holds the last piece of a word cut by HardBreakWord: it ends where the
        // word ends, which is only known once the next token is.
        var unresolved = -1;

        void CommitLine(LineEnd end)
        {
            lines.Add(BuildFragments(lineFrags));
            ends.Add(end);
            lineFrags.Clear();
        }

        void ResolveCutWord(LineEnd end)
        {
            if (unresolved >= 0)
                ends[unresolved] = end;
            unresolved = -1;
        }

        for (var ti = 0; ti < tokens.Count;)
        {
            // Hard break: commit current line and start a new one.
            if (tokens[ti].Kind == TokenKind.HardBreak)
            {
                ResolveCutWord(LineEnd.Hard);
                CommitLine(LineEnd.Hard);
                lineWidth = 0.0;
                ti++;
                continue;
            }

            var tj = ti + 1;
            while (tj < tokens.Count && tokens[tj].Kind == TokenKind.WordContinue)
                tj++;

            // A word's width is its pieces' widths, each measured in its own style. A one-piece
            // word is measured by the one call the unsplit word always made.
            pieceWidths.Clear();
            var wordW = 0.0;
            for (var k = ti; k < tj; k++)
            {
                var (pieceText, pieceStyle, _) = tokens[k];

                // The width is not used. Measuring in an embedded font records the glyphs
                // measured, so this keeps the space in the font's subset (its /W and ToUnicode
                // entries) as the wrap has always done, and a document with no glued boundary
                // keeps its bytes.
                _ = pieceStyle!.FontRef.MeasureString(" ", pieceStyle.FontSize);
                var w = pieceStyle.FontRef.MeasureString(pieceText, pieceStyle.FontSize);
                pieceWidths.Add(w);
                wordW = k == ti ? w : wordW + w;
            }

            // A word follows a word that ended, so a space the wrap drops ended the line before it.
            ResolveCutWord(LineEnd.Soft);

            if (lineFrags.Count == 0)
            {
                if (wordW > maxWidth)
                {
                    // Hard-break oversized word
                    unresolved = HardBreakWord(tokens, ti, tj, maxWidth, lines, ends);
                    lineWidth = 0.0;
                }
                else
                {
                    for (var k = ti; k < tj; k++)
                        lineFrags.Add((tokens[k].Text, tokens[k].Style!, pieceWidths[k - ti]));
                    lineWidth = wordW;
                }
            }
            else
            {
                // Check if adding a space + this word still fits
                // Space belongs to the style of the PRECEDING word's last piece for measurement
                var prevStyle = lineFrags[^1].Style;
                var prevSpaceW = prevStyle.FontRef.MeasureString(" ", prevStyle.FontSize);

                if (lineWidth + prevSpaceW + wordW <= maxWidth)
                {
                    // Fits — append to current line
                    // Merge the first piece into the same fragment if styles match; otherwise new
                    // fragment. Every further piece is a style change, so it is a fragment of its
                    // own.
                    var firstStyle = tokens[ti].Style!;
                    if (lineFrags[^1].Style == firstStyle)
                    {
                        var last = lineFrags[^1];
                        lineFrags[^1] = (last.Word + " " + tokens[ti].Text, firstStyle, last.Width + prevSpaceW + pieceWidths[0]);
                    }
                    else
                    {
                        // Different style: append a space to the preceding fragment and start a new one
                        var last = lineFrags[^1];
                        lineFrags[^1] = (last.Word + " ", last.Style, last.Width + prevSpaceW);
                        lineFrags.Add((tokens[ti].Text, firstStyle, pieceWidths[0]));
                    }

                    for (var k = ti + 1; k < tj; k++)
                        lineFrags.Add((tokens[k].Text, tokens[k].Style!, pieceWidths[k - ti]));
                    lineWidth += prevSpaceW + wordW;
                }
                else
                {
                    // Doesn't fit — commit current line and start new one
                    CommitLine(LineEnd.Soft);

                    if (wordW > maxWidth)
                    {
                        unresolved = HardBreakWord(tokens, ti, tj, maxWidth, lines, ends);
                        lineWidth = 0.0;
                    }
                    else
                    {
                        for (var k = ti; k < tj; k++)
                            lineFrags.Add((tokens[k].Text, tokens[k].Style!, pieceWidths[k - ti]));
                        lineWidth = wordW;
                    }
                }
            }

            ti = tj;
        }

        if (lineFrags.Count > 0)
            CommitLine(LineEnd.Last);
        else
            ResolveCutWord(LineEnd.Last);

        return (lines, ends);
    }

    private static LineFragment[] BuildFragments(List<(string Word, TextStyle Style, double Width)> frags)
    {
        var result = new LineFragment[frags.Count];
        for (var i = 0; i < frags.Count; i++)
            result[i] = new LineFragment(frags[i].Word, frags[i].Style, frags[i].Width);
        return result;
    }

    /// <summary>
    /// Hard-breaks a single word wider than maxWidth at character granularity. The word is the
    /// pieces <paramref name="first"/> up to <paramref name="end"/> of <paramref name="tokens"/>;
    /// each rune is measured in its piece's style and a line holds one fragment for each style
    /// change. Every line but the last is recorded as ending inside the word. Returns the index
    /// of the last line, whose end the caller settles.
    /// </summary>
    private static int HardBreakWord(
        List<(string Text, TextStyle? Style, TokenKind Kind)> tokens,
        int first,
        int end,
        double maxWidth,
        List<LineFragment[]> lines,
        List<LineEnd> ends)
    {
        var lineFragments = new List<LineFragment>();
        var fragment = new System.Text.StringBuilder();
        var fragmentW = 0.0;
        var lineW = 0.0;
        TextStyle? fragmentStyle = null;

        void CloseFragment()
        {
            if (fragment.Length == 0)
                return;
            lineFragments.Add(new LineFragment(fragment.ToString(), fragmentStyle!, fragmentW));
            fragment.Clear();
            fragmentW = 0.0;
        }

        for (var k = first; k < end; k++)
        {
            var style = tokens[k].Style!;

            // A piece in another style starts a fragment of its own.
            CloseFragment();
            fragmentStyle = style;

            foreach (var rune in tokens[k].Text.EnumerateRunes())
            {
                var ch = rune.ToString();
                var charW = style.FontRef.MeasureString(ch, style.FontSize);
                if ((fragment.Length > 0 || lineFragments.Count > 0) && lineW + charW > maxWidth)
                {
                    CloseFragment();
                    lines.Add([.. lineFragments]);
                    ends.Add(LineEnd.Forced);
                    lineFragments.Clear();
                    lineW = 0.0;
                }
                fragment.Append(ch);
                fragmentW += charW;
                lineW += charW;
            }
        }

        CloseFragment();
        if (lineFragments.Count > 0)
        {
            lines.Add([.. lineFragments]);
            ends.Add(LineEnd.Last);
        }
        return lines.Count - 1;
    }
}
