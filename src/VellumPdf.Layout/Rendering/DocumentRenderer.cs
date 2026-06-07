// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Drives automatic pagination: lays out renderers one by one, advancing to a
/// new page whenever a renderer does not fit in the remaining area.
/// </summary>
public sealed class DocumentRenderer
{
    private readonly PdfDocument _pdf;
    private readonly PdfRectangle _pageSize;
    private readonly EdgeInsets   _margins;

    private readonly List<IRenderer> _renderers = [];

    private PdfPage?   _currentPage;
    private PdfCanvas? _currentCanvas;
    private double     _currentY;  // layout-space Y cursor (Y-down)

    public DocumentRenderer(PdfDocument pdf, PdfRectangle? pageSize = null, EdgeInsets? margins = null)
    {
        _pdf      = pdf;
        _pageSize = pageSize ?? pdf.DefaultPageSize;
        _margins  = margins  ?? new EdgeInsets(72); // 1-inch default margins (72pt)
    }

    public DocumentRenderer Add(IRenderer renderer) { _renderers.Add(renderer); return this; }

    /// <summary>Lays out all added renderers and saves the resulting PDF to <paramref name="destination"/>.</summary>
    public void Render(Stream destination)
    {
        DocumentFontRegistry.SetDocument(_pdf);

        foreach (var renderer in _renderers)
            PlaceRenderer(renderer);

        FinishCurrentPage();
        _pdf.Save(destination);
    }

    // ── Private ──────────────────────────────────────────────────────────────

    private LayoutBox ContentArea => new(
        _margins.Left,
        _margins.Top,
        _pageSize.Width  - _margins.Horizontal,
        _pageSize.Height - _margins.Vertical);

    private void EnsurePage()
    {
        if (_currentPage is null)
        {
            _currentPage   = _pdf.AddPage(_pageSize);
            _currentCanvas = new PdfCanvas(_currentPage);
            _currentY      = _margins.Top;
        }
    }

    private void FinishCurrentPage()
    {
        if (_currentCanvas is not null)
        {
            _currentCanvas.Finish();
            _currentPage   = null;
            _currentCanvas = null;
        }
    }

    private void PlaceRenderer(IRenderer renderer)
    {
        EnsurePage();

        var availableArea = new LayoutBox(
            ContentArea.X, _currentY,
            ContentArea.Width, ContentArea.Bottom - _currentY);

        var result = renderer.Layout(new LayoutContext(availableArea));

        switch (result.Status)
        {
            case LayoutResult.Outcome.Full:
                DrawRenderer(renderer, result.OccupiedArea!.Value);
                _currentY = result.OccupiedArea!.Value.Bottom;
                break;

            case LayoutResult.Outcome.Partial:
                DrawRenderer(result.SplitRenderer!, result.OccupiedArea!.Value);
                FinishCurrentPage();
                // Overflow continues on next page
                PlaceRenderer(result.OverflowRenderer!);
                break;

            case LayoutResult.Outcome.Nothing:
                // Nothing fit — advance to fresh page and retry
                FinishCurrentPage();
                EnsurePage();
                availableArea = ContentArea;
                var retry = renderer.Layout(new LayoutContext(availableArea));
                if (retry.Status != LayoutResult.Outcome.Nothing)
                    PlaceRenderer(renderer);
                // else content is too tall for a single page — skip it (degenerate)
                break;
        }
    }

    private void DrawRenderer(IRenderer renderer, LayoutBox occupied)
    {
        var pageBounds = new LayoutBox(0, 0, _pageSize.Width, _pageSize.Height);
        var ctx = new DrawContext(_currentCanvas!, pageBounds);
        renderer.Draw(ctx);
    }
}
