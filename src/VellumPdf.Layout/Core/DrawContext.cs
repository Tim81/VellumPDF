// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;

namespace VellumPdf.Layout.Core;

/// <summary>
/// Passed to <see cref="IRenderer.Draw"/>. Provides access to the current page's
/// canvas and performs the Y-down→Y-up coordinate flip at this single boundary.
///
/// Layout space: origin top-left, Y increases down, units = points.
/// PDF space:    origin bottom-left, Y increases up, units = points.
///
/// Flip formula: pdfY = pageHeight - layoutY
/// </summary>
public sealed class DrawContext
{
    public PdfCanvas Canvas     { get; }
    public LayoutBox PageBounds { get; }   // full page in layout space

    public DrawContext(PdfCanvas canvas, LayoutBox pageBounds)
    {
        Canvas     = canvas;
        PageBounds = pageBounds;
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
