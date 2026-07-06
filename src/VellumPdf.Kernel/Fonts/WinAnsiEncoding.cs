// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts;

/// <summary>
/// Maps UTF-16 chars to WinAnsiEncoding byte codes (ISO 32000-2 Annex D), the encoding
/// <see cref="PdfFontResource.BuildDictionary"/> declares for the 12 non-symbolic Standard-14
/// fonts and <see cref="VellumPdf.Canvas.PdfCanvas.ShowText"/> encodes against.
/// </summary>
internal static class WinAnsiEncoding
{
    /// <summary>
    /// Maps a UTF-16 char to its WinAnsiEncoding byte. Code points U+0000–U+00FF map to the identical
    /// byte (WinAnsi agrees with Latin-1 across 0xA0–0xFF and ASCII); the WinAnsi 0x80–0x9F punctuation
    /// block (bullet, en/em dash, ellipsis, curly quotes, …), whose code points sit above U+00FF, maps
    /// to its 0x80–0x9F code. Returns false for any char WinAnsiEncoding does not cover.
    ///
    /// <para>
    /// The identity branch also returns a byte for U+0080–U+009F (the C1 control range),
    /// even though WinAnsiEncoding leaves several of those codes undefined — this keeps the
    /// method byte-compatible with the Latin-1 encoder it replaced. It is a byte encoder, not
    /// a strict CP1252 validator.
    /// </para>
    /// </summary>
    public static bool TryGetByte(char c, out byte b)
    {
        if (c <= 0xFF)
        {
            b = (byte)c;
            return true;
        }

        if (_highPunctuation.TryGetValue(c, out var mapped))
        {
            b = mapped;
            return true;
        }

        b = 0;
        return false;
    }

    // The WinAnsi 0x80–0x9F code points whose Unicode value sits above U+00FF. Codes 0x81, 0x8D,
    // 0x8F, 0x90, and 0x9D are undefined in WinAnsiEncoding and are intentionally absent here;
    // TryGetByte returns false for their Unicode code points. Cross-checked against the byte→glyph
    // table in VellumPdf.Conformance.Rules.Fonts.SimpleFontEncoding.BuildWinAnsi().
    private static readonly Dictionary<char, byte> _highPunctuation = new()
    {
        ['€'] = 0x80, // Euro
        ['‚'] = 0x82, // quotesinglbase
        ['ƒ'] = 0x83, // florin
        ['„'] = 0x84, // quotedblbase
        ['…'] = 0x85, // ellipsis
        ['†'] = 0x86, // dagger
        ['‡'] = 0x87, // daggerdbl
        ['ˆ'] = 0x88, // circumflex
        ['‰'] = 0x89, // perthousand
        ['Š'] = 0x8A, // Scaron
        ['‹'] = 0x8B, // guilsinglleft
        ['Œ'] = 0x8C, // OE
        ['Ž'] = 0x8E, // Zcaron
        ['‘'] = 0x91, // quoteleft
        ['’'] = 0x92, // quoteright
        ['“'] = 0x93, // quotedblleft
        ['”'] = 0x94, // quotedblright
        ['•'] = 0x95, // bullet
        ['–'] = 0x96, // endash
        ['—'] = 0x97, // emdash
        ['˜'] = 0x98, // tilde
        ['™'] = 0x99, // trademark
        ['š'] = 0x9A, // scaron
        ['›'] = 0x9B, // guilsinglright
        ['œ'] = 0x9C, // oe
        ['ž'] = 0x9E, // zcaron
        ['Ÿ'] = 0x9F, // Ydieresis
    };
}
