// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;
using VellumPdf.Canvas;
using VellumPdf.Fonts;

namespace VellumPdf.Barcodes;

/// <summary>Low-level <see cref="PdfCanvas"/> integration for drawing a barcode directly into a content stream.</summary>
public static class BarcodeCanvasExtensions
{
    /// <summary>
    /// Draws <paramref name="barcode"/> onto <paramref name="canvas"/>, with its footprint's
    /// lower-left corner (including the quiet zone, when <see cref="Barcode.IncludeQuietZone"/>
    /// is <c>true</c>) at (<paramref name="x"/>, <paramref name="y"/>) in PDF user space.
    /// </summary>
    /// <param name="canvas">The canvas to draw onto.</param>
    /// <param name="barcode">The barcode to render.</param>
    /// <param name="x">The X coordinate, in points, of the symbol's lower-left corner.</param>
    /// <param name="y">The Y coordinate, in points, of the symbol's lower-left corner.</param>
    /// <param name="textFont">
    /// The font used to draw a 1D symbology's human-readable text. Required whenever the
    /// barcode is a <see cref="Barcode1D"/> with <see cref="Barcode1D.ShowText"/> enabled;
    /// ignored otherwise.
    /// </param>
    /// <returns><paramref name="canvas"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="canvas"/> or <paramref name="barcode"/> is null.</exception>
    /// <exception cref="ArgumentException">The barcode's sizing or content options are invalid (see <see cref="Barcode.Measure"/>).</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="barcode"/> is a <see cref="Barcode1D"/> with <see cref="Barcode1D.ShowText"/>
    /// enabled but <paramref name="textFont"/> is null.
    /// </exception>
    /// <remarks>
    /// This method draws unmarked content. In a tagged document, bracket the call with
    /// <see cref="PdfCanvas.BeginArtifactMarkedContent"/>/<see cref="PdfCanvas.EndMarkedContent"/>
    /// for a decorative barcode, or with <see cref="PdfCanvas.BeginMarkedContent"/> (tag
    /// <c>"Figure"</c>) plus a registered structure element carrying alternate text otherwise —
    /// <see cref="DocumentBarcodeExtensions"/> does this automatically for the flow-layout API.
    /// </remarks>
    public static PdfCanvas DrawBarcode(this PdfCanvas canvas, Barcode barcode, double x, double y, PdfFontResource? textFont = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(barcode);

        BarcodePainter.Draw(canvas, barcode, x, y, textFont);
        return canvas;
    }
}
