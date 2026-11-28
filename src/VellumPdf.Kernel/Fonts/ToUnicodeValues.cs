// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Fonts;

/// <summary>
/// The one definition of a value a font's ToUnicode CMap may not hold under the claims that need
/// the map, and of the value written instead. The Kernel's CMap writer and the Layout elements
/// that refuse such a character up front both call it, so the two cannot drift.
/// </summary>
/// <remarks>
/// ISO 19005-2, 6.2.11.7.2 (levels U and A) and ISO 14289-1, 7.21.7 require each value to be
/// greater than zero and not U+FEFF or U+FFFE.
/// </remarks>
internal static class ToUnicodeValues
{
    /// <summary>The word joiner. UAX #14 puts it in line-break class WJ with U+FEFF and names it
    /// the preferred character for invisible glue, because U+FEFF is also a byte order mark.</summary>
    internal const int WordJoiner = 0x2060;

    /// <summary>The replacement character, the value glyph 0 already gets.</summary>
    internal const int ReplacementCharacter = 0xFFFD;

    /// <summary>True for the claims whose clause requires a ToUnicode CMap value rule: PDF/A-2u,
    /// PDF/A-2a and PDF/UA-1. PDF/A-2b has no such rule.</summary>
    internal static bool ClaimNeedsToUnicode(PdfConformance claim) =>
        claim is PdfConformance.PdfA2u or PdfConformance.PdfA2a or PdfConformance.PdfUA1;

    /// <summary>True when <paramref name="codePoint"/> cannot be a ToUnicode value: U+0000, U+FEFF
    /// or U+FFFE.</summary>
    internal static bool IsForbidden(int codePoint) => codePoint is 0 or 0xFEFF or 0xFFFE;

    /// <summary>The value written for a forbidden <paramref name="codePoint"/>: U+2060 for U+FEFF
    /// and U+FFFD for the other two.</summary>
    internal static int SubstituteFor(int codePoint) =>
        codePoint == 0xFEFF ? WordJoiner : ReplacementCharacter;
}
