// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts;

/// <summary>
/// The no-break spaces, and what stands in for the two that a font may not be able to show (#562).
///
/// <para>A no-break space is a character that is white space to <see cref="char.IsWhiteSpace(char)"/>
/// and that UAX #14 puts in line-breaking class GL, which "prohibits breaks on either side". Exactly
/// three characters are both: U+00A0 NO-BREAK SPACE, U+2007 FIGURE SPACE and U+202F NARROW NO-BREAK
/// SPACE. Checked against LineBreak.txt for Unicode 15.1: U+180E is GL but is not white space in
/// .NET, and U+2060 and U+FEFF are class WJ and not white space, so none of those needs handling.</para>
///
/// <para>Layout keeps these characters in a paragraph's text so a line cannot break at them. That
/// leaves the question of how a font draws them. WinAnsiEncoding, which the Standard-14 text fonts
/// use, has U+00A0 but neither of the others, and a font embedded from a file may lack the glyph for
/// them. Before this class, Layout folded them into an ordinary space first, so text containing
/// them was drawn correctly if breakable at the wrong places. Keeping them would have drawn
/// <c>?</c> in the Standard-14 fonts and the missing-glyph box in a font without the glyph, so the
/// two places that encode and measure text fall back to U+00A0, and for an embedded font then to
/// U+0020, which every font has. The fallback is wider than a narrow space, which is the best a
/// font without the character can do.</para>
///
/// <para>There is one definition of the set so that Layout's line breaking, the Standard-14 encoder
/// and the embedded-font glyph lookup cannot drift apart.</para>
/// </summary>
internal static class NoBreakSpaces
{
    /// <summary>U+00A0 NO-BREAK SPACE. WinAnsiEncoding encodes it as 0xA0.</summary>
    public const char NoBreakSpace = '\u00A0';

    /// <summary>U+202F NARROW NO-BREAK SPACE, used before <c>; ? !</c> in French typography.</summary>
    public const char NarrowNoBreakSpace = '\u202F';

    /// <summary>U+2007 FIGURE SPACE, used to align digits.</summary>
    public const char FigureSpace = '\u2007';

    /// <summary>
    /// Whether <paramref name="c"/> is white space that must not be a break point: U+00A0, U+202F
    /// or U+2007.
    /// </summary>
    public static bool IsNoBreakSpace(char c) => c is NoBreakSpace or NarrowNoBreakSpace or FigureSpace;

    /// <summary>
    /// Whether <paramref name="codePoint"/> is a no-break space that WinAnsiEncoding cannot encode
    /// and a font may lack, so that a lookup which fails is retried as U+00A0.
    /// </summary>
    public static bool NeedsSpaceFallback(int codePoint) => codePoint is NarrowNoBreakSpace or FigureSpace;

    /// <summary>
    /// Looks <paramref name="codePoint"/> up with <paramref name="glyphOf"/> and, for a no-break
    /// space the font does not have, falls back to U+00A0 and then to U+0020. Any other code point,
    /// and one the font has, is returned as looked up, glyph 0 (.notdef) included.
    /// </summary>
    /// <param name="codePoint">The code point to find a glyph for.</param>
    /// <param name="glyphOf">The font's lookup, returning 0 for a code point it has no glyph for.</param>
    public static ushort ResolveGlyph(int codePoint, Func<int, ushort> glyphOf)
    {
        var glyph = glyphOf(codePoint);
        if (glyph != 0 || !NeedsSpaceFallback(codePoint)) return glyph;

        glyph = glyphOf(NoBreakSpace);
        return glyph != 0 ? glyph : glyphOf(' ');
    }
}
