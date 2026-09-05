// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Fonts;

/// <summary>
/// The predefined simple-font encodings of ISO 32000-2:2020 Annex D.2 (Latin character set and
/// encodings): StandardEncoding, WinAnsiEncoding and MacRomanEncoding, each a 256-entry char-code
/// to glyph-name table, plus MacExpertEncoding, which this reader recognises by name only (see
/// <see cref="MacExpert"/>). Symbol and ZapfDingbats are not named encodings a font's own
/// <c>/Encoding</c> entry can select; their built-in encodings live in
/// <see cref="SymbolFontMetrics"/> instead. §9.6.5 lists three predefined encoding names
/// (MacRomanEncoding, MacExpertEncoding, WinAnsiEncoding), and Annex D.1 says "PDF processors
/// shall not have a predefined encoding named StandardEncoding"; <see cref="TryGetNamed"/>
/// accepts <c>StandardEncoding</c> by name anyway, silently, as a leniency toward producers that
/// write it. The table itself is needed regardless, as the default base encoding of Table 112.
/// </summary>
/// <remarks>
/// Transcribed from the Annex D.2 table (rendered page images, not the Conformance package's copy)
/// with three footnotes applied to WinAnsiEncoding. Footnote 3, verbatim: "In WinAnsiEncoding, all
/// unused codes greater than 40 map to the bullet character. However, only code 225 is specifically
/// assigned to the bullet character; other codes are subject to future reassignment." (40 and 225
/// are octal: 0x20 and 0x95.) Codes 0x7F, 0x81, 0x8D, 0x8F, 0x90 and 0x9D are the codes that
/// footnote covers and have no other assignment in the table; this reader fills all six with
/// <c>bullet</c>, the codes remaining, in the footnote's own words, "subject to future
/// reassignment". Footnotes 5 and 6 record that WinAnsiEncoding additionally encodes hyphen at
/// 0xAD and space at 0xA0 (Windows Code Page 1252 reads those codes as soft hyphen and
/// non-breaking space instead); this reader fills them with the plain <c>hyphen</c> and
/// <c>space</c> names rather than the AGL's separate <c>softhyphen</c>/<c>nonbreakingspace</c>
/// names, so a producer that means the distinct Unicode codepoint says so with its own
/// <c>/Differences</c> entry, per the footnotes' own example.
/// <para>
/// <c>src/VellumPdf.Conformance/Rules/Fonts/SimpleFontEncoding.cs</c> carries its own copy of
/// these three tables, deliberately left as it is (Conformance is Shipped and its verdicts are
/// pinned against veraPDF). It diverges from the tables here at exactly
/// eight WinAnsi codes (the six bullet fills above, plus 0xA0 and 0xAD, which that copy encodes
/// under the AGL's own non-breaking-space/soft-hyphen names instead of the plain ones this reader
/// uses) and seventeen MacRoman codes: fifteen where that copy carries a Mac OS Roman (1, 0)
/// cmap-fallback glyph, from ISO 32000-2 Table 113 ("Additional entries in Mac OS Roman encoding
/// not in MacRomanEncoding"), that Annex D.2 itself does not assign to MacRomanEncoding at all
/// (<c>notequal</c>, <c>infinity</c>, <c>lessequal</c>, <c>greaterequal</c>, <c>partialdiff</c>,
/// <c>summation</c>, <c>product</c>, <c>pi</c>, <c>integral</c>, <c>Omega</c>, <c>radical</c>,
/// <c>approxequal</c>, <c>Delta</c>, <c>lozenge</c> and <c>apple</c>; §9.6.5.4 places Table 113 in
/// the TrueType (1, 0) subtable fallback step, not in building the base encoding table), plus two
/// codes where that copy's name disagrees with this class's own table: 0xCA (Annex D.2's own
/// MacRoman column is blank there; footnote 6 assigns it <c>space</c>, "encoded as 312 (octal) in
/// MacRomanEncoding", the same code WinAnsi's own footnote 6 dual-maps at 0xA0) and 0xDB (Annex
/// D.2's own column reads <c>currency</c>; footnote 1: Apple's later Mac OS Roman revision
/// reassigned that code to the Euro sign, but "this incompatible change has not been reflected in
/// PDF's MacRomanEncoding, which continues to map code 333 to currency").
/// </para>
/// </remarks>
internal static class SimpleFontEncodings
{
    private static readonly string?[] _standard = BuildStandard();
    private static readonly string?[] _winAnsi = BuildWinAnsi();
    private static readonly string?[] _macRoman = BuildMacRoman();
    private static readonly string?[] _macExpert = new string?[256];

    /// <summary>Adobe StandardEncoding (ISO 32000-2 Annex D.2), char code 0-255 to glyph
    /// name.</summary>
    public static ReadOnlySpan<string?> Standard => _standard;

    /// <summary>WinAnsiEncoding (ISO 32000-2 Annex D.2, footnotes 3, 5 and 6 applied).</summary>
    public static ReadOnlySpan<string?> WinAnsi => _winAnsi;

    /// <summary>MacRomanEncoding (ISO 32000-2 Annex D.2).</summary>
    public static ReadOnlySpan<string?> MacRoman => _macRoman;

    /// <summary>
    /// MacExpertEncoding: every cell is <see langword="null"/>. Annex D.4 (Expert set and
    /// MacExpert encoding) is not transcribed here: no oracle in this test suite exercises it, and
    /// fonts that declare it are rare, so a font naming it gets the same outcome as a symbolic
    /// font with no encoding: every code has no name, and text extraction reports no glyph for any
    /// of them, rather than this reader refusing to recognise the name at all. Because the cells
    /// are null for want of a transcription, not because Annex D.4 leaves them undefined,
    /// <see cref="SimpleFontReader"/> does not run §9.6.5.4's StandardEncoding fill over a table
    /// built from this one.
    /// </summary>
    public static ReadOnlySpan<string?> MacExpert => _macExpert;

    /// <summary>
    /// Resolves a <c>/BaseEncoding</c> or <c>/Encoding</c> name to its table. Recognises exactly
    /// <c>StandardEncoding</c>, <c>WinAnsiEncoding</c>, <c>MacRomanEncoding</c> and
    /// <c>MacExpertEncoding</c>; any other name, including a close variant, returns
    /// <see langword="false"/>. The returned span is backed by a shared static array and must be
    /// copied (<c>ToArray()</c>) before a caller modifies a per-font table built from it.
    /// </summary>
    public static bool TryGetNamed(string name, out ReadOnlySpan<string?> table)
    {
        switch (name)
        {
            case "StandardEncoding": table = Standard; return true;
            case "WinAnsiEncoding": table = WinAnsi; return true;
            case "MacRomanEncoding": table = MacRoman; return true;
            case "MacExpertEncoding": table = MacExpert; return true;
            default: table = default; return false;
        }
    }

    private static string?[] BuildStandard()
    {
        var t = new string?[256];
        t[0x20] = "space"; t[0x21] = "exclam"; t[0x22] = "quotedbl"; t[0x23] = "numbersign";
        t[0x24] = "dollar"; t[0x25] = "percent"; t[0x26] = "ampersand"; t[0x27] = "quoteright";
        t[0x28] = "parenleft"; t[0x29] = "parenright"; t[0x2A] = "asterisk"; t[0x2B] = "plus";
        t[0x2C] = "comma"; t[0x2D] = "hyphen"; t[0x2E] = "period"; t[0x2F] = "slash";
        t[0x30] = "zero"; t[0x31] = "one"; t[0x32] = "two"; t[0x33] = "three";
        t[0x34] = "four"; t[0x35] = "five"; t[0x36] = "six"; t[0x37] = "seven";
        t[0x38] = "eight"; t[0x39] = "nine"; t[0x3A] = "colon"; t[0x3B] = "semicolon";
        t[0x3C] = "less"; t[0x3D] = "equal"; t[0x3E] = "greater"; t[0x3F] = "question";
        t[0x40] = "at";
        t[0x41] = "A"; t[0x42] = "B"; t[0x43] = "C"; t[0x44] = "D"; t[0x45] = "E"; t[0x46] = "F";
        t[0x47] = "G"; t[0x48] = "H"; t[0x49] = "I"; t[0x4A] = "J"; t[0x4B] = "K"; t[0x4C] = "L";
        t[0x4D] = "M"; t[0x4E] = "N"; t[0x4F] = "O"; t[0x50] = "P"; t[0x51] = "Q"; t[0x52] = "R";
        t[0x53] = "S"; t[0x54] = "T"; t[0x55] = "U"; t[0x56] = "V"; t[0x57] = "W"; t[0x58] = "X";
        t[0x59] = "Y"; t[0x5A] = "Z";
        t[0x5B] = "bracketleft"; t[0x5C] = "backslash"; t[0x5D] = "bracketright";
        t[0x5E] = "asciicircum"; t[0x5F] = "underscore"; t[0x60] = "quoteleft";
        t[0x61] = "a"; t[0x62] = "b"; t[0x63] = "c"; t[0x64] = "d"; t[0x65] = "e"; t[0x66] = "f";
        t[0x67] = "g"; t[0x68] = "h"; t[0x69] = "i"; t[0x6A] = "j"; t[0x6B] = "k"; t[0x6C] = "l";
        t[0x6D] = "m"; t[0x6E] = "n"; t[0x6F] = "o"; t[0x70] = "p"; t[0x71] = "q"; t[0x72] = "r";
        t[0x73] = "s"; t[0x74] = "t"; t[0x75] = "u"; t[0x76] = "v"; t[0x77] = "w"; t[0x78] = "x";
        t[0x79] = "y"; t[0x7A] = "z";
        t[0x7B] = "braceleft"; t[0x7C] = "bar"; t[0x7D] = "braceright"; t[0x7E] = "asciitilde";
        t[0xA1] = "exclamdown"; t[0xA2] = "cent"; t[0xA3] = "sterling"; t[0xA4] = "fraction";
        t[0xA5] = "yen"; t[0xA6] = "florin"; t[0xA7] = "section"; t[0xA8] = "currency";
        t[0xA9] = "quotesingle"; t[0xAA] = "quotedblleft"; t[0xAB] = "guillemotleft";
        t[0xAC] = "guilsinglleft"; t[0xAD] = "guilsinglright"; t[0xAE] = "fi"; t[0xAF] = "fl";
        t[0xB1] = "endash"; t[0xB2] = "dagger"; t[0xB3] = "daggerdbl"; t[0xB4] = "periodcentered";
        t[0xB6] = "paragraph"; t[0xB7] = "bullet"; t[0xB8] = "quotesinglbase"; t[0xB9] = "quotedblbase";
        t[0xBA] = "quotedblright"; t[0xBB] = "guillemotright"; t[0xBC] = "ellipsis";
        t[0xBD] = "perthousand"; t[0xBF] = "questiondown";
        t[0xC1] = "grave"; t[0xC2] = "acute"; t[0xC3] = "circumflex"; t[0xC4] = "tilde";
        t[0xC5] = "macron"; t[0xC6] = "breve"; t[0xC7] = "dotaccent"; t[0xC8] = "dieresis";
        t[0xCA] = "ring"; t[0xCB] = "cedilla"; t[0xCD] = "hungarumlaut"; t[0xCE] = "ogonek";
        t[0xCF] = "caron"; t[0xD0] = "emdash";
        t[0xE1] = "AE"; t[0xE3] = "ordfeminine"; t[0xE8] = "Lslash"; t[0xE9] = "Oslash";
        t[0xEA] = "OE"; t[0xEB] = "ordmasculine"; t[0xF1] = "ae"; t[0xF5] = "dotlessi";
        t[0xF8] = "lslash"; t[0xF9] = "oslash"; t[0xFA] = "oe"; t[0xFB] = "germandbls";
        return t;
    }

    private static string?[] BuildWinAnsi()
    {
        var t = new string?[256];
        t[0x20] = "space"; t[0x21] = "exclam"; t[0x22] = "quotedbl"; t[0x23] = "numbersign";
        t[0x24] = "dollar"; t[0x25] = "percent"; t[0x26] = "ampersand"; t[0x27] = "quotesingle";
        t[0x28] = "parenleft"; t[0x29] = "parenright"; t[0x2A] = "asterisk"; t[0x2B] = "plus";
        t[0x2C] = "comma"; t[0x2D] = "hyphen"; t[0x2E] = "period"; t[0x2F] = "slash";
        t[0x30] = "zero"; t[0x31] = "one"; t[0x32] = "two"; t[0x33] = "three";
        t[0x34] = "four"; t[0x35] = "five"; t[0x36] = "six"; t[0x37] = "seven";
        t[0x38] = "eight"; t[0x39] = "nine"; t[0x3A] = "colon"; t[0x3B] = "semicolon";
        t[0x3C] = "less"; t[0x3D] = "equal"; t[0x3E] = "greater"; t[0x3F] = "question";
        t[0x40] = "at";
        t[0x41] = "A"; t[0x42] = "B"; t[0x43] = "C"; t[0x44] = "D"; t[0x45] = "E"; t[0x46] = "F";
        t[0x47] = "G"; t[0x48] = "H"; t[0x49] = "I"; t[0x4A] = "J"; t[0x4B] = "K"; t[0x4C] = "L";
        t[0x4D] = "M"; t[0x4E] = "N"; t[0x4F] = "O"; t[0x50] = "P"; t[0x51] = "Q"; t[0x52] = "R";
        t[0x53] = "S"; t[0x54] = "T"; t[0x55] = "U"; t[0x56] = "V"; t[0x57] = "W"; t[0x58] = "X";
        t[0x59] = "Y"; t[0x5A] = "Z";
        t[0x5B] = "bracketleft"; t[0x5C] = "backslash"; t[0x5D] = "bracketright";
        t[0x5E] = "asciicircum"; t[0x5F] = "underscore"; t[0x60] = "grave";
        t[0x61] = "a"; t[0x62] = "b"; t[0x63] = "c"; t[0x64] = "d"; t[0x65] = "e"; t[0x66] = "f";
        t[0x67] = "g"; t[0x68] = "h"; t[0x69] = "i"; t[0x6A] = "j"; t[0x6B] = "k"; t[0x6C] = "l";
        t[0x6D] = "m"; t[0x6E] = "n"; t[0x6F] = "o"; t[0x70] = "p"; t[0x71] = "q"; t[0x72] = "r";
        t[0x73] = "s"; t[0x74] = "t"; t[0x75] = "u"; t[0x76] = "v"; t[0x77] = "w"; t[0x78] = "x";
        t[0x79] = "y"; t[0x7A] = "z";
        t[0x7B] = "braceleft"; t[0x7C] = "bar"; t[0x7D] = "braceright"; t[0x7E] = "asciitilde";
        // Footnote 3: the six codes this table would otherwise leave undefined between 0x20 and
        // 0xFF, filled with bullet; see this class's own remarks for the footnote's exact words.
        t[0x7F] = "bullet";
        t[0x80] = "Euro"; t[0x81] = "bullet"; t[0x82] = "quotesinglbase"; t[0x83] = "florin";
        t[0x84] = "quotedblbase"; t[0x85] = "ellipsis"; t[0x86] = "dagger";
        t[0x87] = "daggerdbl"; t[0x88] = "circumflex"; t[0x89] = "perthousand";
        t[0x8A] = "Scaron"; t[0x8B] = "guilsinglleft"; t[0x8C] = "OE";
        t[0x8D] = "bullet"; t[0x8E] = "Zcaron"; t[0x8F] = "bullet";
        t[0x90] = "bullet"; t[0x91] = "quoteleft"; t[0x92] = "quoteright"; t[0x93] = "quotedblleft";
        t[0x94] = "quotedblright"; t[0x95] = "bullet"; t[0x96] = "endash";
        t[0x97] = "emdash"; t[0x98] = "tilde"; t[0x99] = "trademark";
        t[0x9A] = "scaron"; t[0x9B] = "guilsinglright"; t[0x9C] = "oe";
        t[0x9D] = "bullet"; t[0x9E] = "zcaron"; t[0x9F] = "Ydieresis";
        // Footnotes 5 and 6: the dual mapping described in this class's own remarks.
        t[0xA0] = "space";
        t[0xA1] = "exclamdown"; t[0xA2] = "cent"; t[0xA3] = "sterling";
        t[0xA4] = "currency"; t[0xA5] = "yen"; t[0xA6] = "brokenbar"; t[0xA7] = "section";
        t[0xA8] = "dieresis"; t[0xA9] = "copyright"; t[0xAA] = "ordfeminine"; t[0xAB] = "guillemotleft";
        t[0xAC] = "logicalnot"; t[0xAD] = "hyphen"; t[0xAE] = "registered"; t[0xAF] = "macron";
        t[0xB0] = "degree"; t[0xB1] = "plusminus"; t[0xB2] = "twosuperior"; t[0xB3] = "threesuperior";
        t[0xB4] = "acute"; t[0xB5] = "mu"; t[0xB6] = "paragraph"; t[0xB7] = "periodcentered";
        t[0xB8] = "cedilla"; t[0xB9] = "onesuperior"; t[0xBA] = "ordmasculine"; t[0xBB] = "guillemotright";
        t[0xBC] = "onequarter"; t[0xBD] = "onehalf"; t[0xBE] = "threequarters"; t[0xBF] = "questiondown";
        t[0xC0] = "Agrave"; t[0xC1] = "Aacute"; t[0xC2] = "Acircumflex"; t[0xC3] = "Atilde";
        t[0xC4] = "Adieresis"; t[0xC5] = "Aring"; t[0xC6] = "AE"; t[0xC7] = "Ccedilla";
        t[0xC8] = "Egrave"; t[0xC9] = "Eacute"; t[0xCA] = "Ecircumflex"; t[0xCB] = "Edieresis";
        t[0xCC] = "Igrave"; t[0xCD] = "Iacute"; t[0xCE] = "Icircumflex"; t[0xCF] = "Idieresis";
        t[0xD0] = "Eth"; t[0xD1] = "Ntilde"; t[0xD2] = "Ograve"; t[0xD3] = "Oacute";
        t[0xD4] = "Ocircumflex"; t[0xD5] = "Otilde"; t[0xD6] = "Odieresis"; t[0xD7] = "multiply";
        t[0xD8] = "Oslash"; t[0xD9] = "Ugrave"; t[0xDA] = "Uacute"; t[0xDB] = "Ucircumflex";
        t[0xDC] = "Udieresis"; t[0xDD] = "Yacute"; t[0xDE] = "Thorn"; t[0xDF] = "germandbls";
        t[0xE0] = "agrave"; t[0xE1] = "aacute"; t[0xE2] = "acircumflex"; t[0xE3] = "atilde";
        t[0xE4] = "adieresis"; t[0xE5] = "aring"; t[0xE6] = "ae"; t[0xE7] = "ccedilla";
        t[0xE8] = "egrave"; t[0xE9] = "eacute"; t[0xEA] = "ecircumflex"; t[0xEB] = "edieresis";
        t[0xEC] = "igrave"; t[0xED] = "iacute"; t[0xEE] = "icircumflex"; t[0xEF] = "idieresis";
        t[0xF0] = "eth"; t[0xF1] = "ntilde"; t[0xF2] = "ograve"; t[0xF3] = "oacute";
        t[0xF4] = "ocircumflex"; t[0xF5] = "otilde"; t[0xF6] = "odieresis"; t[0xF7] = "divide";
        t[0xF8] = "oslash"; t[0xF9] = "ugrave"; t[0xFA] = "uacute"; t[0xFB] = "ucircumflex";
        t[0xFC] = "udieresis"; t[0xFD] = "yacute"; t[0xFE] = "thorn"; t[0xFF] = "ydieresis";
        return t;
    }

    private static string?[] BuildMacRoman()
    {
        var t = new string?[256];
        t[0x20] = "space"; t[0x21] = "exclam"; t[0x22] = "quotedbl"; t[0x23] = "numbersign";
        t[0x24] = "dollar"; t[0x25] = "percent"; t[0x26] = "ampersand"; t[0x27] = "quotesingle";
        t[0x28] = "parenleft"; t[0x29] = "parenright"; t[0x2A] = "asterisk"; t[0x2B] = "plus";
        t[0x2C] = "comma"; t[0x2D] = "hyphen"; t[0x2E] = "period"; t[0x2F] = "slash";
        t[0x30] = "zero"; t[0x31] = "one"; t[0x32] = "two"; t[0x33] = "three";
        t[0x34] = "four"; t[0x35] = "five"; t[0x36] = "six"; t[0x37] = "seven";
        t[0x38] = "eight"; t[0x39] = "nine"; t[0x3A] = "colon"; t[0x3B] = "semicolon";
        t[0x3C] = "less"; t[0x3D] = "equal"; t[0x3E] = "greater"; t[0x3F] = "question";
        t[0x40] = "at";
        t[0x41] = "A"; t[0x42] = "B"; t[0x43] = "C"; t[0x44] = "D"; t[0x45] = "E"; t[0x46] = "F";
        t[0x47] = "G"; t[0x48] = "H"; t[0x49] = "I"; t[0x4A] = "J"; t[0x4B] = "K"; t[0x4C] = "L";
        t[0x4D] = "M"; t[0x4E] = "N"; t[0x4F] = "O"; t[0x50] = "P"; t[0x51] = "Q"; t[0x52] = "R";
        t[0x53] = "S"; t[0x54] = "T"; t[0x55] = "U"; t[0x56] = "V"; t[0x57] = "W"; t[0x58] = "X";
        t[0x59] = "Y"; t[0x5A] = "Z";
        t[0x5B] = "bracketleft"; t[0x5C] = "backslash"; t[0x5D] = "bracketright";
        t[0x5E] = "asciicircum"; t[0x5F] = "underscore"; t[0x60] = "grave";
        t[0x61] = "a"; t[0x62] = "b"; t[0x63] = "c"; t[0x64] = "d"; t[0x65] = "e"; t[0x66] = "f";
        t[0x67] = "g"; t[0x68] = "h"; t[0x69] = "i"; t[0x6A] = "j"; t[0x6B] = "k"; t[0x6C] = "l";
        t[0x6D] = "m"; t[0x6E] = "n"; t[0x6F] = "o"; t[0x70] = "p"; t[0x71] = "q"; t[0x72] = "r";
        t[0x73] = "s"; t[0x74] = "t"; t[0x75] = "u"; t[0x76] = "v"; t[0x77] = "w"; t[0x78] = "x";
        t[0x79] = "y"; t[0x7A] = "z";
        t[0x7B] = "braceleft"; t[0x7C] = "bar"; t[0x7D] = "braceright"; t[0x7E] = "asciitilde";
        t[0x80] = "Adieresis"; t[0x81] = "Aring"; t[0x82] = "Ccedilla"; t[0x83] = "Eacute";
        t[0x84] = "Ntilde"; t[0x85] = "Odieresis"; t[0x86] = "Udieresis"; t[0x87] = "aacute";
        t[0x88] = "agrave"; t[0x89] = "acircumflex"; t[0x8A] = "adieresis"; t[0x8B] = "atilde";
        t[0x8C] = "aring"; t[0x8D] = "ccedilla"; t[0x8E] = "eacute"; t[0x8F] = "egrave";
        t[0x90] = "ecircumflex"; t[0x91] = "edieresis"; t[0x92] = "iacute"; t[0x93] = "igrave";
        t[0x94] = "icircumflex"; t[0x95] = "idieresis"; t[0x96] = "ntilde"; t[0x97] = "oacute";
        t[0x98] = "ograve"; t[0x99] = "ocircumflex"; t[0x9A] = "odieresis"; t[0x9B] = "otilde";
        t[0x9C] = "uacute"; t[0x9D] = "ugrave"; t[0x9E] = "ucircumflex"; t[0x9F] = "udieresis";
        t[0xA0] = "dagger"; t[0xA1] = "degree"; t[0xA2] = "cent"; t[0xA3] = "sterling";
        t[0xA4] = "section"; t[0xA5] = "bullet"; t[0xA6] = "paragraph"; t[0xA7] = "germandbls";
        t[0xA8] = "registered"; t[0xA9] = "copyright"; t[0xAA] = "trademark"; t[0xAB] = "acute";
        t[0xAC] = "dieresis";
        // 0xAD ("notequal" in Mac OS Roman's own charset) is not one of Annex D.2's MacRoman
        // cells (see this class's own remarks), so this reader leaves it undefined.
        t[0xAE] = "AE"; t[0xAF] = "Oslash";
        // 0xB0, 0xB2, 0xB3, 0xB6-0xBA and 0xBD (Mac OS Roman's own math/symbol glyphs) are the
        // same kind of Table 113 cell as 0xAD above; left undefined for the same reason.
        t[0xB1] = "plusminus";
        t[0xB4] = "yen"; t[0xB5] = "mu";
        t[0xBB] = "ordfeminine";
        t[0xBC] = "ordmasculine"; t[0xBE] = "ae"; t[0xBF] = "oslash";
        t[0xC0] = "questiondown"; t[0xC1] = "exclamdown"; t[0xC2] = "logicalnot";
        // 0xC3, 0xC5, 0xC6 (radical, approxequal, Delta): Table 113 cells, not Annex D.2 ones.
        t[0xC4] = "florin";
        t[0xC7] = "guillemotleft";
        t[0xC8] = "guillemotright"; t[0xC9] = "ellipsis";
        // Footnote 6's dual mapping (see this class's own remarks): plain "space", not the AGL's
        // separate "nonbreakingspace" name.
        t[0xCA] = "space";
        t[0xCB] = "Agrave";
        t[0xCC] = "Atilde"; t[0xCD] = "Otilde"; t[0xCE] = "OE"; t[0xCF] = "oe";
        t[0xD0] = "endash"; t[0xD1] = "emdash"; t[0xD2] = "quotedblleft"; t[0xD3] = "quotedblright";
        t[0xD4] = "quoteleft"; t[0xD5] = "quoteright"; t[0xD6] = "divide";
        // 0xD7 (lozenge): a Table 113 cell, not an Annex D.2 one.
        t[0xD8] = "ydieresis"; t[0xD9] = "Ydieresis"; t[0xDA] = "fraction";
        // Footnote 1: Annex D.2 and its own text both read "currency" at this code; Apple's later
        // Mac OS Roman revision reassigned it to the Euro sign, but PDF's MacRomanEncoding does
        // not follow that change (see this class's own remarks for the footnote's exact words).
        t[0xDB] = "currency";
        t[0xDC] = "guilsinglleft"; t[0xDD] = "guilsinglright"; t[0xDE] = "fi"; t[0xDF] = "fl";
        t[0xE0] = "daggerdbl"; t[0xE1] = "periodcentered"; t[0xE2] = "quotesinglbase"; t[0xE3] = "quotedblbase";
        t[0xE4] = "perthousand"; t[0xE5] = "Acircumflex"; t[0xE6] = "Ecircumflex"; t[0xE7] = "Aacute";
        t[0xE8] = "Edieresis"; t[0xE9] = "Egrave"; t[0xEA] = "Iacute"; t[0xEB] = "Icircumflex";
        t[0xEC] = "Idieresis"; t[0xED] = "Igrave"; t[0xEE] = "Oacute"; t[0xEF] = "Ocircumflex";
        // 0xF0 (apple): the Mac OS Apple-logo glyph, a Table 113 cell, not an Annex D.2 one.
        t[0xF1] = "Ograve"; t[0xF2] = "Uacute"; t[0xF3] = "Ucircumflex";
        t[0xF4] = "Ugrave"; t[0xF5] = "dotlessi"; t[0xF6] = "circumflex"; t[0xF7] = "tilde";
        t[0xF8] = "macron"; t[0xF9] = "breve"; t[0xFA] = "dotaccent"; t[0xFB] = "ring";
        t[0xFC] = "cedilla"; t[0xFD] = "hungarumlaut"; t[0xFE] = "ogonek"; t[0xFF] = "caron";
        return t;
    }
}
