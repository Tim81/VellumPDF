// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Fonts;

/// <summary>
/// A Standard-14 font registered as a PDF font resource.
/// The PDF font dictionary for a Standard-14 font requires only /Type, /Subtype,
/// and /BaseFont (ISO 32000-2 §9.6.2.2); no embedding or metrics needed. The 12
/// non-symbolic text fonts also declare /Encoding /WinAnsiEncoding, matching the encoding
/// <see cref="VellumPdf.Canvas.PdfCanvas.ShowText"/> uses; Symbol and ZapfDingbats keep their
/// built-in symbolic encoding and omit /Encoding.
/// </summary>
public sealed class PdfFontResource
{
    /// <summary>The Standard-14 font this resource represents.</summary>
    public Standard14 Font { get; }
    /// <summary>The resource name used to reference this font from a page's resource dictionary.</summary>
    public string ResourceName { get; }

    /// <summary>Creates a font resource for the given Standard-14 font and resource name.</summary>
    public PdfFontResource(Standard14 font, string resourceName)
    {
        Font = font;
        ResourceName = resourceName;
    }

    private static readonly string[] _baseFontNames =
    [
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Symbol", "ZapfDingbats",
    ];

    /// <summary>The canonical PostScript <c>/BaseFont</c> name for the selected Standard-14 font.</summary>
    public string BaseFontName => _baseFontNames[(int)Font];

    /// <summary>
    /// Builds the PDF font dictionary (<c>/Type /Font</c>, <c>/Subtype /Type1</c>, <c>/BaseFont</c>).
    /// Adds <c>/Encoding /WinAnsiEncoding</c> for every Standard-14 font except Symbol and
    /// ZapfDingbats, whose built-in symbolic encoding would be overridden by declaring one.
    /// </summary>
    public PdfDictionary BuildDictionary()
    {
        var dict = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Font)
            .Set(PdfName.Subtype, new PdfName("Type1"))
            .Set(PdfName.BaseFont, new PdfName(BaseFontName));
        if (Font is not (Standard14.Symbol or Standard14.ZapfDingbats))
            dict.Set(PdfName.Encoding, new PdfName("WinAnsiEncoding"));
        return dict;
    }

    /// <summary>Measures a string in PDF points at the given size using the Standard-14 metrics.</summary>
    public double MeasureString(string text, double pointSize) =>
        Standard14Metrics.MeasureString(Font, text, pointSize);
}
