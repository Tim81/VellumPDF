// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Reader.Fonts;

/// <summary>
/// Resolves a font's <c>/BaseFont</c> name to one of the 14 standard fonts ISO 32000-2 §9.6.2.2
/// names (Helvetica, Times, Courier in their four styles each, Symbol, ZapfDingbats), for the
/// built-in encoding and AFM-width fallback §9.6.2.1 requires when a font has no
/// <c>/Widths</c>/<c>/FontDescriptor</c>.
/// </summary>
/// <remarks>
/// Beyond the 14 exact names, this class also recognises a fixed list of Windows/Word substitute
/// names (<c>Arial</c>, <c>Times New Roman</c>, <c>Courier New</c>, and their bold/italic
/// combinations) as aliases for the metrically closest standard font. ISO 32000-2 names only the
/// 14 exact strings; this alias list is a reader heuristic with no basis in the standard, and it
/// only ever selects a WIDTH table (§3.9 step 9), never a glyph mapping, which continues to come
/// from the font's own <c>/Encoding</c> resolution regardless of which alias matched.
/// </remarks>
internal static class Standard14Names
{
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal)
    {
        ["Arial"] = "Helvetica",
        ["ArialMT"] = "Helvetica",
        ["Arial,Bold"] = "Helvetica-Bold",
        ["Arial-BoldMT"] = "Helvetica-Bold",
        ["Arial,Italic"] = "Helvetica-Oblique",
        ["Arial-ItalicMT"] = "Helvetica-Oblique",
        ["Arial,BoldItalic"] = "Helvetica-BoldOblique",
        ["Arial-BoldItalicMT"] = "Helvetica-BoldOblique",
        ["Helvetica,Bold"] = "Helvetica-Bold",
        ["Helvetica,Italic"] = "Helvetica-Oblique",
        ["Helvetica,BoldItalic"] = "Helvetica-BoldOblique",
        ["TimesNewRoman"] = "Times-Roman",
        ["TimesNewRomanPSMT"] = "Times-Roman",
        ["TimesNewRoman,Bold"] = "Times-Bold",
        ["TimesNewRomanPS-BoldMT"] = "Times-Bold",
        ["TimesNewRoman,Italic"] = "Times-Italic",
        ["TimesNewRomanPS-ItalicMT"] = "Times-Italic",
        ["TimesNewRoman,BoldItalic"] = "Times-BoldItalic",
        ["TimesNewRomanPS-BoldItalicMT"] = "Times-BoldItalic",
        ["CourierNew"] = "Courier",
        ["CourierNewPSMT"] = "Courier",
        ["CourierNew,Bold"] = "Courier-Bold",
        ["CourierNewPS-BoldMT"] = "Courier-Bold",
        ["CourierNew,Italic"] = "Courier-Oblique",
        ["CourierNewPS-ItalicMT"] = "Courier-Oblique",
        ["CourierNew,BoldItalic"] = "Courier-BoldOblique",
        ["CourierNewPS-BoldItalicMT"] = "Courier-BoldOblique",
    };

    private static readonly HashSet<string> _exact = new(StringComparer.Ordinal)
    {
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Symbol", "ZapfDingbats",
    };

    private static readonly Dictionary<string, Standard14> _kernelFonts = new(StringComparer.Ordinal)
    {
        ["Helvetica"] = Standard14.Helvetica,
        ["Helvetica-Bold"] = Standard14.HelveticaBold,
        ["Helvetica-Oblique"] = Standard14.HelveticaOblique,
        ["Helvetica-BoldOblique"] = Standard14.HelveticaBoldOblique,
        ["Times-Roman"] = Standard14.TimesRoman,
        ["Times-Bold"] = Standard14.TimesBold,
        ["Times-Italic"] = Standard14.TimesItalic,
        ["Times-BoldItalic"] = Standard14.TimesBoldItalic,
        ["Courier"] = Standard14.Courier,
        ["Courier-Bold"] = Standard14.CourierBold,
        ["Courier-Oblique"] = Standard14.CourierOblique,
        ["Courier-BoldOblique"] = Standard14.CourierBoldOblique,
    };

    /// <summary>
    /// Maps a <c>/BaseFont</c> name to the AFM font name it resolves to (e.g. <c>Arial,Bold</c> to
    /// <c>Helvetica-Bold</c>, <c>ABCDEF+Times-Roman</c> to <c>Times-Roman</c>). Returns
    /// <see langword="false"/> when the name is longer than
    /// <see cref="AdobeGlyphList.MaxGlyphNameLength"/>, or is neither one of the 14 exact names nor
    /// a documented alias. Comparison is case-sensitive, matching the standard's own names.
    /// </summary>
    public static bool TryResolve(string baseFont, out string afmName)
    {
        afmName = "";
        if (baseFont.Length == 0 || baseFont.Length > AdobeGlyphList.MaxGlyphNameLength)
            return false;

        // A subset tag is exactly six uppercase letters followed by '+' (ISO 32000-2 §9.9.1).
        var name = baseFont;
        if (name.Length > 7 && name[6] == '+' && IsSubsetTag(name))
            name = name[7..];

        if (_exact.Contains(name))
        {
            afmName = name;
            return true;
        }

        if (_aliases.TryGetValue(name, out var resolved))
        {
            afmName = resolved;
            return true;
        }

        return false;
    }

    private static bool IsSubsetTag(string name)
    {
        for (var i = 0; i < 6; i++)
        {
            if (name[i] is < 'A' or > 'Z')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the <see cref="Standard14"/> member for one of the 12 text fonts. Returns
    /// <see langword="false"/> for <c>Symbol</c>, <c>ZapfDingbats</c>, or any name
    /// <see cref="TryResolve"/> itself would not have produced.
    /// </summary>
    public static bool TryGetKernelFont(string afmName, out Standard14 font) =>
        _kernelFonts.TryGetValue(afmName, out font);
}
