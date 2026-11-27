// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// How far a font's own bounding box reaches above and below the baseline, which is what a zoned
/// band keeps its rule and its logo clear of. A font's box encloses every glyph it holds, so text
/// drawn in it stays inside the box whichever characters it shows.
/// </summary>
internal static class BandFontExtent
{
    // The FontBBox of each of the 14 faces in Adobe's published AFM metrics, in thousandths of an em:
    // the lowest and the highest y, in the order of the Standard14 enumeration.
    private static readonly (int Low, int High)[] Standard14Boxes =
    [
        (-225, 931),  // Helvetica
        (-228, 962),  // HelveticaBold
        (-225, 931),  // HelveticaOblique
        (-228, 962),  // HelveticaBoldOblique
        (-218, 898),  // TimesRoman
        (-218, 935),  // TimesBold
        (-217, 883),  // TimesItalic
        (-218, 921),  // TimesBoldItalic
        (-250, 805),  // Courier
        (-250, 801),  // CourierBold
        (-250, 805),  // CourierOblique
        (-250, 801),  // CourierBoldOblique
        (-293, 1010), // Symbol
        (-143, 820),  // ZapfDingbats
    ];

    /// <summary>The ascent and the descent of the font in ems, both positive for a normal font.</summary>
    /// <remarks>A <see cref="Standard14"/> value the enumeration does not name reads as Helvetica; a zone refuses one when it is built.</remarks>
    internal static (double Ascent, double Descent) Of(FontReference font)
    {
        if (font.IsEmbedded) return font.Embedded.BoundingBoxExtent;
        var index = (int)font.Standard14;
        var (low, high) = index is >= 0 and < 14 ? Standard14Boxes[index] : Standard14Boxes[0];
        return (high / 1000.0, -low / 1000.0);
    }
}
