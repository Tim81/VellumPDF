// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// The three standard simple-font base encodings of ISO 32000-1 Annex D — WinAnsiEncoding,
/// MacRomanEncoding, and StandardEncoding — as char-code (0–255) to glyph-name arrays, plus a
/// helper to resolve a font's <c>/Encoding</c> (a name or a base + <c>/Differences</c> dictionary)
/// to a 256-slot glyph-name map. A <see langword="null"/> slot means the encoding leaves that code
/// undefined.
/// </summary>
/// <remarks>
/// Authored from ISO 32000-1:2008 Annex D (the standard encoding tables). Shared by the glyph-level
/// font rules so the tables live in exactly one place.
/// </remarks>
internal static class SimpleFontEncoding
{
    private static readonly PdfName _baseEncoding = new("BaseEncoding");
    private static readonly PdfName _differences = new("Differences");

    /// <summary>WinAnsiEncoding: char code (0–255) → glyph name (null where undefined).</summary>
    public static readonly string?[] WinAnsi = BuildWinAnsi();

    /// <summary>MacRomanEncoding: char code (0–255) → glyph name (null where undefined).</summary>
    public static readonly string?[] MacRoman = BuildMacRoman();

    /// <summary>Adobe StandardEncoding: char code (0–255) → glyph name (null where undefined).</summary>
    public static readonly string?[] Standard = BuildStandardEncoding();

    /// <summary>Returns the base-encoding table for the named encoding, or null when unknown.</summary>
    public static string?[]? BaseTable(string? name) => name switch
    {
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        "StandardEncoding" => Standard,
        _ => null,
    };

    /// <summary>
    /// Resolves a simple font's <paramref name="encodingObject"/> (already resolved from
    /// <c>/Encoding</c>) to a 256-slot char-code → glyph-name map, applying a base encoding and any
    /// <c>/Differences</c> overlay. Returns <see langword="null"/> when the encoding is neither a
    /// recognised base-encoding name nor a dictionary with a recognised <c>/BaseEncoding</c> or a
    /// <c>/Differences</c> array — i.e. when no glyph names can be confidently derived.
    /// </summary>
    public static string?[]? Resolve(PreflightContext context, PdfObject? encodingObject)
    {
        string? baseName;
        PdfArray? differences = null;

        switch (encodingObject)
        {
            case PdfName n:
                baseName = n.Value;
                break;
            case PdfDictionary d:
                baseName = context.Resolve(d.Get(_baseEncoding)) is PdfName bn ? bn.Value : null;
                if (context.Resolve(d.Get(_differences)) is PdfArray diff)
                    differences = diff;
                break;
            default:
                return null;
        }

        var baseTable = BaseTable(baseName);
        if (baseTable is null && differences is null)
            return null; // no base and no Differences — nothing derivable

        var map = new string?[256];
        if (baseTable is not null)
            Array.Copy(baseTable, map, 256);

        if (differences is not null)
        {
            var code = 0;
            for (var i = 0; i < differences.Count; i++)
            {
                var resolved = context.Resolve(differences[i]);
                if (resolved is PdfInteger idx)
                {
                    code = (int)idx.Value;
                }
                else if (resolved is PdfName gn)
                {
                    if (code >= 0 && code < 256)
                        map[code] = gn.Value;
                    code++;
                }
            }
        }

        return map;
    }

    private static string?[] BuildWinAnsi()
    {
        var t = new string?[256];
        // 0x20–0x7E: printable ASCII (matches standard glyph names)
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
        // 0x80–0x9E: Windows-1252 extensions
        t[0x80] = "Euro"; t[0x82] = "quotesinglbase"; t[0x83] = "florin";
        t[0x84] = "quotedblbase"; t[0x85] = "ellipsis"; t[0x86] = "dagger";
        t[0x87] = "daggerdbl"; t[0x88] = "circumflex"; t[0x89] = "perthousand";
        t[0x8A] = "Scaron"; t[0x8B] = "guilsinglleft"; t[0x8C] = "OE";
        t[0x8E] = "Zcaron";
        t[0x91] = "quoteleft"; t[0x92] = "quoteright"; t[0x93] = "quotedblleft";
        t[0x94] = "quotedblright"; t[0x95] = "bullet"; t[0x96] = "endash";
        t[0x97] = "emdash"; t[0x98] = "tilde"; t[0x99] = "trademark";
        t[0x9A] = "scaron"; t[0x9B] = "guilsinglright"; t[0x9C] = "oe";
        t[0x9E] = "zcaron"; t[0x9F] = "Ydieresis";
        // 0xA0–0xFF: ISO Latin-1 supplement
        t[0xA0] = "nbspace"; t[0xA1] = "exclamdown"; t[0xA2] = "cent"; t[0xA3] = "sterling";
        t[0xA4] = "currency"; t[0xA5] = "yen"; t[0xA6] = "brokenbar"; t[0xA7] = "section";
        t[0xA8] = "dieresis"; t[0xA9] = "copyright"; t[0xAA] = "ordfeminine"; t[0xAB] = "guillemotleft";
        t[0xAC] = "logicalnot"; t[0xAD] = "softhyphen"; t[0xAE] = "registered"; t[0xAF] = "macron";
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
        // 0x20–0x7E: same as WinAnsi (ASCII printable)
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
        // 0x80–0xFF: Mac Roman extended (ISO 32000-1 Annex D.2)
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
        t[0xAC] = "dieresis"; t[0xAD] = "notequal"; t[0xAE] = "AE"; t[0xAF] = "Oslash";
        t[0xB0] = "infinity"; t[0xB1] = "plusminus"; t[0xB2] = "lessequal"; t[0xB3] = "greaterequal";
        t[0xB4] = "yen"; t[0xB5] = "mu"; t[0xB6] = "partialdiff"; t[0xB7] = "summation";
        t[0xB8] = "product"; t[0xB9] = "pi"; t[0xBA] = "integral"; t[0xBB] = "ordfeminine";
        t[0xBC] = "ordmasculine"; t[0xBD] = "Omega"; t[0xBE] = "ae"; t[0xBF] = "oslash";
        t[0xC0] = "questiondown"; t[0xC1] = "exclamdown"; t[0xC2] = "logicalnot"; t[0xC3] = "radical";
        t[0xC4] = "florin"; t[0xC5] = "approxequal"; t[0xC6] = "Delta"; t[0xC7] = "guillemotleft";
        t[0xC8] = "guillemotright"; t[0xC9] = "ellipsis"; t[0xCA] = "nbspace"; t[0xCB] = "Agrave";
        t[0xCC] = "Atilde"; t[0xCD] = "Otilde"; t[0xCE] = "OE"; t[0xCF] = "oe";
        t[0xD0] = "endash"; t[0xD1] = "emdash"; t[0xD2] = "quotedblleft"; t[0xD3] = "quotedblright";
        t[0xD4] = "quoteleft"; t[0xD5] = "quoteright"; t[0xD6] = "divide"; t[0xD7] = "lozenge";
        t[0xD8] = "ydieresis"; t[0xD9] = "Ydieresis"; t[0xDA] = "fraction"; t[0xDB] = "Euro";
        t[0xDC] = "guilsinglleft"; t[0xDD] = "guilsinglright"; t[0xDE] = "fi"; t[0xDF] = "fl";
        t[0xE0] = "daggerdbl"; t[0xE1] = "periodcentered"; t[0xE2] = "quotesinglbase"; t[0xE3] = "quotedblbase";
        t[0xE4] = "perthousand"; t[0xE5] = "Acircumflex"; t[0xE6] = "Ecircumflex"; t[0xE7] = "Aacute";
        t[0xE8] = "Edieresis"; t[0xE9] = "Egrave"; t[0xEA] = "Iacute"; t[0xEB] = "Icircumflex";
        t[0xEC] = "Idieresis"; t[0xED] = "Igrave"; t[0xEE] = "Oacute"; t[0xEF] = "Ocircumflex";
        t[0xF0] = "apple"; t[0xF1] = "Ograve"; t[0xF2] = "Uacute"; t[0xF3] = "Ucircumflex";
        t[0xF4] = "Ugrave"; t[0xF5] = "dotlessi"; t[0xF6] = "circumflex"; t[0xF7] = "tilde";
        t[0xF8] = "macron"; t[0xF9] = "breve"; t[0xFA] = "dotaccent"; t[0xFB] = "ring";
        t[0xFC] = "cedilla"; t[0xFD] = "hungarumlaut"; t[0xFE] = "ogonek"; t[0xFF] = "caron";
        return t;
    }

    private static string?[] BuildStandardEncoding()
    {
        var t = new string?[256];
        // 0x20–0x7E: printable ASCII (same glyph names as WinAnsi for this range)
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
        // 0xA1–0xFF: Standard Encoding extended range (ISO 32000-1 Annex D.1)
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
}
