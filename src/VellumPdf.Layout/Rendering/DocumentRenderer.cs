// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Drives automatic pagination: lays out renderers one by one, advancing to a
/// new page whenever a renderer does not fit in the remaining area.
///
/// When <see cref="Header"/> or <see cref="Footer"/> are set a two-pass render
/// is performed: pass 1 counts pages (layout only, no PDF objects created);
/// pass 2 draws all pages including the running bands with {page}/{pages} resolved.
/// </summary>
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

    // Shared by PlaceRenderer and CountPlaceRenderer so the two passes cannot drift to different
    // wording, or different verdicts, on an element neither can place (#460). It does not make the
    // passes agree in general: with a running band and no content at all, pass 1 still counts one
    // page where pass 2 creates none, which predates this change. Making pass 1 throw also moves
    // the throw earlier when a band is set, so a caller that catches it is left holding an empty
    // PdfDocument rather than the pages pass 2 had already committed; PdfDocument.Save then reports
    // that the document has no pages.
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

    /// <summary>
    /// Characters written via <see cref="PdfCanvas.ShowText"/> across every page rendered so far
    /// that WinAnsiEncoding could not represent. Aggregated from each page's canvas as it finishes.
    /// </summary>
    internal IReadOnlyList<TextEncodingWarning> TextEncodingWarnings => _textEncodingWarnings;

    /// <summary>
    /// Running bands whose text did not fit the content box and was cut, at most one report per
    /// band, each naming the page that lost the most.
    ///
    /// Public here, unlike <see cref="TextEncodingWarnings"/>, because there is no other route to
    /// it: a canvas can be asked what it could not encode, but only the renderer knows the content
    /// box a band was measured against, and the layout assembly grants internal visibility only to
    /// VellumPdf.Signing. Read directly by
    /// <c>RunningBandFitTests.Band_truncation_isReadableFromTheRendererItself</c>, which is what
    /// keeps this member from being public with nothing exercising it.
    /// </summary>
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
    public RunningBand? Header { get; set; }

    /// <summary>Footer band drawn at the bottom of every page. Optional.</summary>
    public RunningBand? Footer { get; set; }

    // Pending bookmarks: queued by Document.AddBookmark, drained on next Draw.
    internal readonly List<BookmarkEntry> PendingBookmarks = [];

    /// <summary>Creates a renderer that paginates content onto <paramref name="pdf"/> using the given page size and margins.</summary>
    public DocumentRenderer(PdfDocument pdf, PdfRectangle? pageSize = null, EdgeInsets? margins = null)
    {
        _pdf = pdf;
        _pageSize = pageSize ?? pdf.DefaultPageSize;
        _margins = margins ?? new EdgeInsets(72); // 1-inch default margins (72pt)

        ValidateGeometry(_pageSize, _margins);
    }

    /// <summary>Appends a renderer to the document flow and returns this instance for chaining.</summary>
    public DocumentRenderer Add(IRenderer renderer) { _renderers.Add(renderer); return this; }

    /// <summary>Lays out all added renderers and saves the resulting PDF to <paramref name="destination"/>.</summary>
    public void Render(Stream destination)
    {
        RunLayout();
        _pdf.Save(destination);
    }

    /// <summary>
    /// Runs the layout pass (page count + full render) without writing to any stream.
    /// After this call, callers may invoke <see cref="PdfDocument.Save"/> or
    /// <see cref="PdfDocument.PrepareForSigning"/> on the underlying document.
    /// </summary>
    internal void RunLayout()
    {
        var contentArea = ContentArea;
        if (contentArea.Width <= 0 || contentArea.Height <= 0)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The computed content area has no positive size ({contentArea.Width:F1}×{contentArea.Height:F1}pt). ") +
                "The margins, header, and footer together exceed the page dimensions. " +
                "Reduce the margins or band heights.",
                "margins");

        int totalPages;
        if (Header is not null || Footer is not null)
        {
            // Pass 1: count pages via the same PlaceRenderer code path (no PDF objects created).
            totalPages = CountPages();
        }
        else
        {
            totalPages = 0; // unknown / irrelevant
        }

        // Pass 2 (or only pass): full render with running bands resolved.
        foreach (var renderer in _renderers)
            PlaceRenderer(renderer, totalPages);

        FinishCurrentPage(totalPages);
    }

    // ── Private ──────────────────────────────────────────────────────────────

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
            // Draw header/footer for this page.
            if (Header is not null || Footer is not null)
            {
                var pageNumber = _pdf.Pages.Count; // pages already added
                DrawRunningBands(_currentCanvas, _currentRendererCtx!, _currentPage!, pageNumber, totalPages);
            }

            _textEncodingWarnings.AddRange(_currentCanvas.TextEncodingWarnings);

            _currentCanvas.Finish();
            _currentPage = null;
            _currentCanvas = null;
            _currentRendererCtx = null;
        }
    }

    private void PlaceRenderer(IRenderer renderer, int totalPages)
    {
        var continuations = 0;
        while (true)
        {
            EnsurePage();

            var availableArea = new LayoutBox(
                ContentArea.X, _currentY,
                ContentArea.Width, ContentArea.Bottom - _currentY);

            var result = renderer.Layout(new LayoutContext(availableArea));

            switch (result.Status)
            {
                case LayoutResult.Outcome.Full:
                    DrawRenderer(renderer);
                    _currentY = result.OccupiedArea!.Value.Bottom;
                    return;

                case LayoutResult.Outcome.Partial:
                    DrawRenderer(result.SplitRenderer!);
                    FinishCurrentPage(totalPages);
                    renderer = result.OverflowRenderer!;
                    break;

                case LayoutResult.Outcome.Nothing:
                    FinishCurrentPage(totalPages);
                    EnsurePage();
                    var retry = renderer.Layout(new LayoutContext(ContentArea));
                    if (retry.Status == LayoutResult.Outcome.Nothing)
                    {
                        FinishCurrentPage(totalPages);
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
                // Symmetry with the too-tall throw above: commit the open page rather than leave
                // its canvas unflushed. The page still stays in the document. Both
                // branches reach this throw, but only the Nothing branch can arrive with a page
                // still open, because the Partial branch has already finished one.
                FinishCurrentPage(totalPages);
                throw TooManyContinuations();
            }
        }
    }

    private void DrawRenderer(IRenderer renderer)
    {
        var pageBounds = new LayoutBox(0, 0, _pageSize.Width, _pageSize.Height);
        var ctx = new DrawContext(_currentCanvas!, pageBounds, _currentRendererCtx!, _pdf, _currentPage!);
        renderer.Draw(ctx);
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
        var ctx = new DrawContext(canvas, pageBounds, rendererCtx, _pdf, page);

        if (Header is not null)
        {
            var text = Header.Resolve(pageNumber, totalPages);
            var bandY = _margins.Top;
            var bandHeight = Header.EffectiveHeight;
            DrawBandText(ctx, canvas, text, Header, RunningBandKind.Header, pageNumber, bandY, bandHeight);
        }

        if (Footer is not null)
        {
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

        // The band is the one path to a Tf operator that reaches no renderer, so it reached no
        // validation either: a non-finite band font size saved a document containing "/F1 NaN Tf"
        // and "1 0 0 1 NaN NaN Tm". The note further down about geometry validation asked for this.
        LayoutValidation.ValidateStyle(style, $"The {kind.ToString().ToLowerInvariant()} band");

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

    // ── Pass 1: page counting ─────────────────────────────────────────────────

    /// <summary>
    /// Counts pages by running the same placement logic as the real render pass,
    /// but without creating PDF objects. This guarantees the count matches the actual render.
    /// </summary>
    private int CountPages()
    {
        var pages = 1;
        var currentY = _margins.Top + HeaderHeight;
        var contentArea = ContentArea;

        foreach (var renderer in _renderers)
            CountPlaceRenderer(renderer, ref currentY, ref pages, contentArea);

        return pages;
    }

    private void CountPlaceRenderer(IRenderer renderer, ref double currentY, ref int pages, LayoutBox contentArea)
    {
        var continuations = 0;
        while (true)
        {
            var availableArea = new LayoutBox(
                contentArea.X, currentY,
                contentArea.Width, contentArea.Bottom - currentY);

            var result = renderer.Layout(new LayoutContext(availableArea));

            switch (result.Status)
            {
                case LayoutResult.Outcome.Full:
                    currentY = result.OccupiedArea!.Value.Bottom;
                    return;

                case LayoutResult.Outcome.Partial:
                    pages++;
                    currentY = contentArea.Y;
                    renderer = result.OverflowRenderer!;
                    break;

                case LayoutResult.Outcome.Nothing:
                    pages++;
                    currentY = contentArea.Y;
                    var retry = renderer.Layout(new LayoutContext(contentArea));
                    if (retry.Status == LayoutResult.Outcome.Nothing)
                    {
                        // This pass used to skip the element and undercount here while
                        // PlaceRenderer threw on it; the two passes must agree (#460).
                        throw ElementTooTall();
                    }
                    break;

                default:
                    // Unreachable for the same reason as PlaceRenderer's arm above, and kept for
                    // the same reason (#459).
                    throw new InvalidOperationException($"Unknown layout outcome {result.Status}.");
            }

            if (++continuations > MaxContinuationsPerElement)
                throw TooManyContinuations();
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates that page size and margins are finite, positive, and leave a non-empty content area.
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
