// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using VellumPdf.Canvas;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Drives automatic pagination: lays out renderers one by one, advancing to a
/// new page whenever a renderer does not fit in the remaining area.
///
/// When <see cref="Header"/> or <see cref="Footer"/> are set a two-pass render
/// is performed: pass 1 counts pages (layout only, no PDF objects created);
/// pass 2 draws all pages including the running bands with {page}/{pages} resolved.
/// </summary>
/// <remarks>
/// The page size and margins are checked in the constructor, which also throws for a null
/// <c>pdf</c> when no page size is given. Every other refusal comes from <see cref="Render"/>,
/// including a header or footer that leaves the content area no positive size. One element may take
/// at most 50,000 page continuations; see <see cref="IRenderer.Layout"/>.
/// </remarks>
public sealed class DocumentRenderer
{
    // Bounds the per-element continuation loop in both pagination passes (#459). It exists because
    // Document.Add(IRenderer) takes any implementation and LayoutResult.Partial accepts any overflow
    // renderer: one whose overflow never advances used to be stopped by the CLR stack overflowing,
    // which is a crash rather than something a caller can catch, and now that both passes loop it
    // would page forever instead.
    //
    // It is therefore also a hard ceiling on how many pages one top-level element may span, and the
    // library's own renderers can reach it: measured on a 420x400pt page laid out one table row to
    // a page, 50,001 rows render and 50,002 throw here, having made progress on every page. That is
    // deliberate rather than ideal, and for some callers it is a new limit.
    //
    // How deep the old recursion could go was never a property of this library. It was the thread's
    // stack size and whether the JIT emitted a tail call. On the default stack with tiered
    // compilation on it died below 4,000 continuations, which is where the 50,000 was pitched. The
    // same document renders 50,002 pages on a 24MB thread under that same JIT, and again on the
    // default stack with DOTNET_TieredCompilation=0 or under Native AOT, where the recursion
    // tail-calls. A caller who had already worked around the crash either way meets the cap
    // instead. The message names the causes it can, because only one of them is the caller's own.
    private const int MaxContinuationsPerElement = 50_000;

    // Thrown from the one placement loop, which both passes run, so the two passes cannot drift to
    // different wording, or different verdicts, on an element neither can place (#460). It does not
    // make the passes agree in general: with a running band and no content at all, the measuring
    // pass still counts one page where the drawing pass creates none, which predates the loop.
    // The throw comes in the measuring pass when a band is set, so a caller that catches it is left
    // holding an empty PdfDocument rather than the pages the drawing pass had already committed;
    // PdfDocument.Save then reports that the document has no pages.
    private const string ElementTooTallMessage =
        "An element is too tall to fit on a single page and cannot be rendered. " +
        "Reduce the element's content or increase the page size.";

    /// <summary>
    /// The too-tall exception, naming a running band when one is what shrank the content box.
    ///
    /// The message above is what both passes threw for every cause, and it cost a consumer a wrong
    /// first reading: a footer was added to a document that had been paginating, the document
    /// stopped rendering, and the message pointed at the element while suggesting a remedy —
    /// reduce its content — that cannot help, because the element had not changed. The bands had.
    ///
    /// The no-band wording is unchanged byte for byte, so the existing control test still holds and
    /// its passing is itself the evidence that only the band case moved. Figures follow the
    /// margins-case throw in RunLayout, which already prints them to one decimal with a pt suffix.
    /// A method rather than a second constant because the band clause needs the instance's own
    /// geometry, and both passes call this one producer for the reason the constant existed: so
    /// they cannot drift to different wording on an element neither can place (#460).
    /// </summary>
    private InvalidOperationException ElementTooTall()
    {
        var reserved = HeaderHeight + FooterHeight;
        if (reserved <= 0) return new InvalidOperationException(ElementTooTallMessage);

        // The margin term is named, and named before the result, because without it the figures
        // read as a subtraction that fails: a reader shown a 200pt page and 18.4pt of bands
        // subtracts to 181.6 and is told 161.6. The margins are usually the larger term, and the
        // old message's fault was offering a remedy that could not work, so leaving them out of
        // both the arithmetic and the remedy list would repeat it.
        //
        // Every figure is rounded to a tenth, so the printed terms need not sum exactly to the
        // printed result — measured, margins of 10.02 and a footer of 18.04 print 20.0 and 18.0
        // against a content area of 161.9. The point is to name every term that shrank the box,
        // not to be arithmetic a reader checks to the last digit.
        return new InvalidOperationException(
            "An element is too tall to fit on a single page and cannot be rendered. "
            + FormattableString.Invariant(
                $"On a {_pageSize.Height:F1}pt page, {_margins.Vertical:F1}pt of margins and ")
            + FormattableString.Invariant(
                $"{reserved:F1}pt of running bands (header {HeaderHeight:F1}pt, footer ")
            + FormattableString.Invariant(
                $"{FooterHeight:F1}pt) leave a content area {ContentArea.Height:F1}pt tall. ")
            + "Reduce the element's content, lower the band heights, reduce the margins, or "
            + "increase the page size.");
    }

    private static InvalidOperationException TooManyContinuations() =>
        new($"A single element needed more than {MaxContinuationsPerElement} page continuations, so " +
            "its layout is not converging. Either an overflow renderer never advances, or the " +
            "element genuinely spans more pages than one element may and should be split into " +
            "several, or its height sits within a rounding error of the content area, so " +
            "pagination refuses and accepts it by turns.");

    private readonly PdfDocument _pdf;
    private readonly PdfRectangle _pageSize;
    private readonly EdgeInsets _margins;

    private readonly List<IRenderer> _renderers = [];

    private PdfPage? _currentPage;
    private PdfCanvas? _currentCanvas;
    private RendererContext? _currentRendererCtx;
    private double _currentY;  // layout-space Y cursor (Y-down)

    private readonly List<TextEncodingWarning> _textEncodingWarnings = [];

    // One slot per band rather than one entry per page. A per-page list would hold two entries per
    // page — 9,900 on the 4,950-page document the defect was reported against — and would put a
    // list append back into the per-band-per-page path this branch exists to bound. Two slots need
    // no cap, which is why no options type is invented for them.
    private BandTruncationWarning? _headerTruncation;
    private BandTruncationWarning? _footerTruncation;

    // Each band's template, scanned once and resolved lazily for every page, so a long template
    // costs what is drawn from it rather than its length on each page. Kept per renderer because
    // BandText holds the page's buffer; the compiled template itself belongs to the RunningBand.
    private BandText? _headerText;
    private BandText? _footerText;

    // The chapter a band's {chapter} prints, followed through the drawing pass only. Both bands
    // show the same one, so the tracker is shared. All three are null until a level-0 heading is
    // drawn, and nothing is kept when no band asks for the token (_trackChapters).
    private bool _trackChapters;
    private string? _chapterBefore;  // the last chapter drawn on an earlier page
    private string? _chapterFirst;   // the first chapter that starts on the current page
    private string? _chapterLatest;  // the last chapter that starts on the current page

    /// <summary>
    /// Renderers the chapter tracker looked at in the last <see cref="RunLayout"/>, read by tests.
    /// Zero when no band uses <c>{chapter}</c>, and zero in the measuring pass whatever the bands
    /// are.
    /// </summary>
    internal int ChapterNotes { get; private set; }

    // Characters produced across every band draw of the save. Read by tests that pin the cost per
    // page, which no timing can pin at this size.
    internal long BandCharactersProduced { get; private set; }

    // What the layout noticed and kept going through; see Document.Diagnostics. Created with the
    // renderer, but it allocates its collections on the first report.
    private readonly LayoutDiagnosticSink _sink = new();

    internal LayoutDiagnosticSink Sink => _sink;

    // The items the list walk visited in this save; see ListWalkState. It lives on the sink because
    // only a list that reports takes part in the walk.
    internal ListWalkState ListWalk => _sink.ListWalk;

    /// <summary>Everything the layout reported so far, in first-occurrence order.</summary>
    internal IReadOnlyList<LayoutDiagnostic> Diagnostics => _sink.Snapshot().AsReadOnly();

    /// <summary>
    /// Characters written via <see cref="PdfCanvas.ShowText"/> across every page rendered so far
    /// that WinAnsiEncoding could not represent. Aggregated from each page's canvas as it finishes.
    /// </summary>
    internal IReadOnlyList<TextEncodingWarning> TextEncodingWarnings => _textEncodingWarnings;

    /// <summary>
    /// Running bands whose text was cut to fit the content box, at most one report per band, each
    /// naming the page that lost the most.
    /// </summary>
    /// <remarks>
    /// Empty until <see cref="Render"/> has run. A cut band loses text the caller wrote, so
    /// <see cref="Document.StrictDiagnostics"/> refuses a save that cut one, and
    /// <see cref="Document.Diagnostics"/> also lists it as
    /// <see cref="LayoutDiagnosticCode.BandTruncated"/>. This list stays as it is until 3.0, and
    /// <see cref="Render"/> exposes no <c>Diagnostics</c> and has no strict switch. The cut
    /// compares a running total of the resolved text's character widths
    /// with the content width. Resolved text whose glyphs measure zero or negative width, as at a
    /// negative font size, is not cut or reported (see <see cref="RunningBand.Template"/>), and
    /// neither is any band when the content width is <c>NaN</c>. Resolved text that fits exactly
    /// can still be cut, because the running total can exceed the whole-string width by a rounding
    /// error.
    /// </remarks>
    public IReadOnlyList<BandTruncationWarning> BandTruncations
    {
        get
        {
            var result = new List<BandTruncationWarning>(2);
            if (_headerTruncation is { } header) result.Add(header);
            if (_footerTruncation is { } footer) result.Add(footer);
            return result;
        }
    }

    /// <summary>Header band drawn at the top of every page. Optional.</summary>
    /// <remarks>
    /// A band whose <see cref="RunningBand.Template"/> is null makes <see cref="Render"/> throw
    /// (#531). Pages the kernel document already held when <see cref="Render"/> runs get no band,
    /// and <c>{page}</c> counts those pages while <c>{pages}</c> does not. The band's height and
    /// style refusals also come from <see cref="Render"/>; see <see cref="RunningBand.Height"/>,
    /// <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and
    /// <see cref="TextStyle.FontRef"/>.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Render"/>, not from this property, when the band's
    /// <see cref="RunningBand.Template"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Render"/>, when the band's height leaves the content area no positive
    /// size, or when <c>Height</c> is null and its style's size or leading does; see
    /// <see cref="RunningBand.Height"/> and <see cref="RunningBand.Style"/>. It is also raised when
    /// the band measures text that holds an unpaired surrogate in an embedded font, which can
    /// include part of the template it then does not draw; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Render"/>, when the band's height, or its style's size or leading, is
    /// refused; see <see cref="RunningBand.Height"/>, <see cref="TextStyle.FontSize"/> and
    /// <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Render"/>, when the band's style holds a
    /// <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name and the band
    /// draws text.
    /// </exception>
    public RunningBand? Header { get; set; }

    /// <summary>Footer band drawn at the bottom of every page. Optional.</summary>
    /// <inheritdoc cref="Header" path="/remarks"/>
    /// <inheritdoc cref="Header" path="/exception"/>
    public RunningBand? Footer { get; set; }

    /// <summary>Creates a renderer that paginates content onto <paramref name="pdf"/> using the given page size and margins.</summary>
    /// <remarks>
    /// The page size and margins are checked here. A page width or height that is not a positive
    /// finite number throws <see cref="ArgumentOutOfRangeException"/>, and margins that meet or
    /// exceed the page throw <see cref="ArgumentException"/>, both from this constructor, and so does
    /// a positive-infinity margin, which meets the page. A <c>NaN</c> or negative-infinity margin
    /// is not checked here; see <see cref="Document.Margins"/>.
    /// <para>A null <paramref name="pageSize"/> means the <see cref="PdfDocument.DefaultPageSize"/>
    /// of <paramref name="pdf"/> at construction, and a null <paramref name="margins"/> means 72
    /// points on every side.</para>
    /// <para>A null <paramref name="pdf"/> throws <see cref="ArgumentNullException"/> from this
    /// constructor, naming <c>pdf</c>. </para>
    /// <para>A negative margin is accepted and moves that edge of the content area outward, as on
    /// <see cref="Document.Margins"/>. The origin of <paramref name="pageSize"/> is not used for
    /// layout, as on <see cref="Document.PageSize"/>.</para>
    /// <para>Do not pass a page rectangle not at the origin or a page size so large that positions
    /// overflow. Do not pass a margin that is a finite negative inset either. 3.0
    /// will refuse them.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The page width or height is not a positive finite number.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A margin is <c>NaN</c> or infinite, or the margins meet or exceed the page on either axis. It
    /// is also raised from
    /// <see cref="Render"/>, not from this constructor, when margins that pass this check leave no
    /// positive content area once a header or footer is taken off, or put a position written
    /// outside the content stream at a non-finite value; see <see cref="Document.Margins"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="pdf"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Render"/>, not from this constructor, when a page size and margins
    /// that pass this constructor's checks still leave an element unable to be laid out, for any of
    /// the causes listed on <see cref="Document.PageSize"/> and <see cref="Document.Margins"/>.
    /// </exception>
    public DocumentRenderer(PdfDocument pdf, PdfRectangle? pageSize = null, EdgeInsets? margins = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        _pdf = pdf;
        _pageSize = pageSize ?? pdf.DefaultPageSize;
        _margins = margins ?? new EdgeInsets(72); // 1-inch default margins (72pt)

        ValidateGeometry(_pageSize, _margins);
    }

    /// <summary>Appends a renderer to the document flow and returns this instance for chaining.</summary>
    /// <remarks>
    /// A null <paramref name="renderer"/> is refused here, as <see cref="Document"/> refuses
    /// one passed to its own <c>Add</c>. What the renderer holds is not
    /// checked here. The page-continuation limit on <see cref="IRenderer.Layout"/> applies from
    /// <see cref="Render"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="renderer"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Render"/>, not from this call, when <paramref name="renderer"/> returns
    /// a null result from <see cref="IRenderer.Layout"/> or a <see cref="LayoutResult.Partial"/>
    /// with a null renderer.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Render"/>, not from this call, when <paramref name="renderer"/>, or
    /// an overflow it returns, needs more than 50,000 page continuations, or when it returns
    /// <see cref="LayoutResult.Outcome.Nothing"/> twice in a row, the second time on a new page.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Render"/>, not from this call, when <paramref name="renderer"/>
    /// returns a result whose non-finite bottom leaves a later element at a position
    /// <see cref="Render"/> writes outside the content stream; see <see cref="LayoutResult.Full"/>.
    /// </exception>
    public DocumentRenderer Add(IRenderer renderer)
    {
        // Document.Add(IRenderer) already refused a null; this is the same operation by the other
        // door.
        ArgumentNullException.ThrowIfNull(renderer);
        _renderers.Add(renderer);
        return this;
    }

    /// <summary>Lays out all added renderers and saves the resulting PDF to <paramref name="destination"/>.</summary>
    /// <remarks>
    /// The checks on <paramref name="destination"/> and on the kernel document's options run first,
    /// before the layout adds any page to it (a document with nothing added checks its page geometry
    /// and bands before them), so a refusal leaves the document as it was and a
    /// retry cannot write the pages twice. The write is
    /// <see cref="PdfDocument.Save(System.IO.Stream)"/>, so every exception it documents can reach
    /// you from here. <see cref="Document.Save(System.IO.Stream)"/> builds a
    /// renderer and calls this method, so the exceptions listed on that save, other than those
    /// about the <see cref="Document"/> itself, can reach you from here, and so can anything a
    /// custom renderer or the destination raises. A second call after one that succeeded throws;
    /// after a layout that threw, it can succeed and write the failed call's pages as well.
    /// <see cref="Document.Save(System.IO.Stream)"/> guards against that by refusing a save after a
    /// layout that threw; this method does not. A failure once writing has started leaves what was
    /// written so far in
    /// <paramref name="destination"/>, which can be the PDF header alone.
    /// <para>This method has no strict switch and exposes no <c>Diagnostics</c>: it never applies
    /// <see cref="Document.StrictDiagnostics"/>, so none of that refusal reaches you from
    /// here.</para>
    /// <para><b>A page reference or a link to an anchor can refuse this call.</b> The refusals are
    /// on <see cref="TextStyle.LinkAnchor"/> and
    /// <see cref="TextRun.PageReference(string, TextStyle)"/>. In a renderer that holds a page
    /// reference, a <see cref="Header"/> or <see cref="Footer"/> replaced while this call runs also
    /// makes it throw "Pagination changed between the measuring and the drawing pass". <b>Do not
    /// replace</b> <see cref="Header"/> or <see cref="Footer"/> while this call runs; 3.0 reads
    /// them once.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// An element needs more than <b>50,000</b> page continuations or is too tall for one page, or
    /// the kernel document has no pages, or this document has already been written, or the kernel
    /// document's conformance level refuses its encryption settings, or an element's input cannot
    /// be laid out, or a registered font lacks a table the write needs, or a link to an anchor or a
    /// page reference is refused; see the remarks. It is also thrown, by the final write and so
    /// after the layout, when the kernel document holds a link on one of its pages whose
    /// <see cref="VellumPdf.Annotations.PdfLinkAnnotation.DestName"/> no
    /// <see cref="PdfDocument.AddNamedDestination"/> call added, or a
    /// <see cref="PdfDocument.AddPageLabelRange"/> range that starts at or after the number of
    /// pages the layout produced. The layout's pages are then already in the kernel document, which
    /// is not marked written: calling this method again lays them out a second time, so fix the
    /// kernel document and save it with <see cref="PdfDocument.Save(System.IO.Stream)"/> instead.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The margins, header and footer together leave the content area no positive size, or an
    /// element refuses its own input, or <paramref name="destination"/> is not writable or is closed
    /// and the kernel document is not linearized (the parameter name is <c>destination</c>, and it
    /// is raised before the layout, except in a document with nothing added), or a
    /// registered font has a value that makes a font metric
    /// non-finite, or text measured in an embedded font holds an unpaired surrogate, or a
    /// non-finite position is written outside the content stream, such as a link rectangle.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> is <see langword="null"/>, checked before the layout runs, except
    /// in a document with nothing added, where the page geometry and bands are checked first. A
    /// null that an element holds and that is refused where it is used, such as a band template that
    /// is drawn or a null value in a written <see cref="PdfDictionary"/>, is raised from here too; see
    /// <see cref="Document.Save(System.IO.Stream)"/>.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// An added renderer is <see langword="null"/>, returns a null result, or returns a
    /// <see cref="LayoutResult.Partial"/> result with a null renderer. It is also raised when a
    /// member an element stored without a check is null.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// A <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name is selected
    /// on a page; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A registered embedded font has malformed outline data.
    /// </exception>
    /// <exception cref="OverflowException">
    /// A row's <see cref="Elements.Table.Cell.ColSpan"/> values sum past
    /// <see cref="int.MaxValue"/>.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// A column count resolved from <see cref="Elements.Table.Cell.ColSpan"/> is too large to
    /// allocate.
    /// </exception>
    /// <exception cref="IOException">
    /// The destination stream failed to write. With a linearized kernel document the destination is
    /// not checked up front, so a destination that cannot be written to throws here after the layout
    /// whatever its own <c>Write</c> throws, <see cref="IOException"/> or not; see
    /// <see cref="PdfDocument.Save(System.IO.Stream)"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The kernel document combines options it cannot write together, such as object streams with
    /// encryption, or linearization with either.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The kernel document was disposed. A closed <paramref name="destination"/> is an
    /// <see cref="ArgumentException"/> unless the kernel document is linearized, where the stream's
    /// own <c>Write</c> decides.
    /// </exception>
    public void Render(Stream destination)
    {
        // What the save would refuse without looking at the pages comes first. Layout appends pages
        // to the kernel document, and a refusal after it, such as an unwritable destination, left
        // them there with the document not marked as written, so a retry laid out again on top of
        // them and wrote every page twice (#530). The kernel document marks itself written once
        // these checks have passed, and a failure after that burns it; so after this point a failure
        // either added no pages, or leaves the document unable to be written again, or is the
        // navigation refusal (a link to a missing name, a label range past the last page), which
        // adds the pages and leaves the kernel document writable.
        // With nothing added the layout only checks the page geometry and the bands and adds no page,
        // so it runs first and "no pages" follows, as it always did.
        if (_renderers.Count == 0) RunLayout();
        _pdf.EnsureCanSave(destination, pagesKnown: _renderers.Count == 0);
        _pdf.ValidateSaveOptions();

        RunLayout();
        _pdf.Save(destination);
    }

    /// <summary>
    /// True once <see cref="RunLayout"/> got past its geometry checks. A renderer caches its wrap at
    /// the width it was first laid out at, so a document that reached this point cannot be laid out
    /// again with different geometry.
    /// </summary>
    internal bool HasStartedLayout { get; private set; }

    /// <summary>
    /// Runs the layout pass (page count + full render) without writing to any stream.
    /// After this call, callers may invoke <see cref="PdfDocument.Save"/> or
    /// <see cref="PdfDocument.PrepareForSigning"/> on the underlying document.
    /// </summary>
    internal void RunLayout()
    {
        // Every list this layout lays out reads the run, whatever context it is handed, and a
        // layout that a renderer of a caller's starts from inside this one gets a run of its own.
        // Removed in 3.0 (#688).
        var run = ListLayoutRun.Begin(_pdf, out var outerRun);
        try
        {
            RunLayoutWith(run);
        }
        finally
        {
            ListLayoutRun.End(outerRun);
        }
    }

    private void RunLayoutWith(ListLayoutRun listRun)
    {
        // A refusal belongs to the layout that raised it; a renderer reused for another layout starts clean.
        _deferredCanvasRefusal = null;
        MeasureRuns = 0;
        LayoutCalls = 0;
        ContinuationProbes = 0;
        ContinuationProbeFaults = 0;
        UnreportedChildReports = 0;
        _continuationVerdicts = null;
        _measureSink = null;
        _drawSink = null;
        _guard = null;
        _navigation = new NavigationPlan();
        _labels = new PageLabelMap();
        _trackChapters = WantsChapter(Header) || WantsChapter(Footer);
        _chapterBefore = _chapterFirst = _chapterLatest = null;
        ChapterNotes = 0;

        var contentArea = ContentArea;
        if (contentArea.Width <= 0 || contentArea.Height <= 0)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The computed content area has no positive size ({contentArea.Width:F1}×{contentArea.Height:F1}pt). ") +
                "The margins, header, and footer together exceed the page dimensions. " +
                "Reduce the margins or band heights.",
                "margins");

        // The check above refuses before any renderer has measured, so the document is still
        // saveable once the caller has fixed what it names: a save that fails after the layout
        // started cannot be retried, one refused before it can. The preformatted elements are
        // checked here for the same reason; the walk below, over the added elements, finds the
        // plan-dependent ones.
        PreformattedChecks.Run(_renderers, _pdf.Conformance, contentArea);

        // Tab stops are checked here, with the content width every top-level paragraph is laid out
        // in, so the refusal leaves the document saveable once the page or margins are changed.
        foreach (var renderer in _renderers)
        {
            if (renderer is ParagraphRenderer { Paragraph.TabStops: { Count: > 0 } } paragraphRenderer)
            {
                LayoutValidation.ValidateTabStops(
                    paragraphRenderer.Paragraph, contentArea.Deflate(paragraphRenderer.Paragraph.Margins).Width);
                LayoutValidation.ValidateLeaderSizes(paragraphRenderer.Paragraph);
            }
        }

        // What the elements alone decide about anchors and label ranges is refused here too, so the
        // document stays saveable: a repeated anchor, and a labelled break with nothing after it.
        // The same walk reads every link to an anchor and every page reference, so a name no
        // element holds, and a link under a PDF/UA-1 claim, are refused here too.
        _navigation = NavigationPlan.Build(_renderers, _pdf.Conformance, Header, Footer);
        listRun.Navigation = _navigation;
        if (_navigation.References is { } references)
        {
            references.LabelBound = LongestLabelPrefix() + PageReferenceTable.NumeralBound;
            listRun.References = references;
        }

        // From here the renderers measure and cache; see Document.MarkFailedIfLaidOut.
        HasStartedLayout = true;

        // The pages the kernel document already holds. A recto break counts physical pages, as
        // {page} does, so it counts from here and not from this layout's first page.
        _pagesBefore = _pdf.Pages.Count;

        var dependents = FindPlanDependents();

        // Reports are collected only when something reads them: a dependent resolves against them
        // and the guard compares the drawing pass with them. Tests set RecordPlacements.
        _collect = dependents is not null || RecordPlacements || _navigation.HasAnchors || _navigation.HasBreakLabels;

        ScanLists(listRun);

        int totalPages;
        if (dependents is not null)
        {
            // Resolves the dependents against each run's plan until they stop changing, so what the
            // drawing pass prints names the page its target is drawn on.
            totalPages = MeasureUntilStable(dependents);
        }
        else if (Header is not null || Footer is not null)
        {
            // One measuring run: the same loop as the drawing pass, with no PDF objects created.
            totalPages = RunPass(measure: true, totalPages: 0);
        }
        else
        {
            totalPages = 0; // unknown / irrelevant
        }

        // The drawing pass, with running bands resolved. A document with a dependent compares each
        // placement it reports with the last measuring run's, which is what the dependents were
        // resolved against.
        _guard = dependents is not null ? _measureSink : null;
        RunPass(measure: false, totalPages);

        // An anchor whose element the drawing pass never reported leaves no destination, and the
        // caller was promised one.
        if (_navigation.HasAnchors) _navigation.RefuseUnplaced();

        // A reference a renderer of the caller's held was never resolved against the pagination.
        listRun.References?.RefuseUnresolved();
        ReportBandTruncations();

        if (_deferredCanvasRefusal is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_deferredCanvasRefusal).Throw();
    }

    // ── Private ──────────────────────────────────────────────────────────────

    // The longest prefix any label range of the document can give a page, which with a numeral is
    // the most a page reference can print. The ranges are known before any layout runs.
    private int LongestLabelPrefix()
    {
        var longest = DocumentPageLabel?.Prefix?.Length ?? 0;
        foreach (var renderer in _renderers)
            if (renderer is PageBreakRenderer { PageBreak.PageLabel: { } range })
                longest = Math.Max(longest, range.Prefix?.Length ?? 0);
        return longest;
    }

    // What a reference can add to the bound on the continuations an element needs: the most its
    // label can hold, which the walk set from the ranges of the document. Zero for a layout with
    // no reference.
    private static long ReferenceBound => ListLayoutRun.Current?.References?.LabelBound ?? 0;

    private static LayoutElementKind KindOf(IRenderer renderer) => renderer switch
    {
        ParagraphRenderer => LayoutElementKind.Paragraph,
        HeadingRenderer => LayoutElementKind.Heading,
        ListRenderer => LayoutElementKind.List,
        TableRenderer => LayoutElementKind.Table,
        LayoutImageRenderer => LayoutElementKind.Image,
        LineSeparatorRenderer => LayoutElementKind.LineSeparator,
        PieChartRenderer => LayoutElementKind.PieChart,
        PreformattedRenderer => LayoutElementKind.Preformatted,
        _ => LayoutElementKind.Custom,
    };

    private void ReportBandTruncations()
    {
        foreach (var warning in BandTruncations)
        {
            _sink.CurrentKind = warning.Band == RunningBandKind.Header
                ? LayoutElementKind.Header
                : LayoutElementKind.Footer;
            _sink.CurrentIndex = null;
            _sink.CurrentPage = warning.PageNumber;
            _sink.Report(
                LayoutDiagnosticCode.BandTruncated,
                (long)warning.Band,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"The {warning.Band.ToString().ToLowerInvariant()} band's text was cut to fit the content width: {warning.DrawnCharacters} of {warning.ResolvedCharacters} characters drawn on the page that lost the most."));
        }

        _sink.CurrentKind = LayoutElementKind.Document;
        _sink.CurrentPage = null;
    }

    // A null template is left for the band's first draw to refuse, as it always was.
    private static bool WantsChapter(RunningBand? band) =>
        band is { ChapterToken: true, Template: not null }
        && band.Compiled.TokenCounts[(int)BandToken.Chapter] > 0;

    // Called with each renderer the drawing pass accepted, which is the placement that stays: the
    // measuring pass never reaches it, and a renderer that was laid out and then replaced is not
    // drawn. A split heading arrives as the piece that holds its first line; the overflow is a
    // plain paragraph renderer, so it starts no chapter.
    private void NoteChapter(IRenderer renderer)
    {
        ChapterNotes++;
        if (renderer is not HeadingRenderer { Element: { Level: 0 } heading }) return;

        var title = LayoutText.ControlSpacesToSpaces(heading.ResolvedBookmarkTitle);
        _chapterFirst ??= title;
        _chapterLatest = title;
    }

    // The page's chapter: the first one that starts on it, else the one in force from before.
    private string? ChapterOnPage => _chapterFirst ?? _chapterBefore;

    private double HeaderHeight => Header?.EffectiveHeight ?? 0;
    private double FooterHeight => Footer?.EffectiveHeight ?? 0;

    private LayoutBox ContentArea => new(
        _margins.Left,
        _margins.Top + HeaderHeight,
        _pageSize.Width - _margins.Horizontal,
        _pageSize.Height - _margins.Vertical - HeaderHeight - FooterHeight);

    private void EnsurePage()
    {
        if (_currentPage is null)
        {
            _currentPage = _pdf.AddPage(_pageSize);
            _currentCanvas = new PdfCanvas(_currentPage);
            _currentRendererCtx = new RendererContext(_currentPage, _pdf);
            _currentY = _margins.Top + HeaderHeight;
        }
    }

    private void FinishCurrentPage(int totalPages)
    {
        if (_currentCanvas is not null)
        {
            // The bands report under their own location, and the element that was being laid out
            // when the page ended carries on afterwards, so the location is put back.
            var (kind, index, reportPage) = (_sink.CurrentKind, _sink.CurrentIndex, _sink.CurrentPage);

            // Draw header/footer for this page.
            if (Header is not null || Footer is not null)
            {
                var pageNumber = _pdf.Pages.Count; // pages already added
                DrawRunningBands(_currentCanvas, _currentRendererCtx!, _currentPage!, pageNumber, totalPages);
            }

            _sink.SyncEncodingWarnings(_currentCanvas!, report: true);
            (_sink.CurrentKind, _sink.CurrentIndex, _sink.CurrentPage) = (kind, index, reportPage);

            // The next page is in the chapter this one ended in.
            if (_chapterLatest is not null)
            {
                _chapterBefore = _chapterLatest;
                _chapterFirst = _chapterLatest = null;
            }

            _textEncodingWarnings.AddRange(_currentCanvas.TextEncodingWarnings);

            _currentCanvas.Finish();
            _currentPage = null;
            _currentCanvas = null;
            _currentRendererCtx = null;
        }
    }

    // ── The placement loop ────────────────────────────────────────────────────

    /// <summary>
    /// The most measuring runs a document with a plan-dependent renderer gets before the save
    /// refuses it. The first run resolves against the placeholder and each later one against the
    /// run before it, so five runs allow four refinements.
    /// </summary>
    internal const int MaxMeasureRuns = 5;

    /// <summary>Measuring runs of the last <see cref="RunLayout"/>, read by tests.</summary>
    internal int MeasureRuns { get; private set; }

    /// <summary>The page references of the last <see cref="RunLayout"/>, or null when it found
    /// none. Read by tests.</summary>
    internal PageReferenceTable? ReferenceTable => _navigation.References;

    /// <summary>
    /// <see cref="IRenderer.Layout"/> calls the placement loop made in the last
    /// <see cref="RunLayout"/>, both passes together. Read by tests.
    /// </summary>
    internal int LayoutCalls { get; private set; }

    /// <summary>
    /// The plan of the last measuring run, or null when none ran. Built on each read.
    /// </summary>
    internal PaginationPlan? MeasurePlan =>
        _measureSink is null ? null : new PaginationPlan(_measureSink.Placements, _measurePages, DocumentPageLabel, _pagesBefore);

    /// <summary>
    /// The plan the drawing pass reported, or null before it ran. Built on each read.
    /// </summary>
    internal PaginationPlan? DrawPlan =>
        _drawSink is null ? null : new PaginationPlan(_drawSink.Placements, _drawPages, DocumentPageLabel, _pagesBefore);

    // The pass the loop is in. A measuring pass creates no page and keeps its own cursor, so a
    // page a failed drawing pass left open is not disturbed by it.
    private bool _measuring;
    private LayoutBox _measureArea;
    private double _measureY;

    // 0-based page of the cursor, counted the same way in both passes and from this layout's first
    // page. _pdf.Pages.Count would differ between the passes for a kernel document that already
    // holds pages.
    private int _pageIndex;

    // Pages the kernel document held when the layout started; see RunLayout.
    private int _pagesBefore;

    // The strongest break asked for since the last element was placed, and the renderers that asked
    // (kept only when placements are collected). A break takes effect before the next element, so a
    // break with nothing after it is dropped at the end of the pass.
    private PageBreakMode _pendingBreak;
    private List<IRenderer>? _pendingBreakers;

    // Whether anything has been accepted on the current page; a zero-height element counts. A page
    // without is already a new page, so a break adds no page to end it; a Recto break may still add
    // a blank page for parity.
    private bool _placedOnPage;

    // The keep rules look ahead by placing a group of elements on a copy of the cursor. While that
    // runs the loop is in measuring mode with these set: it records where each element's first
    // piece landed, reports nothing and touches no page. _keepResolvedUntil is the first element
    // whose keep request has not been decided, so a run is decided once, at its first element.
    private bool _simulating;
    private bool _simFirstOnly;
    private int _simFirstPage;
    private int _keepResolvedUntil;

    // Set when a run was dropped in the drawing pass; the report is made once its first element is
    // placed (ReportPendingKeepWithNext).
    private bool _keepRelaxedPending;

    private PlacementSink _placements = new();
    private PlacementSink? _measureSink;
    private PlacementSink? _drawSink;
    private int _measurePages;
    private int _drawPages;

    // The measuring run the drawing pass is compared with; set only when a dependent exists.
    private PlacementSink? _guard;

    private bool _collect;

    // The anchors and labelled breaks the added elements hold; see NavigationPlan.
    private NavigationPlan _navigation = new();

    // The label ranges the drawing pass has started so far, by kernel page index. A range starts
    // when its break is applied, which is before any page it labels is finished, so a running
    // band reads the label of its own page from here.
    private PageLabelMap _labels = new();

    /// <summary>
    /// The page-label range that starts at the first page of the layout, or null. Set by
    /// <see cref="Document"/> from <see cref="Document.PageLabel"/>; a renderer built directly has
    /// none, and no element it can be given starts a range.
    /// </summary>
    internal PageLabelRange? DocumentPageLabel { get; set; }

    /// <summary>
    /// Collect placement reports even when no dependent reads them. Off by default, so a document
    /// pays for reports only when a dependent needs them; tests turn it on to read the plans.
    /// </summary>
    internal bool RecordPlacements { get; set; }

    private double Cursor
    {
        get => _measuring ? _measureY : _currentY;
        set
        {
            if (_measuring) _measureY = value;
            else _currentY = value;
        }
    }

    // The measuring pass reads the content area once, as it always has; the drawing pass reads it
    // as it goes.
    private LayoutBox PlaceArea => AreaOnPage(_measuring ? _measureArea : ContentArea, _pageIndex);

    /// <summary>
    /// Whether margins are mirrored for double-sided print: <see cref="EdgeInsets.Left"/> is then the
    /// inside margin and <see cref="EdgeInsets.Right"/> the outside one. Set by
    /// <see cref="Document"/> from <see cref="Document.MirrorMargins"/>; a renderer built directly
    /// keeps the same margins on every page.
    /// </summary>
    internal bool MirrorMargins { get; set; }

    // Parity is the physical position in the kernel document and page 1 is recto; a page label
    // never enters it, because labels may restart. One rule for the recto break, the content
    // area's x and the bands: the recto break and the content area ask IsRecto, which numbers the
    // page from its index, and the bands ask IsRectoNumber with the number they already hold.
    private static bool IsRectoNumber(int physicalPageNumber) => physicalPageNumber % 2 == 1;

    private bool IsRecto(int pageIndex) => IsRectoNumber(_pagesBefore + pageIndex + 1);

    // The content area as the page with this 0-based index offers it. The width and the vertical
    // extent are the same on every page, so a wrap measured on one page holds on the next; only
    // the left edge moves, to the outside margin on a verso page.
    private LayoutBox AreaOnPage(LayoutBox area, int pageIndex) =>
        MirrorMargins && !IsRecto(pageIndex)
            ? new LayoutBox(_margins.Right, area.Y, area.Width, area.Height)
            : area;

    // Decides, before anything is laid out, whether every list of the document is its twin
    // (ListLayoutRun), and records which of the three document-wide rules said so. Every element
    // the document holds itself is read here, and every list sits at the top level: no built-in
    // renderer builds one. The decision is taken once, from what was added, so the measuring pass
    // that counts pages and the pass that draws lay every list out the same way.
    //
    // The first rule is the renderer: a renderer the library does not ship reads where it starts
    // and lays out whatever it holds, a list among it, through a context of its own making, and
    // nothing here can read what it will do. Where the lists before it put it can decide whether
    // the document saves, and a document whose passes disagreed about where a list fell would
    // count pages the saved file does not have. A renderer of this library that is not in this
    // assembly and holds no list does not count (IListFreeRenderer).
    //
    // The second rule is the font: a list that holds an entry the document cannot draw turns every
    // list into its twin.
    //
    // The third rule is the limit: a built-in element after a list whose continuations can come
    // near the limit starts where the lists before it put it, and the limit's verdict for it
    // depends on where that is. 2.3.3 saved or refused it according to where the twin lists put
    // it, so the lists are the twins. The bound is ContinuationBoundOf's, which holds for either
    // placement, and an element whose bound is within the limit cannot reach it from any start.
    // Each rule is tested whatever the others have found, so the causes the scan records do not
    // depend on the order of the elements, and the 207 of a list names every cause the scan found.
    // Removed in 3.0 (#688).
    private void ScanLists(ListLayoutRun listRun)
    {
        var listBefore = false;
        foreach (var renderer in _renderers)
        {
            if (renderer is ListRenderer list)
            {
                if (listBefore && ContinuationBoundOf(renderer) > ContinuationLimit)
                    listRun.NoteNearTheLimit();
                listBefore = true;
                if (ListRenderer.HoldsUndrawableEntry(list.Element, listRun)) listRun.NoteUndrawable();
            }
            else if (renderer.GetType().Assembly != typeof(ListRenderer).Assembly)
            {
                if (renderer is not IListFreeRenderer) listRun.NoteCallerRenderer();
            }
            else if (listBefore && ContinuationBoundOf(renderer) > ContinuationLimit)
            {
                listRun.NoteNearTheLimit();
            }
        }
    }

    // An upper bound on the page continuations a built-in renderer's placement can take from any
    // start, so that a bound within the limit says the element cannot reach it however far a list
    // before it moved its start. Every Partial a renderer returns consumes at least one line, entry
    // or row, a Nothing the loop retries can only come first, and the piece that ends adds one, so
    // the continuations are at most 2 plus the lines, entries and rows. The count is capped as it
    // is summed: a bound past the limit is all the caller asks. Elements that never return a
    // Partial take at most a retry and a piece. A renderer built with a negative start, which the
    // constructors ask callers not to pass, is the exception. A built-in renderer this does not
    // name is taken to be near the limit.
    //
    // What a line holds, for each kind that wraps text, read from the renderer:
    //  - A paragraph, and a heading, which wraps through one: no step rewrites the text. A hard
    //    break ends a line and is a character, and a word wider than the line is cut at one scalar
    //    at least per line, so the lines are at most the characters plus one. A tab stop only
    //    places text; a line is committed only before a word or a break, which are characters.
    //  - A list: each entry is its text's lines in a paragraph of that kind, and a marker that
    //    wraps moves the entry whole, so an entry adds at most max(1, its characters).
    //  - A table: a cell reads its line ends (#679) and wraps each line at spaces, cutting a word
    //    at one scalar at least per line. A line end is a character and ends one line, so the
    //    cell holds at most one line more than its characters, and a cell built from runs is its
    //    runs' text joined, wrapped at the same points, so it holds no more. Any other control
    //    white-space character reads as one space, and a cell read unbroken, as the table's twin
    //    reads every cell, holds at most max(1, its characters) lines. A row is cut only where
    //    every cell holds a line on a fresh page, so each piece after the first takes at least one
    //    line from every cell that has lines left, and a cut row ends in at most one call more than
    //    its characters. The first piece can leave the cell with the most lines untouched, when the
    //    room left holds the shorter lines of the others. That happens in a call that also
    //    finishes the row before it, which then needs no call of its own, or in the table's first
    //    call, which the 2 covers. A row counts one more than the characters of its cells.
    //  - Preformatted text rewrites the text before it wraps: PreformattedText.Expand replaces
    //    each tab by 1 to TabWidth spaces, and that is the only rewrite. A line end is a
    //    character and ends one line, and the wrap cuts an expanded line after at least one
    //    cluster, which is at least one character. So the lines are at most 1 plus the
    //    characters with a tab counted as TabWidth, which is what the count below is.
    // Removed in 3.0 (#688).
    internal long ContinuationBoundOf(IRenderer renderer)
    {
        var cap = (long)ContinuationLimit + 1;
        long total = 2;
        switch (renderer)
        {
            case ParagraphRenderer paragraph:
                var runs = paragraph.Paragraph.Runs;
                for (var i = 0; i < runs.Count && total <= cap; i++)
                    total += runs[i].Text.Length + (runs[i].IsReference ? ReferenceBound : 0);
                return total;

            case HeadingRenderer heading:
                return total + heading.Element.Text.Length
                    + PageReferences.Count(heading.Element.BackingRuns) * ReferenceBound;

            case PreformattedRenderer preformatted:
                var pre = preformatted.Element;
                return total + pre.Text.Length + (long)pre.Text.AsSpan().Count('\t') * (pre.TabWidth - 1);

            case ListRenderer list:
                var items = list.Element.Items;
                for (var i = 0; i < items.Count && total <= cap; i++)
                {
                    total += Math.Max(1, items[i].Text.Length + PageReferences.Count(items[i].OwnRuns) * ReferenceBound);
                    if (items[i].Children is not { } children) continue;
                    for (var c = 0; c < children.Count && total <= cap; c++)
                        total += Math.Max(1, children[c].Text.Length + PageReferences.Count(children[c].OwnRuns) * ReferenceBound);
                }

                return total;

            case TableRenderer table:
                var rows = table.Element.Rows;
                for (var r = 0; r < rows.Count && total <= cap; r++)
                {
                    total++;
                    var cells = rows[r].Cells;
                    for (var c = 0; c < cells.Count && total <= cap; c++)
                        total += (cells[c].Content?.Length ?? 0)
                            + (cells[c].HoldsReferences ? PageReferences.Count(cells[c].Runs) * ReferenceBound : 0);
                }

                return total;

            case LayoutImageRenderer or LineSeparatorRenderer or PieChartRenderer or PageBreakRenderer:
                return total;

            default:
                return long.MaxValue;
        }
    }

    // Only the renderers added to this document are searched, not ones a renderer holds inside
    // itself.
    private List<IPlanDependent>? FindPlanDependents()
    {
        List<IPlanDependent>? found = null;

        // Once the document holds a page reference, the table of them is the first plan dependent.
        if (_navigation.References is { } references) (found ??= []).Add(references);
        foreach (var renderer in _renderers)
            if (renderer is IPlanDependent dependent)
                (found ??= []).Add(dependent);
        return found;
    }

    /// <summary>
    /// Runs one pass over every renderer. Returns the pages it counted: in a measuring pass, one
    /// more than the pages it moved past, so a band with no content still counts one page.
    /// </summary>
    private int RunPass(bool measure, int totalPages)
    {
        _measuring = measure;
        _pageIndex = 0;
        _placedOnPage = false;
        _keepResolvedUntil = 0;
        _keepRelaxedPending = false;
        _pendingBreak = PageBreakMode.None;
        _pendingBreakers = null;
        _placements = new PlacementSink();
        if (measure)
        {
            _measureArea = ContentArea;
            _measureY = _margins.Top + HeaderHeight;
            MeasureRuns++;
        }
        else
        {
            // Ranges are kept by kernel page index, the one {page} and the file use. The document's
            // own range is the earliest, so a break's range on the same page replaces it.
            _labels = new PageLabelMap();
            if (DocumentPageLabel is { } first) _labels.Add(_pagesBefore, first);
        }

        for (var i = 0; i < _renderers.Count; i++)
        {
            // The diagnostics location is that of the element the document added, set here and not
            // in PlaceRenderer: its variable becomes the overflow renderer, which for a heading is
            // a plain paragraph renderer, and mapping that would call a heading's second page a
            // paragraph. Diagnostics are reported from the drawing pass only, so the measuring
            // pass leaves them alone.
            if (!measure)
            {
                _sink.CurrentKind = KindOf(_renderers[i]);
                _sink.CurrentIndex = i;
            }

            var renderer = _renderers[i];
            var own = renderer is IPageBreakSource source ? source.BreakBefore : PageBreakMode.None;
            if (renderer is PageBreakRenderer)
            {
                // Never laid out: a break has no size. It joins the pending one.
                if (own > _pendingBreak) _pendingBreak = own;
                if (_collect) (_pendingBreakers ??= []).Add(renderer);
                continue;
            }

            var mode = own > _pendingBreak ? own : _pendingBreak;
            if (mode != PageBreakMode.None)
                ApplyBreak(mode, renderer, own != PageBreakMode.None, totalPages);

            // After the break, so a run that a break starts is at the top of its page and is not
            // moved off it.
            if (i >= _keepResolvedUntil && renderer is IKeepSource { KeepWithNext: true })
                ResolveKeepRun(i, totalPages);

            PlaceRenderer(renderer, totalPages);
        }

        // A break with nothing after it is dropped: it would only add a trailing blank page.
        _pendingBreak = PageBreakMode.None;
        _pendingBreakers = null;

        if (!measure)
        {
            _sink.CurrentKind = LayoutElementKind.Document;
            _sink.CurrentIndex = null;
            FinishCurrentPage(totalPages);
            if (_guard is not null && _placements.Count != _guard.Count)
                throw PaginationChanged();

            // One call per range, after every page exists: the kernel refuses a range that starts
            // at or after the page count when the file is written.
            foreach (var (start, range) in _labels.Ranges)
                _pdf.AddPageLabelRange(start, range.Style, range.Prefix, range.FirstNumber);
            _drawSink = _placements;
            _drawPages = _pageIndex + 1;
            return _drawPages;
        }

        _measureSink = _placements;
        _measurePages = _pageIndex + 1;
        return _measurePages;
    }

    private int MeasureUntilStable(List<IPlanDependent> dependents)
    {
        var history = new List<string>[dependents.Count];
        for (var i = 0; i < history.Length; i++)
            history[i] = [];
        var changedLast = new bool[dependents.Count];
        var pageCounts = new List<int>(MaxMeasureRuns);

        var plan = PaginationPlan.Placeholder;
        var previousPages = -1;
        var references = dependents.OfType<PageReferenceTable>().FirstOrDefault();
        for (var run = 1; run <= MaxMeasureRuns; run++)
        {
            var anyChanged = false;
            for (var i = 0; i < dependents.Count; i++)
            {
                changedLast[i] = dependents[i].Resolve(plan);
                anyChanged |= changedLast[i];
                history[i].Add(dependents[i].ResolvedText);
            }

            // A resolve that changed a text changed the layout without changing the key a verdict
            // is kept under, so those verdicts are not valid for this run (#686). Removed in 3.0
            // (#688).
            if (anyChanged) _continuationVerdicts = null;

            // The layout is a function of the references' strings, so a vector that equals one two
            // or more runs back is a cycle, and the loop would only repeat it until the cap. The
            // vector is compared string by string, never joined.
            if (references is not null && references.RepeatedRun() >= 0)
                throw new InvalidOperationException(references.DescribeUnstable(
                    "the labels of this pass are those of a pass two or more passes back"));

            var pages = RunPass(measure: true, totalPages: 0);
            pageCounts.Add(pages);

            // Run 1 has no earlier run to equal. From run 2, a run that resolved nothing new laid
            // out the same strings as the run before it, so its plan is that run's plan.
            if (run >= 2 && !anyChanged && pages == previousPages)
            {
                // A label that prints nothing is refused once the labels have settled.
                references?.RefuseBlankLabels();
                return pages;
            }

            plan = new PaginationPlan(_measureSink!.Placements, pages, DocumentPageLabel, _pagesBefore);
            previousPages = pages;
        }

        if (references is not null && changedLast[dependents.IndexOf(references)])
            throw new InvalidOperationException(references.DescribeUnstable(
                $"the labels were still changing after {MaxMeasureRuns.ToString(System.Globalization.CultureInfo.InvariantCulture)} layout passes"));
        throw PaginationDidNotSettle(dependents, history, changedLast, pageCounts);
    }

    private static InvalidOperationException PaginationDidNotSettle(
        List<IPlanDependent> dependents, List<string>[] history, bool[] changedLast, List<int> pageCounts)
    {
        const int named = 3;
        var parts = new List<string>(named);
        var unstable = 0;
        for (var i = 0; i < dependents.Count; i++)
        {
            if (!changedLast[i]) continue;
            if (++unstable > named) continue;
            parts.Add(
                $"the text for '{dependents[i].TargetName}' read " +
                string.Join(", ", history[i].Select(s => $"'{s}'")));
        }

        var more = unstable > named ? $", and {unstable - named} more" : "";
        // No dependent changed in the last run, so the page count was what kept moving.
        var cause = parts.Count > 0
            ? string.Join("; ", parts) + more
            : "the page count read " + string.Join(", ", pageCounts);
        return new InvalidOperationException(
            $"Pagination did not settle after {MaxMeasureRuns} layout passes: {cause}.");
    }

    private static InvalidOperationException PaginationChanged() =>
        new("Pagination changed between the measuring and the drawing pass: the drawing pass " +
            "reported a placement that differs from the last measuring run's, so a renderer " +
            "answered differently the later time or a band was replaced.");

    // FreshPageArea is the area of the page a Nothing answer is retried on: the next page, or, for
    // the retry itself, the page it is already on. Under mirrored margins the two differ in x.
    // pagesAhead is how far the page the area belongs to is from the page the loop is on, so that a
    // probe of the next page names the page after it as the one a Nothing is retried on.
    //
    // Every call addresses its context to the instance it calls, in both passes and in every
    // simulation, so a renderer can tell a context the document built for it from one that a
    // caller's renderer passed on or made (#686). Removed in 3.0 (#688), which lays a built-in
    // renderer out one way under every context.
    private LayoutResult CallLayout(IRenderer renderer, LayoutBox area, bool lineCounts, ContinuationSession session, bool retry = false, bool atTop = false, int pagesAhead = 0)
    {
        LayoutCalls++;
        session.Called = renderer;
        session.Save = ListLayoutRun.CurrentSave;
        var fresh = AreaOnPage(_measuring ? _measureArea : ContentArea, _pageIndex + pagesAhead + (retry ? 0 : 1));

        // The line counts are read for a paragraph renderer the caller added, and for the pieces
        // its splits return. A renderer that wraps one, and the pieces that wrapper returns, are
        // handed a context without the flag.
        return renderer.Layout(new LayoutContext(area)
        {
            FreshPageArea = fresh,
            AtPageTop = retry || atTop,
            HonourLineCounts = lineCounts,
            Session = session,
        });
    }

    // True when the cursor is at the top of the content area, where no later page is better. The
    // cursor equals the area's Y exactly there; see OfferedArea.
    private bool AtPageTop => Cursor == (_measuring ? _measureArea : ContentArea).Y;

    // Tells the renderer whose result was just accepted to report where it landed. Called before
    // the cursor moves, in both passes, so the two passes record the same positions.
    private void ReportPlacements(IRenderer? accepted)
    {
        if (!_collect || _simulating || accepted is not IPlacementSource source) return;

        var first = _placements.Count;
        _placements.Enter(accepted, _pageIndex);
        source.ReportPlacements(_placements);

        // The drawing pass turns each anchor placement into a named destination, from the same
        // report the measuring pass made, so the destination and a page reference to the same
        // element cannot disagree. It is the one place an anchor becomes a destination (#600 adds
        // a structure destination for the same placement here).
        if (!_measuring && _navigation.HasAnchors)
            for (var i = first; i < _placements.Count; i++)
                if (_placements[i].Anchor is { } name && _navigation.TryRegister(name))
                    _pdf.AddNamedDestination(name, _currentPage!, 0, _pageSize.Height - _placements[i].Y);

        if (_measuring || _guard is null) return;
        for (var i = first; i < _placements.Count; i++)
            if (i >= _guard.Count
                || _guard[i].PageIndex != _placements[i].PageIndex
                || !ReferenceEquals(_guard[i].Key, _placements[i].Key))
                throw PaginationChanged();
    }

    // Applies the break in force before `renderer` is placed. The strongest pending mode is
    // applied once: after any break the current page is empty, so a later Page adds nothing and a
    // later Recto only settles parity, and one application gives the pages the breaks would give
    // one at a time.
    private void ApplyBreak(PageBreakMode mode, IRenderer renderer, bool ownBreak, int totalPages)
    {
        _pendingBreak = PageBreakMode.None;

        // A page nothing has been placed on is already new, so consecutive breaks, and a break
        // before the first element, add no page.
        if (_placedOnPage) AdvancePage(totalPages);

        // Parity is the physical position in the kernel document and page 1 is recto; a page label
        // never enters it, because labels may restart.
        if (mode == PageBreakMode.Recto && !IsRecto(_pageIndex))
            AddBlankPage(totalPages);

        if (!_collect) return;

        // One report per applied break, at the top of the content area of the page the element
        // starts on, in both passes.
        var area = PlaceArea;
        var top = new LayoutBox(area.X, area.Y, area.Width, 0);
        if (_pendingBreakers is not null)
            foreach (var breaker in _pendingBreakers)
            {
                _placements.Enter(breaker, _pageIndex);
                _placements.Report(breaker, top);
            }

        // A labelled break starts its range at the page the element after it lands on, which is
        // the page the cursor is on now, after the blank page a recto break adds. Only the drawing
        // pass keeps the ranges; the measuring pass reports the same placements for a plan.
        if (!_measuring && _pendingBreakers is not null)
            foreach (var breaker in _pendingBreakers)
                if (breaker is PageBreakRenderer { PageBreak.PageLabel: { } range })
                    _labels.Add(_pagesBefore + _pageIndex, range);

        _pendingBreakers = null;
        if (ownBreak)
        {
            _placements.Enter(renderer, _pageIndex);
            _placements.Report(renderer, top);
        }
    }

    // A page that holds nothing and shows no running band: it counts as a page in both passes, in
    // {page} and in {pages}, but is never routed through FinishCurrentPage, which draws the bands.
    private void AddBlankPage(int totalPages)
    {
        if (!_measuring)
        {
            FinishCurrentPage(totalPages);
            new PdfCanvas(_pdf.AddPage(_pageSize)).Finish();
        }

        _pageIndex++;
    }

    // Moves the cursor to the top of the next page. The drawing pass commits the open page; the
    // measuring pass has none.
    private void AdvancePage(int totalPages)
    {
        if (!_measuring) FinishCurrentPage(totalPages);
        _placedOnPage = false;
        _pageIndex++;
        if (_measuring) _measureY = _measureArea.Y;
    }

    // The box offered to a Layout call. Mid-page it runs from the cursor to the bottom of the
    // content area. At the top of a page that height, Bottom - Y, is a difference of two sums and
    // can sit one rounding step below the content area's Height, which the retry after a Nothing
    // offers.
    // A renderer that needs exactly Height then returned Nothing here, succeeded on the retry, had
    // that result discarded and was offered the short box again, until the continuation limit
    // (#549). Offering the larger of the two at a page top makes the two agree when the first is
    // the shorter and leaves a page top whose difference is the longer exactly as it was. The
    // cursor equals the area's Y exactly there: EnsurePage, AdvancePage and the measuring pass set
    // it from the expression ContentArea evaluates for Y.
    private LayoutBox OfferedArea(LayoutBox contentArea) => OfferedArea(contentArea, Cursor);

    private static LayoutBox OfferedArea(LayoutBox contentArea, double cursor)
    {
        var height = contentArea.Bottom - cursor;
        if (cursor == contentArea.Y && contentArea.Height > height)
            height = contentArea.Height;
        return new LayoutBox(contentArea.X, cursor, contentArea.Width, height);
    }

    // ── Keep rules ────────────────────────────────────────────────────────────

    // Reported from the drawing pass only, against the element the loop is placing, once per
    // element and code. The measuring pass and a simulated placement make the same decisions and
    // stay silent.
    private void ReportRelaxed(LayoutDiagnosticCode code, string message)
    {
        if (_measuring) return;

        // The run's report goes first, as it did when it was made at the decision, so a later
        // report of the same element follows it.
        ReportPendingKeepWithNext();
        _sink.CurrentPage = _pdf.Pages.Count;
        _sink.Report(code, 0, message, perElement: true);
    }

    // The keep-with-next report of a run is made when its first element has been placed, not when
    // the run was decided: that element can still move to the next page by its own keep-together
    // rule or a line count, and the report names the page it ends up on.
    private void ReportPendingKeepWithNext()
    {
        if (!_keepRelaxedPending || _measuring) return;
        _keepRelaxedPending = false;
        _sink.CurrentPage = _pdf.Pages.Count;
        _sink.Report(LayoutDiagnosticCode.KeepWithNextRelaxed, 0,
            "The elements asked to stay with the one after them and do not fit one page together, or already start a page, so they were placed where they fall.",
            perElement: true);
    }

    // Records the page an element's first piece landed on, for the simulation.
    private void NoteFirstPiece()
    {
        if (_simulating && _simFirstPage < 0) _simFirstPage = _pageIndex;
    }

    /// <summary>
    /// Decides, at the first element of a run of keep-with-next elements, whether the run and the
    /// element after it hold together where the cursor is, and moves the run to the next page when
    /// they would not but would on an empty page. Each element of the group must start on the page
    /// the one before it ends on. The decision is made once for the whole run.
    /// </summary>
    /// <remarks>
    /// A page break, or an element that asks for one, ends the group, and the element before it
    /// keeps nothing. When the run is already at the top of a page, moving it would leave that page
    /// empty, and when it does not hold together on an empty page there is nothing to gain, so the
    /// request is dropped and reported. The group is placed by the same loop that places it for
    /// real, on a copy of the cursor, so the keep-together and line-count rules of its elements
    /// take part in deciding where each one starts.
    /// </remarks>
    private void ResolveKeepRun(int first, int totalPages)
    {
        var last = first;
        var after = -1;
        while (last + 1 < _renderers.Count)
        {
            var next = _renderers[last + 1];
            if (next is IPageBreakSource { BreakBefore: not PageBreakMode.None }) break;
            if (next is IKeepSource { KeepWithNext: true })
            {
                last++;
                continue;
            }

            after = last + 1;
            break;
        }

        _keepResolvedUntil = last + 1;
        var end = after >= 0 ? after : last;
        if (end == first) return;

        if (!_measuring) EnsurePage();
        if (RunHolds(first, end, fromFreshPage: false)) return;

        if (!AtPageTop && RunHolds(first, end, fromFreshPage: true))
        {
            AdvancePage(totalPages);
            return;
        }

        _keepRelaxedPending = !_measuring;
    }

    // Places elements first..end on a copy of the cursor, from the current page or from the next
    // one, and answers whether each starts on the page the one before it ended on. The last one
    // is placed only as far as its first piece, because nothing depends on where it ends.
    private bool RunHolds(int first, int end, bool fromFreshPage)
    {
        var saved = (_measuring, _measureArea, _measureY, _pageIndex, _placedOnPage, _simulating, _simFirstOnly, _simFirstPage);
        var cursor = Cursor;
        try
        {
            _measuring = true;
            _simulating = true;
            _measureArea = ContentArea;
            _measureY = cursor;
            if (fromFreshPage)
            {
                _pageIndex++;
                _measureY = _measureArea.Y;
            }

            var previousEnd = -1;
            for (var k = first; k <= end; k++)
            {
                _simFirstPage = -1;
                _simFirstOnly = k == end;
                PlaceRenderer(_renderers[k], 0);
                if (k > first && _simFirstPage != previousEnd) return false;
                previousEnd = _pageIndex;
            }

            return true;
        }
        finally
        {
            (_measuring, _measureArea, _measureY, _pageIndex, _placedOnPage, _simulating, _simFirstOnly, _simFirstPage) = saved;
        }
    }

    // The decision, its probes and its memo below exist only to keep the 2.3.3 continuation verdict
    // for a renderer whose new layout differs from 2.3.3's. They are removed in 3.0 (#688), which
    // lays a built-in renderer out one way; the limit itself stays as a test seam.
    //
    // The most continuations one element may take; the constant, except in tests that probe the
    // decision below at a size that is cheap to reach.
    internal int ContinuationLimit { get; init; } = MaxContinuationsPerElement;

    /// <summary>Layout-only placements the last <see cref="RunLayout"/> ran, for tests.</summary>
    internal int ContinuationProbes { get; private set; }

    /// <summary>
    /// Exceptions other than the limit that probes caught in the last <see cref="RunLayout"/>, read
    /// by tests. A probe leaves such an exception for the placement that follows, which raises it
    /// again if it uses the layout that threw.
    /// </summary>
    internal int ContinuationProbeFaults { get; private set; }

    // The verdict of a decision that ran probes, kept for the start it was made from: the element
    // the document placed, where on the page it starts, whether it starts at the retry after a
    // Nothing, and the continuations already counted. Both passes and every simulation of the keep
    // rules start the same element from the same place, so each asks once. The x position is not
    // part of the key: mirrored margins move only x, so a reporting renderer's answer must not
    // depend on x, only on the width. A measuring run of a plan-dependent document starts it from
    // the same place too, but with a different text, so the verdicts are dropped whenever a resolve
    // changes one. Y and Height are both kept: they determine each other only up to rounding.
    private Dictionary<ContinuationKey, ContinuationMode>? _continuationVerdicts;

    // The session of the placement in progress, reused for each element. A placement never starts
    // another one: a probe runs the loop with a session of its own, and the keep rules reach
    // PlaceRenderer only from the pass loop, so nothing is lost by resetting it for each element.
    private ContinuationSession? _session;

    /// <summary>
    /// Calls that ended with a report from a child a built-in parent addressed and no report from
    /// the parent, in the last <see cref="RunLayout"/>, read by tests. Each is a parent that lets
    /// its child lay out the new way while the document still has the twin to decide.
    /// </summary>
    internal int UnreportedChildReports { get; private set; }

    /// <summary>
    /// Whether a call that ends with an unreported child fails a Debug build. Tests that provoke it
    /// switch it off to read <see cref="UnreportedChildReports"/>.
    /// </summary>
    internal bool AssertOnUnreportedChild { get; init; } = true;

    private readonly struct ContinuationKey(IRenderer root, double y, double height, int continuations, bool retry)
        : IEquatable<ContinuationKey>
    {
        private readonly IRenderer _root = root;
        private readonly double _y = y;
        private readonly double _height = height;
        private readonly int _continuations = continuations;
        private readonly bool _retry = retry;

        // The renderer is compared by reference: a renderer may define Equals, and two equal ones
        // are still two elements.
        public bool Equals(ContinuationKey other) =>
            ReferenceEquals(_root, other._root) && _y.Equals(other._y) && _height.Equals(other._height)
            && _continuations == other._continuations && _retry == other._retry;

        public override bool Equals(object? obj) => obj is ContinuationKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_root), _y, _height, _continuations, _retry);
    }

    // Decides between a renderer's new layout and its twin, at the first call that reported (#686).
    // `continuations` is the count the loop holds where the next call would start.
    //
    // A bound within what is left of the limit (bound <= L', where L' is the limit minus the
    // continuations counted) needs no probe, whatever the element's real count: the new layout is
    // trusted not to reach the limit, and the bound covers the twin's continuations too. There
    // the new layout alone decides what is refused, so it must refuse where its twin refuses and
    // save where its twin saves. Above it the element is laid out twice without drawing, the twin
    // first and the new layout second, and the new layout is chosen only when both finish
    // cleanly. The twin is chosen when the twin reaches the limit or throws, which keeps a refusal
    // 2.3.3 made with the twin's own exception, and when the new layout reaches the limit or
    // throws, which keeps a save 2.3.3 made.
    //
    // At the retry site the loop offers the next call the page-top box, which can be one rounding
    // step taller than the box the retry got (the two are computed differently). A probe that
    // starts at that next call would skip the retry's own check, so the twin probe first calls the
    // twin on the retry's box, and a Nothing there counts as a fault, which keeps the refusal
    // 2.3.3 raised at the retry. The new layout's retry is the call that reported, and SettleCall
    // chooses the twin when that call answered Nothing, so the new layout is probed only after a
    // retry that fit, and then from the call the real placement makes next.
    private ContinuationMode DecideContinuation(IRenderer renderer, ContinuationSession session, int continuations, bool keepTogether, bool lineCounts, bool retry)
    {
        if (session.Bound <= (long)ContinuationLimit - continuations) return ContinuationMode.New;

        var offered = OfferedArea(PlaceArea);
        var key = new ContinuationKey(renderer, offered.Y, offered.Height, continuations, retry);
        if (_continuationVerdicts is not null && _continuationVerdicts.TryGetValue(key, out var known))
            return known;

        var twin = session.Reporter!.Twin();
        var verdict =
            ProbeContinuations(twin, ContinuationMode.Twin, keepTogether, lineCounts, continuations, retry) == ProbeOutcome.Clean
            && ProbeContinuations(renderer, ContinuationMode.New, keepTogether, lineCounts, continuations, retryFirst: false) == ProbeOutcome.Clean
                ? ContinuationMode.New
                : ContinuationMode.Twin;
        (_continuationVerdicts ??= [])[key] = verdict;
        return verdict;
    }

    private enum ProbeOutcome
    {
        Clean,
        Limit,
        Fault,
    }

    // Places the renderer in one mode on a copy of the cursor, the way RunHolds places the elements
    // of a keep run, and answers Clean when the loop finishes, Limit when it would have thrown for
    // the limit and Fault when anything else stopped it. Nothing is drawn, reported or kept. Any
    // other exception is counted and left for the placement that follows. Running out of memory
    // and a cancellation are not ours to absorb. At the retry site (retryFirst) the renderer is
    // first called on the retry's box with the retry's flags, and a Nothing there is a Fault: the
    // real placement refuses the element too, as the loop does for a retry that answers Nothing.
    private ProbeOutcome ProbeContinuations(IRenderer renderer, ContinuationMode mode, bool keepTogether, bool lineCounts, int continuations, bool retryFirst)
    {
        ContinuationProbes++;
        var probe = new ContinuationSession(mode, probing: true);
        var saved = (_measuring, _measureArea, _measureY, _pageIndex, _placedOnPage, _simulating, _simFirstOnly, _simFirstPage);
        var cursor = Cursor;
        var fault = false;
        try
        {
            _measuring = true;
            _simulating = true;
            _simFirstOnly = false;
            _measureArea = ContentArea;
            _measureY = cursor;
            if (retryFirst && CallLayout(renderer, PlaceArea, lineCounts, probe, retry: true).Status == LayoutResult.Outcome.Nothing)
            {
                ContinuationProbeFaults++;
                fault = true;
            }
            else
            {
                PlaceLoop(renderer, 0, keepTogether, lineCounts, probe, continuations);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            ContinuationProbeFaults++;
            fault = true;
        }
        finally
        {
            (_measuring, _measureArea, _measureY, _pageIndex, _placedOnPage, _simulating, _simFirstOnly, _simFirstPage) = saved;
        }

        return probe.LimitHit ? ProbeOutcome.Limit : fault ? ProbeOutcome.Fault : ProbeOutcome.Clean;
    }

    // What a call that reported, or that a child reported under, leaves to do, reached only when
    // the session says so (Pending), so a call that reports nothing costs the loop one field read.
    // When the decision is the twin, the call is made again with the twin, which the caller then
    // holds, unless the caller asked to redo more than the call (recall false): the keep-together
    // probe redoes the whole iteration. Removed in 3.0 (#688), with the decision. It returns the
    // renderer instead of taking it by reference, because a by-reference variable would keep the
    // loop's own out of a register for every call.
    private (LayoutResult Result, IRenderer Renderer) SettleCall(IRenderer renderer, LayoutResult result, LayoutBox area, bool lineCounts, ContinuationSession session, int continuations, bool keepTogether, bool recall, bool retry = false, bool atTop = false, int pagesAhead = 0)
    {
        var reported = session.TakeReport();
        if (session.Strays != 0) NoteStrays(session, reported);
        if (!reported) return (result, renderer);

        // A call that reports starts the placement it reports for, as the loop's next call would.
        // The retry has already moved to the next page, where the count has gone up by the Nothing.
        // A retry that answers Nothing is refused by the loop; the twin decides that refusal, as it
        // did in 2.3.3, so it is chosen without probing.
        var mode = retry && result.Status == LayoutResult.Outcome.Nothing
            ? ContinuationMode.Twin
            : DecideContinuation(renderer, session, retry ? continuations + 1 : continuations, keepTogether, lineCounts, retry);
        session.Mode = mode;
        if (mode != ContinuationMode.Twin) return (result, renderer);

        session.TwinChosen = true;

        renderer = session.Reporter!.Twin();
        return (recall ? CallLayout(renderer, area, lineCounts, session, retry, atTop, pagesAhead) : result, renderer);
    }

    // A child that reported under a built-in parent's context while the mode was undecided, in a
    // call where the parent made no report: the child laid out the new way, the first piece then
    // fixes the twin, and the output is neither layout. Removed in 3.0 (#688).
    private void NoteStrays(ContinuationSession session, bool parentReported)
    {
        session.ClearStrays();
        if (parentReported) return;

        UnreportedChildReports++;
        Debug.Assert(!AssertOnUnreportedChild, "A built-in parent addressed a reporting child and did not report for itself.");
    }

    private void PlaceRenderer(IRenderer renderer, int totalPages)
    {
        // Read once, from the element the caller added: the overflow this loop continues with
        // holds the same element and must not ask again.
        var keepTogether = renderer is IKeepSource { KeepTogether: true };
        var lineCounts = renderer is ParagraphRenderer;
        var session = _session ??= new ContinuationSession();
        session.Reset();
        PlaceLoop(renderer, totalPages, keepTogether, lineCounts, session, 0);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void PlaceLoop(IRenderer renderer, int totalPages, bool keepTogether, bool lineCounts, ContinuationSession session, int continuations)
    {
        var split = false;
        while (true)
        {
            if (!_measuring) EnsurePage();

            var availableArea = OfferedArea(PlaceArea);
            var atTop = AtPageTop;

            var result = CallLayout(renderer, availableArea, lineCounts, session, atTop: atTop);
            if (session.Pending)
                (result, renderer) = SettleCall(renderer, result, availableArea, lineCounts, session, continuations, keepTogether, recall: true, atTop: atTop);

            // Checked on every call until the element has split once, not only on the first: a
            // Nothing moves the element to the top of the next page, and a keep-together element
            // that cannot be placed whole there splits there, which is reported the same way.
            if (keepTogether && !split && result.Status == LayoutResult.Outcome.Partial)
            {
                // The element would split. It moves whole when an empty page holds it; at the top
                // of a page there is no better page, so it splits there as well. The probe offers
                // the box a page top is offered, so what it answers is what the move gets.
                var next = AreaOnPage(_measuring ? _measureArea : ContentArea, _pageIndex + 1);
                LayoutResult whole;
                if (atTop)
                {
                    whole = result;
                }
                else
                {
                    var before = renderer;
                    var nextArea = OfferedArea(next, next.Y);
                    whole = CallLayout(renderer, nextArea, lineCounts, session, atTop: true, pagesAhead: 1);
                    if (session.Pending)
                        (whole, renderer) = SettleCall(renderer, whole, nextArea, lineCounts, session, continuations, keepTogether, recall: false, atTop: true, pagesAhead: 1);

                    // The probe reported and the twin was chosen, so the iteration is made again
                    // with the twin. The call before it matched the twin's by the report contract.
                    // Removed in 3.0 (#688), with the decision.
                    if (!ReferenceEquals(before, renderer)) continue;
                }

                if (whole.Status == LayoutResult.Outcome.Full)
                    result = LayoutResult.Nothing();
                else
                    ReportRelaxed(LayoutDiagnosticCode.KeepTogetherRelaxed,
                        "The element asked to be kept whole and does not fit on an empty page, so it was split.");
            }

            switch (result.Status)
            {
                case LayoutResult.Outcome.Full:
                    _placedOnPage = true;
                    NoteFirstPiece();
                    ReportPendingKeepWithNext();
                    ReportPlacements(renderer);
                    if (!_measuring) DrawRenderer(renderer);
                    Cursor = result.OccupiedArea!.Value.Bottom;
                    return;

                case LayoutResult.Outcome.Partial:
                    // A piece placed with no report is the twin's, so the rest of the placement is.
                    // A Full piece ends the placement, so only this case needs the rule. Removed in
                    // 3.0 (#688), with the decision.
                    if (session.Mode == ContinuationMode.Undecided) session.Mode = ContinuationMode.Twin;
                    split = true;
                    _placedOnPage = true;
                    NoteFirstPiece();
                    ReportPendingKeepWithNext();
                    ReportPlacements(result.SplitRenderer);
                    if (result.SplitRenderer is ParagraphRenderer { MinLinesRelaxed: true })
                        ReportRelaxed(LayoutDiagnosticCode.MinLinesRelaxed,
                            "The paragraph's line counts could not be met at the top of a page, so it was split where the page ends.");
                    if (!_measuring) DrawRenderer(result.SplitRenderer!);
                    if (_simFirstOnly) return;
                    AdvancePage(totalPages);
                    renderer = result.OverflowRenderer!;
                    break;

                case LayoutResult.Outcome.Nothing:
                    AdvancePage(totalPages);
                    if (!_measuring) EnsurePage();
                    var retry = CallLayout(renderer, PlaceArea, lineCounts, session, retry: true);
                    if (session.Pending)
                        (retry, renderer) = SettleCall(renderer, retry, PlaceArea, lineCounts, session, continuations, keepTogether, recall: true, retry: true);
                    if (retry.Status == LayoutResult.Outcome.Nothing)
                    {
                        if (!_measuring) FinishCurrentPage(totalPages);

                        // The renderer's own reason follows the standard sentence, so every
                        // message starts as it always has and a caller matching on that start
                        // still matches.
                        throw retry.Reason is { } reason
                            ? new InvalidOperationException(ElementTooTall().Message + " " + reason)
                            : ElementTooTall();
                    }
                    break;

                default:
                    // Unreachable: LayoutResult's constructor is private and its three factories
                    // pass only these three members. Kept because a fourth Outcome used to make
                    // this method return and skip the element; under the loop it would instead
                    // spin to the cap and blame the renderer (#459).
                    throw new InvalidOperationException($"Unknown layout outcome {result.Status}.");
            }

            if (++continuations > ContinuationLimit)
            {
                // A probe only learns that the limit was reached; the placement that follows
                // throws it.
                if (session.Probing)
                {
                    session.LimitHit = true;
                    return;
                }

                // Commit the open page rather than leave its canvas unflushed; the page still
                // stays in the document. Both branches reach this throw, but only the Nothing
                // branch can arrive with a page still open, because the Partial branch has
                // already finished one. The measuring pass has no page to commit.
                if (!_measuring) FinishCurrentPage(totalPages);
                throw TooManyContinuations();
            }
        }
    }

    // The first refusal of a non-finite operand that the canvas raised while an element was drawn.
    // The first refusal is kept and the layout carries on, so an element that is too tall, or any
    // other refusal later in the document, surfaces as its own exception type. It is raised when the
    // layout has finished and nothing else threw.
    private ArgumentException? _deferredCanvasRefusal;

    private static bool IsCanvasNonFiniteRefusal(ArgumentException ex) =>
        ex.ParamName is null
        && ex.Message.StartsWith("PDF does not support NaN or Infinity as a real number value.", StringComparison.Ordinal);

    private void DrawRenderer(IRenderer renderer)
    {
        if (_trackChapters) NoteChapter(renderer);

        var pageBounds = new LayoutBox(0, 0, _pageSize.Width, _pageSize.Height);
        var ctx = new DrawContext(_currentCanvas!, pageBounds, _currentRendererCtx!, _pdf, _currentPage!)
        {
            Diagnostics = _sink,
        };
        // Reports come from this pass only. The page counting pass lays out without drawing, and a
        // wrap is cached per renderer, so a report raised from Layout could run twice.
        _sink.CurrentPage = _pdf.Pages.Count;
        _sink.Conformance = _pdf.Conformance;
        try
        {
            renderer.Draw(ctx);
        }
        catch (ArgumentException ex) when (IsCanvasNonFiniteRefusal(ex))
        {
            _deferredCanvasRefusal ??= ex;
        }

        _sink.SyncEncodingWarnings(_currentCanvas!, report: true);
    }

    // ── Running bands ─────────────────────────────────────────────────────────

    private void DrawRunningBands(
        PdfCanvas canvas,
        RendererContext rendererCtx,
        PdfPage page,
        int pageNumber,
        int totalPages)
    {
        var pageBounds = new LayoutBox(0, 0, _pageSize.Width, _pageSize.Height);
        var ctx = new DrawContext(canvas, pageBounds, rendererCtx, _pdf, page) { Diagnostics = _sink };
        _sink.CurrentPage = pageNumber;
        _sink.Conformance = _pdf.Conformance;
        _sink.CurrentIndex = null;

        if (Header is not null)
        {
            _sink.CurrentKind = LayoutElementKind.Header;
            var text = ResolveBand(
                Header, ref _headerText, pageNumber, totalPages, LabelFor(Header, pageNumber), ChapterOnPage);
            var bandY = _margins.Top;
            var bandHeight = Header.EffectiveHeight;
            DrawBandText(ctx, canvas, text, Header, RunningBandKind.Header, pageNumber, bandY, bandHeight);
        }

        _sink.SyncEncodingWarnings(canvas, report: true);

        if (Footer is not null)
        {
            _sink.CurrentKind = LayoutElementKind.Footer;
            var text = ResolveBand(
                Footer, ref _footerText, pageNumber, totalPages, LabelFor(Footer, pageNumber), ChapterOnPage);
            var bandY = _pageSize.Height - _margins.Bottom - Footer.EffectiveHeight;
            var bandHeight = Footer.EffectiveHeight;
            DrawBandText(ctx, canvas, text, Footer, RunningBandKind.Footer, pageNumber, bandY, bandHeight);
        }
    }

    // A null template is refused here, where the band is first drawn, as RunningBand.Resolve
    // refuses it. The cached BandText is replaced when the band was: Header and Footer are
    // settable, and a renderer's Draw can replace one between two pages of the same render.
    private static BandText ResolveBand(
        RunningBand band, ref BandText? cache, int pageNumber, int totalPages, string? pageLabel,
        string? chapter)
    {
        if (cache is null || !ReferenceEquals(cache.Template, band.Compiled))
            cache = new BandText(band.Compiled);
        cache.Reset(pageNumber, totalPages, pageLabel, chapter);
        return cache;
    }

    // What {page} prints on this page: its label once the document defines any range, and null
    // otherwise, which leaves the decimal number every template printed before labels existed. A
    // band with no {page} asks for nothing, so a label too long to print is refused only where it
    // would be shown.
    private string? LabelFor(RunningBand band, int pageNumber) =>
        _labels.IsEmpty || band.Compiled.TokenCounts[(int)BandToken.Page] == 0
            ? null
            : _labels.LabelOf(pageNumber - 1);

    // Drops the RendererContext the old signature took and never read: the body reaches everything
    // it needs through DrawContext, which carries the same canvas.
    private void DrawBandText(
        DrawContext ctx,
        PdfCanvas canvas,
        BandText text,
        RunningBand band,
        RunningBandKind kind,
        int pageNumber,
        double bandY,
        double bandHeight)
    {
        var style = band.Style;

        // The band is the one path to a Tf operator that reaches no renderer, so it needs its own
        // check. The size only: a colour is written only when text is drawn, and a band that draws
        // nothing is not refused for one.
        LayoutValidation.ValidateFontSize(style, $"The {kind.ToString().ToLowerInvariant()} band");

        var contentWidth = _pageSize.Width - _margins.Horizontal;
        var (drawn, textWidth) = FitToWidth(style, text, contentWidth);

        BandCharactersProduced += text.Produced;

        if (drawn.Length < text.Length) RecordTruncation(kind, pageNumber, drawn.Length, text.Length);

        // Nothing fits, so nothing is emitted — not an empty text object, and not the marked-content
        // pair either. Returning before SetFont also matters: setting a font registers the font as
        // a page resource, so an empty band would otherwise add an entry to every page's /Font
        // dictionary, and the font object it points at to the file.
        //
        // This is one of the two places the branch moves bytes for a document that was already
        // fine, the other being the colour operator below. A band whose template is empty
        // previously emitted a text object showing an empty string, and now emits nothing;
        // measured, that removes five lines per band placement, and on a document whose only text
        // was such a band it also removes that /Font entry and its font object — the /Font
        // dictionary itself stays, empty, because PdfPage sets it on every page unconditionally.
        // An empty template is one of the shapes the property generator produces, so it is not a
        // corner nobody reaches.
        if (drawn.Length == 0) return;


        // textWidth is bounded by contentWidth for every finite, positive font size, because
        // FitToWidth only ever accumulates a piece whose running total still fits. So no clamp is
        // needed and none of these arms can place the run outside the content box.
        //
        // The bound does not survive a font size that is not finite and positive, which TextStyle
        // validates nowhere. A negative size makes every advance negative, so the break test never
        // fires and the returned width is negative; a NaN size emits a text matrix of NaN, which is
        // not a PDF number. Measured on a 300x400pt page, a negative size put a right-aligned band
        // at x = 290.56 against a content edge of 250. That predates this change, since the old
        // code computed the same width and the same x, and validating the size belongs with the
        // rest of the geometry validation rather than here.
        //
        // Under mirrored margins a verso page has its outside margin on the left, so the page's
        // edges swap and so do Left and Right alignment: a band on the outside stays outside.
        // Anything that is not Center or Right is drawn as Left (Justify and a value the
        // enumeration does not name included), so on a verso page it lands on the right.
        var mirrored = MirrorMargins && !IsRectoNumber(pageNumber);
        var leftEdge = mirrored ? _margins.Right : _margins.Left;
        var rightEdge = mirrored ? _margins.Left : _margins.Right;
        var alignment = band.Alignment;
        if (mirrored)
            alignment = alignment switch
            {
                HorizontalAlignment.Right => HorizontalAlignment.Left,
                HorizontalAlignment.Center => HorizontalAlignment.Center,
                _ => HorizontalAlignment.Right,
            };
        double x = alignment switch
        {
            HorizontalAlignment.Center => leftEdge + (contentWidth - textWidth) / 2,
            HorizontalAlignment.Right => _pageSize.Width - rightEdge - textWidth,
            _ => leftEdge,
        };

        // PDF Y: baseline of text within the band (centred vertically in the band)
        var pdfY = ctx.ToPdfY(bandY + bandHeight - (bandHeight - style.FontSize) / 2);

        // The band's own fill colour is set below, after the font, matching the order
        // ParagraphRenderer and TableRenderer emit. It has to be set unconditionally rather than
        // only when it differs from black, because the two text renderers do not bracket their
        // fill colour in q/Q. LayoutImageRenderer and PieChartRenderer do bracket theirs, and the
        // chart says so in its own comment, but a paragraph's colour and a cell's colour leak.
        // A band is drawn from FinishCurrentPage after the page's content, so without this the
        // band takes whatever colour the last paragraph or cell left set. Measured on a page whose
        // body was red and whose footer style asked for blue: the band's text object held no rg at
        // all and the footer rendered red.
        //
        // This is the other of the two places the branch moves bytes for a document that was
        // already correct, alongside the empty-template return above. Every banded document whose
        // band emits any text carries one extra colour operator per band per page, including those
        // that looked right because the inherited colour happened to be black. A band that fits
        // nothing returns above this and gains none.
        //
        // Running-band text is pagination decoration, not part of the logical structure.
        // Wrap as /Artifact when the document is tagged so PDF/UA validators find no
        // untagged real content.
        if (ctx.Tagged) canvas.BeginArtifactMarkedContent();
        canvas.BeginText();
        TextEmitter.SelectedFont? bandFont = null;
        TextEmitter.SelectFont(ctx, canvas, style, ref bandFont);
        canvas.SetFillColorRgb(style.Color.R, style.Color.G, style.Color.B);
        canvas.SetTextMatrix(1, 0, 0, 1, x, pdfY);
        TextEmitter.Show(canvas, style, drawn);
        canvas.EndText();
        if (ctx.Tagged) canvas.EndMarkedContent();

        // A link in the band's style covers the text as drawn, over the height of the band: one
        // annotation per page. Nothing is written for a context with no page or a rectangle that
        // would be written with a value that is not finite, and nothing is reported for either.
        // Under a PDF/UA-1 claim a URI link is not written, because it would be untagged, and the
        // drop is reported once per band kind with the pages as its count. Removed in 3.0 (#688).
        if (textWidth > 0 && !string.IsNullOrWhiteSpace(drawn)
            && (style.LinkUri is not null || style.LinkAnchor is not null))
        {
            if (style.LinkAnchor is null && ctx.Conformance == PdfConformance.PdfUA1)
                _sink.Report(LayoutDiagnosticCode.LinkNotWritten, 0, TableRenderer.LinkNotWrittenMessage, 1, perElement: true);
            else
                ctx.TryAddLinkAnnotation(
                    new LayoutBox(x, bandY, textWidth, bandHeight), style.LinkUri, style.LinkAnchor, LinkOwner.Band);
        }
    }

    /// <summary>
    /// Keeps the worst cut per band. Worst rather than first because a template carrying a
    /// <c>{page}</c> or <c>{pages}</c> token resolves to a different width on every page, and the
    /// page that lost the most is the one that tells a caller how much shorter the template must
    /// be. Comparing on the dropped count keeps that decision O(1) and needs no history.
    /// </summary>
    private void RecordTruncation(RunningBandKind kind, int pageNumber, int drawn, int resolved)
    {
        var report = new BandTruncationWarning(kind, pageNumber, drawn, resolved);
        ref var slot = ref kind == RunningBandKind.Header
            ? ref _headerTruncation
            : ref _footerTruncation;

        if (slot is not { } existing || report.DroppedCharacters > existing.DroppedCharacters)
            slot = report;
    }

    /// <summary>
    /// The longest prefix of <paramref name="text"/> whose advance width fits
    /// <paramref name="maxWidth"/>, and that width.
    ///
    /// A running band used to be measured whole and emitted whole, so a template wider than the
    /// content box was positioned by a formula that had never been given a bound and went off the
    /// page with every glyph still written into the content stream. Truncating here is what makes
    /// the alignment arithmetic safe, because the width it returns can never exceed maxWidth.
    ///
    /// It walks forward and stops, rather than measuring the whole string first, so the work is
    /// proportional to what fits rather than to what was passed. The text is produced as the walk
    /// asks for it, so the characters of the template that are never reached are never copied
    /// either. That bound has one hole, and it is worth stating its consequence and not only its
    /// mechanism. A glyph may advance zero: the 38 code points U+0000 to U+001F, U+007F and
    /// WinAnsi's five undefined codes measure zero in every face, and every character measures
    /// zero in Symbol and ZapfDingbats, the two of the fourteen Standard 14 faces whose width
    /// tables are absent from the metrics lookup.
    ///
    /// So a band in one of those two faces measures zero however long it is, fits by definition, is
    /// never cut, and is positioned by an alignment formula fed a width of zero. That is the defect
    /// this method exists to fix, surviving for those two faces. Measured: a 500-glyph Symbol
    /// footer on a 300pt page emits all 500 glyphs and reports no truncation. They are a supported
    /// configuration rather than a dead corner, since PdfFontResource omits /Encoding for exactly
    /// them so their symbolic encoding applies and their glyphs carry real advances in a viewer.
    /// The metrics gap is #470; this method cannot close it, having no width to work from.
    ///
    /// Two subtleties. It advances two UTF-16 units for a well-formed surrogate pair, so for
    /// well-formed input every candidate cut lands on a code-point boundary. Malformed input is a
    /// different matter: a lone surrogate is measured and kept as one unit, so a cut can leave one
    /// in the drawn text. The Standard 14 path tolerates that and substitutes the question-mark
    /// glyph; the embedded path throws from char.ConvertToUtf32, and because the walk stops early
    /// that throw now depends on whether the lone surrogate sits inside the fitted prefix. Before
    /// this change the whole template was always measured, so it threw at any position. Malformed
    /// UTF-16 is caller error either way, but the difference is worth knowing.
    ///
    /// And when the whole string fits it takes the width from one measurement of the whole string
    /// rather than from the running total, because the metrics sum integer thousandths and scale
    /// once at the end while this loop scales each piece. Every sweep run of the two orders put the
    /// drift below 1e-11pt — 2.8e-13 over 120,000 strings of up to 60 characters at 10pt, 6.4e-12
    /// over strings of up to 400 at 12pt — and PdfCanvas formats coordinates to five decimals, so
    /// the gap to what the output can even represent is six orders of magnitude. Substituting the
    /// running total moved no byte of any content stream measured and left the suite green. The
    /// whole-string measurement is kept because it is the more accurate width and costs nothing on
    /// a path that has already measured every piece, not because the alternative would visibly
    /// move bands. No test defends this line, since the mutation is invisible to the suite, so
    /// this comment is what defends it.
    /// </summary>
    private static (string Drawn, double Width) FitToWidth(TextStyle style, BandText text, double maxWidth)
    {
        var length = text.Length;
        if (length == 0) return (string.Empty, 0);

        var width = 0.0;
        var cut = 0;

        // The last point at which the run could be cut without splitting a token, and the width up
        // to it. Recorded as the walk passes the space so the fallback costs no second scan.
        var lastSpaceCut = 0;
        var lastSpaceWidth = 0.0;

        var i = 0;
        while (i < length)
        {
            var len = char.IsHighSurrogate(text[i]) && i + 1 < length && char.IsLowSurrogate(text[i + 1])
                ? 2
                : 1;

            var piece = style.MeasureString(text.Slice(i, len));
            if (width + piece > maxWidth) break;

            width += piece;
            i += len;
            cut = i;

            // Only the first space of a run is a cut point. Recording every space would land the
            // cut on the last of a consecutive run and keep the ones before it, so the prefix
            // would end in whitespace and the width returned would charge the alignment formula
            // for ink that is not there — measured, 2.78pt per trailing space at 10pt, which
            // shifts a centred or right-aligned band by that much.
            if (len == 1 && text[i - 1] == ' ' && (i < 2 || text[i - 2] != ' '))
            {
                lastSpaceCut = i - 1;
                lastSpaceWidth = width - piece;
            }
        }

        if (cut == length)
        {
            var whole = text.Prefix(length);
            return (whole, style.MeasureString(whole));
        }

        // Prefer the last word boundary. For a band this is not only tidier: a template ending in
        // a page number cut mid-token would show a number that is simply wrong, where dropping the
        // token shows none. Fall back to the mid-token cut when the fitted prefix holds no interior
        // space, which is the case a single overlong word produces.
        return lastSpaceCut > 0
            ? (text.Prefix(lastSpaceCut), lastSpaceWidth)
            : (text.Prefix(cut), width);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates that the page size is positive and finite, and refuses margins whose sum on either
    /// axis meets or exceeds the page; a NaN sum passes.
    /// </summary>
    private static void ValidateGeometry(PdfRectangle pageSize, EdgeInsets margins)
    {
        if (!double.IsFinite(pageSize.Width) || pageSize.Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize),
                FormattableString.Invariant(
                    $"Page width must be a positive finite number (was {pageSize.Width})."));
        if (!double.IsFinite(pageSize.Height) || pageSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize),
                FormattableString.Invariant(
                    $"Page height must be a positive finite number (was {pageSize.Height})."));

        if (margins.Horizontal >= pageSize.Width)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Horizontal margins ({margins.Horizontal:F1}pt) meet or exceed page width ({pageSize.Width:F1}pt). ") +
                "Reduce the left/right margins.",
                nameof(margins));
        if (margins.Vertical >= pageSize.Height)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"Vertical margins ({margins.Vertical:F1}pt) meet or exceed page height ({pageSize.Height:F1}pt). ") +
                "Reduce the top/bottom margins.",
                nameof(margins));
    }
}
