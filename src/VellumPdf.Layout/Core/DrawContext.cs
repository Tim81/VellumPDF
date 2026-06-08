// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Fonts;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Core;

/// <summary>
/// Passed to <see cref="IRenderer.Draw"/>. Provides access to the current page's
/// canvas, a per-page resource context, and the document font/resource provider.
///
/// Layout space: origin top-left, Y increases down, units = points.
/// PDF space:    origin bottom-left, Y increases up, units = points.
///
/// Flip formula: pdfY = pageHeight - layoutY
/// </summary>
public sealed class DrawContext
{
    public PdfCanvas Canvas { get; }
    public LayoutBox PageBounds { get; }   // full page in layout space
    public RendererContext RendererContext { get; }   // per-page resource registration
    private readonly PdfDocument _document;

    public DrawContext(PdfCanvas canvas, LayoutBox pageBounds, RendererContext rendererContext, PdfDocument document)
    {
        Canvas = canvas;
        PageBounds = pageBounds;
        RendererContext = rendererContext;
        _document = document;
    }

    /// <summary>Returns (or creates) a font resource on the current document.</summary>
    public PdfFontResource GetFont(Standard14 font) => _document.UseFont(font);

    /// <summary>
    /// Records that the current page uses the given embedded TrueType font handle
    /// and returns its PDF resource name so the canvas can select it.
    /// </summary>
    public string UseEmbeddedFont(EmbeddedFontHandle handle)
    {
        RendererContext.RegisterEmbeddedFontUsage(handle);
        return handle.ResourceName;
    }

    /// <summary>Converts a layout-space Y coordinate to PDF user-space Y.</summary>
    public double ToPdfY(double layoutY) => PageBounds.Height - layoutY;

    /// <summary>
    /// Converts layout-space (x, y, width, height) → PDF (x, pdfY-height, width, height).
    /// PDF rectangles are anchored at their lower-left corner.
    /// </summary>
    public (double x, double y, double w, double h) ToPdfRect(LayoutBox box) =>
        (box.X, PageBounds.Height - box.Bottom, box.Width, box.Height);
}
