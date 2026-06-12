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
    private readonly PdfDocument _pdf;
    private readonly PdfRectangle _pageSize;
    private readonly EdgeInsets _margins;

    private readonly List<IRenderer> _renderers = [];

    private PdfPage? _currentPage;
    private PdfCanvas? _currentCanvas;
    private RendererContext? _currentRendererCtx;
    private double _currentY;  // layout-space Y cursor (Y-down)

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
                $"The computed content area has no positive size ({contentArea.Width:F1}×{contentArea.Height:F1}pt). " +
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

            _currentCanvas.Finish();
            _currentPage = null;
            _currentCanvas = null;
            _currentRendererCtx = null;
        }
    }

    private void PlaceRenderer(IRenderer renderer, int totalPages)
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
                break;

            case LayoutResult.Outcome.Partial:
                DrawRenderer(result.SplitRenderer!);
                FinishCurrentPage(totalPages);
                PlaceRenderer(result.OverflowRenderer!, totalPages);
                break;

            case LayoutResult.Outcome.Nothing:
                FinishCurrentPage(totalPages);
                EnsurePage();
                var retry = renderer.Layout(new LayoutContext(ContentArea));
                if (retry.Status == LayoutResult.Outcome.Nothing)
                {
                    FinishCurrentPage(totalPages);
                    throw new InvalidOperationException(
                        "An element is too tall to fit on a single page and cannot be rendered. " +
                        "Reduce the element's content or increase the page size.");
                }
                PlaceRenderer(renderer, totalPages);
                break;
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
            DrawBandText(ctx, canvas, rendererCtx, text, Header, bandY, bandHeight);
        }

        if (Footer is not null)
        {
            var text = Footer.Resolve(pageNumber, totalPages);
            var bandY = _pageSize.Height - _margins.Bottom - Footer.EffectiveHeight;
            var bandHeight = Footer.EffectiveHeight;
            DrawBandText(ctx, canvas, rendererCtx, text, Footer, bandY, bandHeight);
        }
    }

    private void DrawBandText(
        DrawContext ctx,
        PdfCanvas canvas,
        RendererContext rendererCtx,
        string text,
        RunningBand band,
        double bandY,
        double bandHeight)
    {
        var style = band.Style;
        var contentWidth = _pageSize.Width - _margins.Horizontal;
        var textWidth = style.MeasureString(text);

        double x = band.Alignment switch
        {
            HorizontalAlignment.Center => _margins.Left + (contentWidth - textWidth) / 2,
            HorizontalAlignment.Right => _pageSize.Width - _margins.Right - textWidth,
            _ => _margins.Left,
        };

        // PDF Y: baseline of text within the band (centred vertically in the band)
        var pdfY = ctx.ToPdfY(bandY + bandHeight - (bandHeight - style.FontSize) / 2);

        // Running-band text is pagination decoration, not part of the logical structure.
        // Wrap as /Artifact when the document is tagged so PDF/UA validators find no
        // untagged real content.
        if (ctx.Tagged) canvas.BeginArtifactMarkedContent();
        canvas.BeginText();
        if (style.FontRef.IsEmbedded)
        {
            var resourceName = ctx.UseEmbeddedFont(style.FontRef.Embedded);
            canvas.SetFontByName(resourceName, style.FontSize);
            // Show using glyph IDs
            var gids = new ushort[text.Length];
            var count = style.FontRef.Embedded.GetGlyphIds(text, gids);
            canvas.SetTextMatrix(1, 0, 0, 1, x, pdfY);
            canvas.ShowGlyphs(gids.AsSpan(0, count));
        }
        else
        {
            var fontResource = ctx.GetFont(style.Font);
            canvas.SetFont(fontResource, style.FontSize);
            canvas.SetTextMatrix(1, 0, 0, 1, x, pdfY);
            canvas.ShowText(text);
        }
        canvas.EndText();
        if (ctx.Tagged) canvas.EndMarkedContent();
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
        var availableArea = new LayoutBox(
            contentArea.X, currentY,
            contentArea.Width, contentArea.Bottom - currentY);

        var result = renderer.Layout(new LayoutContext(availableArea));

        switch (result.Status)
        {
            case LayoutResult.Outcome.Full:
                currentY = result.OccupiedArea!.Value.Bottom;
                break;

            case LayoutResult.Outcome.Partial:
                pages++;
                currentY = contentArea.Y;
                CountPlaceRenderer(result.OverflowRenderer!, ref currentY, ref pages, contentArea);
                break;

            case LayoutResult.Outcome.Nothing:
                pages++;
                currentY = contentArea.Y;
                var retry = renderer.Layout(new LayoutContext(contentArea));
                if (retry.Status == LayoutResult.Outcome.Nothing)
                    break; // element too tall — skip (real render will throw)
                CountPlaceRenderer(renderer, ref currentY, ref pages, contentArea);
                break;
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
                $"Page width must be a positive finite number (was {pageSize.Width}).");
        if (!double.IsFinite(pageSize.Height) || pageSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize),
                $"Page height must be a positive finite number (was {pageSize.Height}).");

        if (margins.Horizontal >= pageSize.Width)
            throw new ArgumentException(
                $"Horizontal margins ({margins.Horizontal:F1}pt) meet or exceed page width ({pageSize.Width:F1}pt). " +
                "Reduce the left/right margins.",
                nameof(margins));
        if (margins.Vertical >= pageSize.Height)
            throw new ArgumentException(
                $"Vertical margins ({margins.Vertical:F1}pt) meet or exceed page height ({pageSize.Height:F1}pt). " +
                "Reduce the top/bottom margins.",
                nameof(margins));
    }
}
