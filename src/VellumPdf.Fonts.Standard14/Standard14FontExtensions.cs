// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Fonts;

/// <summary>
/// Embeds metric-compatible substitutes for the PDF standard-14 fonts, so a document can contain
/// conformant (fully embedded) PDF/A text without the caller supplying a font program. Backed by the
/// Liberation fonts (SIL OFL 1.1): Liberation Sans for the Helvetica family, Liberation Serif for the
/// Times family, and Liberation Mono for the Courier family.
/// </summary>
public static class Standard14FontExtensions
{
    /// <summary>
    /// Registers an embeddable, subset TrueType substitute for <paramref name="font"/> and returns a
    /// handle to draw with (map text to glyph ids via <see cref="EmbeddedFontHandle.GetGlyphIds"/> and
    /// show them with the canvas glyph operator). Unlike <see cref="PdfDocument.UseFont(Standard14)"/>,
    /// the resulting font is embedded, so text drawn with it satisfies PDF/A's font-embedding rule.
    /// </summary>
    /// <param name="doc">The document to register the font with.</param>
    /// <param name="font">
    /// The standard-14 font to substitute. The Helvetica, Times and Courier families are supported;
    /// <see cref="Standard14.Symbol"/> and <see cref="Standard14.ZapfDingbats"/> are not — the
    /// Liberation set does not cover them.
    /// </param>
    /// <returns>A handle to the embedded substitute font.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="doc"/> is null.</exception>
    /// <exception cref="System.NotSupportedException"><paramref name="font"/> is Symbol or ZapfDingbats.</exception>
    public static EmbeddedFontHandle EmbedStandard14Font(this PdfDocument doc, Standard14 font)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var bytes = LoadFont(ResourceNameFor(font));
        return doc.UseTrueTypeFont(bytes);
    }

    private static string ResourceNameFor(Standard14 font) => font switch
    {
        Standard14.Helvetica => "LiberationSans-Regular.ttf",
        Standard14.HelveticaBold => "LiberationSans-Bold.ttf",
        Standard14.HelveticaOblique => "LiberationSans-Italic.ttf",
        Standard14.HelveticaBoldOblique => "LiberationSans-BoldItalic.ttf",
        Standard14.TimesRoman => "LiberationSerif-Regular.ttf",
        Standard14.TimesBold => "LiberationSerif-Bold.ttf",
        Standard14.TimesItalic => "LiberationSerif-Italic.ttf",
        Standard14.TimesBoldItalic => "LiberationSerif-BoldItalic.ttf",
        Standard14.Courier => "LiberationMono-Regular.ttf",
        Standard14.CourierBold => "LiberationMono-Bold.ttf",
        Standard14.CourierOblique => "LiberationMono-Italic.ttf",
        Standard14.CourierBoldOblique => "LiberationMono-BoldItalic.ttf",
        _ => throw new NotSupportedException(
            $"No embeddable substitute is bundled for the {font} standard-14 font; the Liberation set "
            + "covers only the Helvetica, Times and Courier families. Supply your own embeddable font "
            + "via PdfDocument.UseTrueTypeFont for Symbol/ZapfDingbats."),
    };

    private static byte[] LoadFont(string logicalName)
    {
        var asm = typeof(Standard14FontExtensions).Assembly;
        using var s = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Bundled font resource '{logicalName}' is missing.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
