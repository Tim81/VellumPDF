// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;
using VellumPdf.Fonts;

namespace VellumPdf.Barcodes;

/// <summary>Base type for the linear (one-dimensional) barcode symbologies: EAN/UPC, ITF-14 and Code 128.</summary>
public abstract class Barcode1D : Barcode
{
    private protected Barcode1D()
    {
    }

    /// <summary>The height of the bars, in points, excluding the human-readable text. Defaults to 40.</summary>
    public double BarHeight { get; init; } = 40;

    /// <summary>Whether to render the human-readable interpretation (HRI) text below/above the bars. Defaults to <c>true</c>.</summary>
    public bool ShowText { get; init; } = true;

    /// <summary>The Standard-14 font used for the HRI text. Defaults to <see cref="Standard14.Helvetica"/>.</summary>
    /// <remarks>
    /// The font is not embedded. A document with a PDF/A or PDF/UA conformance claim that draws
    /// this text is not told so by <c>VellumPdf.Layout</c>: the barcode selects the font without
    /// its diagnostics channel, so no <c>UnembeddedFont</c> item is reported for it. <b>Do not
    /// rely</b> on an empty <c>Document.Diagnostics</c> to show that every font in such a document
    /// is embedded; set <see cref="ShowText"/> to <c>false</c> or draw the text yourself.
    /// </remarks>
    public Standard14 TextFont { get; init; } = Standard14.Helvetica;

    /// <summary>The HRI text size, in points. Zero (the default) sizes it automatically from <see cref="BarHeight"/>.</summary>
    public double TextSize { get; init; }

    private protected sealed override BarcodeSize MeasureCore() => BarcodeGeometry.Measure1D(this, GetEncoded());

    /// <summary>Encodes this barcode's content into module-run form, caching the result on first use.</summary>
    private protected abstract Encoded1D GetEncoded();

    internal sealed override Encoded1D? GetEncoded1D() => GetEncoded();
}
