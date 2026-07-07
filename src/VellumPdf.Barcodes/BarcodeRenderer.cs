// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;
using VellumPdf.Document;
using VellumPdf.Layout.Core;

namespace VellumPdf.Barcodes;

/// <summary>
/// Renders a <see cref="Barcode"/> as a flow-layout element. Added to a document via
/// <see cref="DocumentBarcodeExtensions.Add"/>, or directly through the generic
/// <c>Document.Add(IRenderer)</c> overload.
/// </summary>
public sealed class BarcodeRenderer : IRenderer
{
    private readonly Barcode _barcode;
    private LayoutBox _occupied;

    /// <summary>Creates a renderer for the given barcode.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="barcode"/> is null.</exception>
    public BarcodeRenderer(Barcode barcode)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        _barcode = barcode;
    }

    /// <summary>Measures the barcode's footprint and reserves it, or reports that it does not fit the remaining area on the current page.</summary>
    /// <exception cref="ArgumentException">
    /// The barcode's sizing or content options are invalid (see <see cref="Barcode.Measure"/>), or
    /// its footprint is wider than the available width — unlike height, pagination cannot resolve
    /// an over-wide symbol.
    /// </exception>
    public LayoutResult Layout(LayoutContext ctx)
    {
        var size = _barcode.Measure();

        if (size.Width > ctx.Area.Width)
            throw new ArgumentException(
                $"The barcode is {size.Width:F1}pt wide, exceeding the {ctx.Area.Width:F1}pt available width. " +
                "Reduce ModuleSize/TargetWidth, or widen the page or reduce its margins.",
                nameof(_barcode));

        if (ctx.Area.Height < size.Height) return LayoutResult.Nothing();

        _occupied = ctx.Area.WithHeight(size.Height);
        return LayoutResult.Full(_occupied);
    }

    /// <summary>Draws the barcode, tagging it as a Figure with alternate text, or as an artifact when <see cref="Barcode.Decorative"/> is set.</summary>
    public void Draw(DrawContext ctx)
    {
        var size = _barcode.Measure();
        var area = _occupied.Deflate(_barcode.Margins);
        var symbolWidth = size.Width - _barcode.Margins.Horizontal;

        var xOffset = _barcode.Alignment switch
        {
            HorizontalAlignment.Center => (area.Width - symbolWidth) / 2,
            HorizontalAlignment.Right => area.Width - symbolWidth,
            _ => 0,
        };

        var (x, y, _, _) = ctx.ToPdfRect(area);
        var canvas = ctx.Canvas;

        var textFont = _barcode is Barcode1D { ShowText: true } barcode1D
            ? ctx.GetFont(barcode1D.TextFont)
            : null;

        // Tagged PDF: a data-bearing barcode is a Figure carrying alternate text (mirrors
        // PieChartRenderer/LayoutImageRenderer); a barcode flagged Decorative is an artifact the
        // structure tree omits, e.g. when the encoded data is already accessible as nearby text.
        var mcid = -1;
        if (ctx.Tagged)
        {
            if (_barcode.Decorative)
                canvas.BeginArtifactMarkedContent();
            else
                mcid = canvas.BeginMarkedContent("Figure");
        }

        BarcodePainter.Draw(canvas, _barcode, x + xOffset, y, textFont);

        if (ctx.Tagged)
        {
            canvas.EndMarkedContent();
            if (mcid >= 0)
                ctx.RegisterStructElem(new PdfStructElem("Figure")
                {
                    Mcid = mcid,
                    // Whitespace-only alt text falls back to the default: PDF/UA-1 clause 7.3-1
                    // rejects a Figure whose /Alt is empty.
                    AltText = string.IsNullOrWhiteSpace(_barcode.AltText)
                        ? DefaultAltText(_barcode)
                        : _barcode.AltText,
                });
        }
    }

    /// <summary>
    /// Composes alternate text (required by the PDF/UA rule that every Figure carries <c>/Alt</c>)
    /// from the symbology and, for text content, the encoded string. Byte content falls back to
    /// the bare symbology name since arbitrary bytes rarely make meaningful alternate text.
    /// </summary>
    private static string DefaultAltText(Barcode barcode) => barcode switch
    {
        QrCode { Gs1: QrGs1Mode.ElementString, Text: { } gs1Text } => $"QR code (GS1): {Gs1ElementString.Parse(gs1Text).Hri}",
        QrCode { Gs1: QrGs1Mode.DigitalLink, Text: { } linkText } => $"QR code (GS1 Digital Link): {Gs1DigitalLink.Build(linkText)}",
        QrCode { Text: { } text } => $"QR code: {text}",
        QrCode => "QR code",
        MicroQrCode micro => $"Micro QR code: {micro.Content}",
        Pdf417Barcode { Text: { } text } => $"PDF417 barcode: {text}",
        Pdf417Barcode => "PDF417 barcode",
        DataMatrixBarcode { Text: { } text } => $"Data Matrix barcode: {text}",
        DataMatrixBarcode => "Data Matrix barcode",
        AztecCode { Text: { } text } => $"Aztec Code: {text}",
        AztecCode => "Aztec Code",
        Code128Barcode code128 => $"Code 128 barcode: {code128.Content}",
        Code39Barcode code39 => $"Code 39 barcode: {code39.Content}",
        EanBarcode ean => $"{DescribeEanSymbology(ean.Symbology)}: {ean.Digits}",
        Itf14Barcode itf => $"ITF-14 barcode: {itf.Digits}",
        _ => "Barcode",
    };

    private static string DescribeEanSymbology(EanSymbology symbology) => symbology switch
    {
        EanSymbology.Ean13 => "EAN-13 barcode",
        EanSymbology.Ean8 => "EAN-8 barcode",
        EanSymbology.UpcA => "UPC-A barcode",
        EanSymbology.UpcE => "UPC-E barcode",
        _ => "Barcode",
    };
}
