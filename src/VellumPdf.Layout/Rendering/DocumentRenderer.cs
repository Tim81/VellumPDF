// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// An element needs more than <b>50,000</b> page continuations or is too tall for one page, or
    /// the kernel document has no pages, or this document has already been written, or the kernel
    /// document's conformance level refuses its encryption settings, or an element's input cannot
    /// be laid out, or a registered font lacks a table the write needs.
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
        // either added no pages or leaves the document unable to be written again.
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
        // A refusal belongs to the layout that raised it; a renderer reused for another layout starts clean.
        _deferredCanvasRefusal = null;
        MeasureRuns = 0;
        LayoutCalls = 0;
        _measureSink = null;
        _drawSink = null;
        _guard = null;

        var contentArea = ContentArea;
        if (contentArea.Width <= 0 || contentArea.Height <= 0)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The computed content area has no positive size ({contentArea.Width:F1}×{contentArea.Height:F1}pt). ") +
                "The margins, header, and footer together exceed the page dimensions. " +
                "Reduce the margins or band heights.",
                "margins");

        // A check that must refuse before any renderer has measured goes here, so the document is
        // still saveable once the caller has fixed what it names: a save that fails after the
        // layout started cannot be retried, one refused before it can. Nothing walks the added
        // elements yet.

        // From here the renderers measure and cache; see Document.MarkFailedIfLaidOut.
        HasStartedLayout = true;

        var dependents = FindPlanDependents();

        // Reports are collected only when something reads them: a dependent resolves against them
        // and the guard compares the drawing pass with them. Tests set RecordPlacements.
        _collect = dependents is not null || RecordPlacements;

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
        ReportBandTruncations();

        if (_deferredCanvasRefusal is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_deferredCanvasRefusal).Throw();
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private static LayoutElementKind KindOf(IRenderer renderer) => renderer switch
    {
        ParagraphRenderer => LayoutElementKind.Paragraph,
        HeadingRenderer => LayoutElementKind.Heading,
        ListRenderer => LayoutElementKind.List,
        TableRenderer => LayoutElementKind.Table,
        LayoutImageRenderer => LayoutElementKind.Image,
        LineSeparatorRenderer => LayoutElementKind.LineSeparator,
        PieChartRenderer => LayoutElementKind.PieChart,
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

    /// <summary>
    /// <see cref="IRenderer.Layout"/> calls the placement loop made in the last
    /// <see cref="RunLayout"/>, both passes together. Read by tests.
    /// </summary>
    internal int LayoutCalls { get; private set; }

    /// <summary>The plan of the last measuring run, or null when none ran. Built on each read.</summary>
    internal PaginationPlan? MeasurePlan =>
        _measureSink is null ? null : new PaginationPlan(_measureSink.Placements, _measurePages);

    /// <summary>The plan the drawing pass reported, or null before it ran. Built on each read.</summary>
    internal PaginationPlan? DrawPlan =>
        _drawSink is null ? null : new PaginationPlan(_drawSink.Placements, _drawPages);

    // The pass the loop is in. A measuring pass creates no page and keeps its own cursor, so a
    // page a failed drawing pass left open is not disturbed by it.
    private bool _measuring;
    private LayoutBox _measureArea;
    private double _measureY;

    // 0-based page of the cursor, counted the same way in both passes and from this layout's first
    // page. _pdf.Pages.Count would differ between the passes for a kernel document that already
    // holds pages.
    private int _pageIndex;

    private PlacementSink _placements = new();
    private PlacementSink? _measureSink;
    private PlacementSink? _drawSink;
    private int _measurePages;
    private int _drawPages;

    // The measuring run the drawing pass is compared with; set only when a dependent exists.
    private PlacementSink? _guard;

    private bool _collect;

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
    private LayoutBox PlaceArea => _measuring ? _measureArea : ContentArea;

    // Only the renderers added to this document are searched, not ones a renderer holds inside
    // itself.
    private List<IPlanDependent>? FindPlanDependents()
    {
        List<IPlanDependent>? found = null;
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
        _placements = new PlacementSink();
        if (measure)
        {
            _measureArea = ContentArea;
            _measureY = _margins.Top + HeaderHeight;
            MeasureRuns++;
        }

        for (var i = 0; i < _renderers.Count; i++)
        {
            // The diagnostics location is that of the element the document added, set here and not
            // in PlaceRenderer: its variable becomes the overflow renderer, which for a heading is
            // a plain paragraph renderer, and mapping that would call a heading's second page a
            // paragraph. Reports come from the drawing pass only, so the measuring pass leaves the
            // diagnostics alone.
            if (!measure)
            {
                _sink.CurrentKind = KindOf(_renderers[i]);
                _sink.CurrentIndex = i;
            }

            PlaceRenderer(_renderers[i], totalPages);
        }

        if (!measure)
        {
            _sink.CurrentKind = LayoutElementKind.Document;
            _sink.CurrentIndex = null;
            FinishCurrentPage(totalPages);
            if (_guard is not null && _placements.Count != _guard.Count)
                throw PaginationChanged();
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
        for (var run = 1; run <= MaxMeasureRuns; run++)
        {
            var anyChanged = false;
            for (var i = 0; i < dependents.Count; i++)
            {
                changedLast[i] = dependents[i].Resolve(plan);
                anyChanged |= changedLast[i];
                history[i].Add(dependents[i].ResolvedText);
            }

            var pages = RunPass(measure: true, totalPages: 0);
            pageCounts.Add(pages);

            // Run 1 has no earlier run to equal. From run 2, a run that resolved nothing new laid
            // out the same strings as the run before it, so its plan is that run's plan.
            if (run >= 2 && !anyChanged && pages == previousPages)
                return pages;

            plan = new PaginationPlan(_measureSink!.Placements, pages);
            previousPages = pages;
        }

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
            $"Pagination did not settle after {MaxMeasureRuns} layout passes: {cause}. " +
            "A string that depends on where its target lands can change the layout and so move " +
            "that target, and here the passes keep changing it instead of settling. " +
            "The renderers have already measured, so lay the content out again with a new Document.");
    }

    private static InvalidOperationException PaginationChanged() =>
        new("Pagination changed between the measuring and the drawing pass: the drawing pass " +
            "reported a placement that differs from the last measuring run's, so a renderer " +
            "answered differently the later time. " +
            "The renderers have already measured, so lay the content out again with a new Document.");

    private LayoutResult CallLayout(IRenderer renderer, LayoutBox area)
    {
        LayoutCalls++;
        return renderer.Layout(new LayoutContext(area));
    }

    // Tells the renderer whose result was just accepted to report where it landed. Called before
    // the cursor moves, in both passes, so the two passes record the same positions.
    private void ReportPlacements(IRenderer? accepted)
    {
        if (!_collect || accepted is not IPlacementSource source) return;

        var first = _placements.Count;
        _placements.Enter(accepted, _pageIndex);
        source.ReportPlacements(_placements);

        if (_measuring || _guard is null) return;
        for (var i = first; i < _placements.Count; i++)
            if (i >= _guard.Count
                || _guard[i].PageIndex != _placements[i].PageIndex
                || !ReferenceEquals(_guard[i].Key, _placements[i].Key))
                throw PaginationChanged();
    }

    // Moves the cursor to the top of the next page. The drawing pass commits the open page; the
    // measuring pass has none.
    private void AdvancePage(int totalPages)
    {
        if (!_measuring) FinishCurrentPage(totalPages);
        _pageIndex++;
        if (_measuring) _measureY = _measureArea.Y;
    }

    private void PlaceRenderer(IRenderer renderer, int totalPages)
    {
        var continuations = 0;
        while (true)
        {
            if (!_measuring) EnsurePage();

            var contentArea = PlaceArea;
            var availableArea = new LayoutBox(
                contentArea.X, Cursor,
                contentArea.Width, contentArea.Bottom - Cursor);

            var result = CallLayout(renderer, availableArea);

            switch (result.Status)
            {
                case LayoutResult.Outcome.Full:
                    ReportPlacements(renderer);
                    if (!_measuring) DrawRenderer(renderer);
                    Cursor = result.OccupiedArea!.Value.Bottom;
                    return;

                case LayoutResult.Outcome.Partial:
                    ReportPlacements(result.SplitRenderer);
                    if (!_measuring) DrawRenderer(result.SplitRenderer!);
                    AdvancePage(totalPages);
                    renderer = result.OverflowRenderer!;
                    break;

                case LayoutResult.Outcome.Nothing:
                    AdvancePage(totalPages);
                    if (!_measuring) EnsurePage();
                    var retry = CallLayout(renderer, PlaceArea);
                    if (retry.Status == LayoutResult.Outcome.Nothing)
                    {
                        if (!_measuring) FinishCurrentPage(totalPages);
                        throw ElementTooTall();
                    }
                    break;

                default:
                    // Unreachable: LayoutResult's constructor is private and its three factories
                    // pass only these three members. Kept because a fourth Outcome used to make
                    // this method return and skip the element; under the loop it would instead
                    // spin to the cap and blame the renderer (#459).
                    throw new InvalidOperationException($"Unknown layout outcome {result.Status}.");
            }

            if (++continuations > MaxContinuationsPerElement)
            {
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
        var pageBounds = new LayoutBox(0, 0, _pageSize.Width, _pageSize.Height);
        var ctx = new DrawContext(_currentCanvas!, pageBounds, _currentRendererCtx!, _pdf, _currentPage!)
        {
            Diagnostics = _sink,
        };
        // Reports come from this pass only. The page counting pass lays out without drawing, and a
        // wrap is cached per renderer, so a report raised from Layout could run twice.
        _sink.CurrentPage = _pdf.Pages.Count;
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
        _sink.CurrentIndex = null;

        if (Header is not null)
        {
            _sink.CurrentKind = LayoutElementKind.Header;
            var text = Header.Resolve(pageNumber, totalPages);
            var bandY = _margins.Top;
            var bandHeight = Header.EffectiveHeight;
            DrawBandText(ctx, canvas, text, Header, RunningBandKind.Header, pageNumber, bandY, bandHeight);
        }

        _sink.SyncEncodingWarnings(canvas, report: true);

        if (Footer is not null)
        {
            _sink.CurrentKind = LayoutElementKind.Footer;
            var text = Footer.Resolve(pageNumber, totalPages);
            var bandY = _pageSize.Height - _margins.Bottom - Footer.EffectiveHeight;
            var bandHeight = Footer.EffectiveHeight;
            DrawBandText(ctx, canvas, text, Footer, RunningBandKind.Footer, pageNumber, bandY, bandHeight);
        }
    }

    // Drops the RendererContext the old signature took and never read: the body reaches everything
    // it needs through DrawContext, which carries the same canvas.
    private void DrawBandText(
        DrawContext ctx,
        PdfCanvas canvas,
        string text,
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
        double x = band.Alignment switch
        {
            HorizontalAlignment.Center => _margins.Left + (contentWidth - textWidth) / 2,
            HorizontalAlignment.Right => _pageSize.Width - _margins.Right - textWidth,
            _ => _margins.Left,
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
        if (style.FontRef.IsEmbedded)
        {
            var resourceName = ctx.UseEmbeddedFont(style.FontRef.Embedded);
            canvas.SetFontByName(resourceName, style.FontSize);
            canvas.SetFillColorRgb(style.Color.R, style.Color.G, style.Color.B);
            // Show using glyph IDs
            var gids = new ushort[drawn.Length];
            var count = style.FontRef.Embedded.GetGlyphIds(drawn, gids);
            canvas.SetTextMatrix(1, 0, 0, 1, x, pdfY);
            canvas.ShowGlyphs(gids.AsSpan(0, count));
        }
        else
        {
            var fontResource = ctx.GetFont(style.Font);
            canvas.SetFont(fontResource, style.FontSize);
            canvas.SetFillColorRgb(style.Color.R, style.Color.G, style.Color.B);
            canvas.SetTextMatrix(1, 0, 0, 1, x, pdfY);
            canvas.ShowText(drawn);
        }
        canvas.EndText();
        if (ctx.Tagged) canvas.EndMarkedContent();
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
    /// proportional to what fits rather than to what was passed. That bound has one hole, and it is
    /// worth stating its consequence and not only its mechanism. A glyph may advance zero: the 38
    /// code points U+0000 to U+001F, U+007F and WinAnsi's five undefined codes measure zero in
    /// every face, and every character measures zero in Symbol and ZapfDingbats, the two of the
    /// fourteen Standard 14 faces whose width tables are absent from the metrics lookup.
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
    private static (string Drawn, double Width) FitToWidth(TextStyle style, string text, double maxWidth)
    {
        if (text.Length == 0) return (text, 0);

        var width = 0.0;
        var cut = 0;

        // The last point at which the run could be cut without splitting a token, and the width up
        // to it. Recorded as the walk passes the space so the fallback costs no second scan.
        var lastSpaceCut = 0;
        var lastSpaceWidth = 0.0;

        var i = 0;
        while (i < text.Length)
        {
            var len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? 2
                : 1;

            var piece = style.MeasureString(text.Substring(i, len));
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

        if (cut == text.Length) return (text, style.MeasureString(text));

        // Prefer the last word boundary. For a band this is not only tidier: a template ending in
        // a page number cut mid-token would show a number that is simply wrong, where dropping the
        // token shows none. Fall back to the mid-token cut when the fitted prefix holds no interior
        // space, which is the case a single overlong word produces.
        return lastSpaceCut > 0
            ? (text[..lastSpaceCut], lastSpaceWidth)
            : (text[..cut], width);
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
