// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;
using VellumPdf.Layout.Core;

namespace VellumPdf.Barcodes;

/// <summary>
/// Base type for every barcode symbology. A barcode is a plain data object: it describes what
/// to encode and how to size and colour it, and is placed on a page either through the
/// low-level <c>PdfCanvas</c> extension or the flow-layout <c>Document.Add</c> extension.
/// The class hierarchy is closed to this assembly (see the private protected constructor);
/// consumers choose one of the sealed symbology types instead of deriving their own.
/// </summary>
public abstract class Barcode
{
    private protected Barcode()
    {
    }

    /// <summary>
    /// The width of one module (the narrowest bar/space or matrix cell), in points. Mutually
    /// exclusive with <see cref="TargetWidth"/> — setting both throws when the barcode is
    /// measured or drawn. When neither is set, the default is 1.0 for 1D symbologies.
    /// </summary>
    public double? ModuleSize { get; init; }

    /// <summary>
    /// The desired overall rendered width, in points, from which the module size is derived.
    /// Mutually exclusive with <see cref="ModuleSize"/>.
    /// </summary>
    public double? TargetWidth { get; init; }

    /// <summary>
    /// When <c>true</c> (the default), the symbol's quiet zone is included in its measured and
    /// drawn footprint, as required for reliable scanning. Set to <c>false</c> only when the
    /// surrounding layout already guarantees an equivalent clear margin.
    /// </summary>
    public bool IncludeQuietZone { get; init; } = true;

    /// <summary>The colour of the dark bars/modules. Defaults to <see cref="ColorRgb.Black"/>.</summary>
    public ColorRgb Foreground { get; init; } = ColorRgb.Black;

    /// <summary>The fill colour behind the symbol. <c>null</c> (the default) leaves it transparent.</summary>
    public ColorRgb? Background { get; init; }

    /// <summary>
    /// Alternate text for the PDF <c>/Figure</c> structure element (tagged PDF). When null, a
    /// symbology-specific description is composed from the encoded content. Ignored when
    /// <see cref="Decorative"/> is <c>true</c>.
    /// </summary>
    public string? AltText { get; init; }

    /// <summary>
    /// When <c>true</c>, the barcode is marked as a decorative artifact in tagged output and
    /// omitted from the structure tree. Use this only when the encoded data is already available
    /// as accessible text nearby. When <c>false</c> (the default) the barcode is a
    /// <c>/Figure</c> carrying <see cref="AltText"/>.
    /// </summary>
    public bool Decorative { get; init; }

    /// <summary>Margins around the symbol, in points. Defaults to zero on all sides.</summary>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Horizontal placement of the symbol within the content area. Defaults to left.</summary>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>
    /// Measures the symbol's footprint (including quiet zones, HRI text and any bearer bars),
    /// validating the barcode's content and sizing options.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The barcode's options are invalid (e.g. both <see cref="ModuleSize"/> and
    /// <see cref="TargetWidth"/> are set).
    /// </exception>
    public BarcodeSize Measure() => MeasureCore();

    private protected abstract BarcodeSize MeasureCore();

    /// <summary>
    /// Returns this barcode's encoded linear (1D) run data, or <c>null</c> when it is a matrix
    /// (2D) symbology. <see cref="BarcodePainter"/> uses this (together with
    /// <see cref="GetEncoded2D"/>) to draw any concrete barcode type without depending on it.
    /// </summary>
    internal virtual Encoded1D? GetEncoded1D() => null;

    /// <summary>
    /// Returns this barcode's encoded matrix (2D) data, or <c>null</c> when it is a linear (1D)
    /// symbology. See <see cref="GetEncoded1D"/>.
    /// </summary>
    internal virtual Encoded2D? GetEncoded2D() => null;
}
