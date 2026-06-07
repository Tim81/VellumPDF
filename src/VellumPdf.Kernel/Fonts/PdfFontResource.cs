// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Fonts;

/// <summary>
/// A Standard-14 font registered as a PDF font resource.
/// The PDF font dictionary for a Standard-14 font requires only /Type, /Subtype,
/// and /BaseFont (ISO 32000-2 §9.6.2.2); no embedding or metrics needed.
/// </summary>
public sealed class PdfFontResource
{
    public Standard14 Font { get; }
    public string ResourceName { get; }

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

    public string BaseFontName => _baseFontNames[(int)Font];

    public PdfDictionary BuildDictionary() => new PdfDictionary()
        .Set(PdfName.Type, PdfName.Font)
        .Set(PdfName.Subtype, new PdfName("Type1"))
        .Set(PdfName.BaseFont, new PdfName(BaseFontName));

    public double MeasureString(string text, double pointSize) =>
        Standard14Metrics.MeasureString(Font, text, pointSize);
}
