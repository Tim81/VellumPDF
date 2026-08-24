// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Encodes a password string to PDFDocEncoding bytes (ISO 32000-2 Annex D.2), the encoding the
/// Adobe Supplement to ISO 32000, BaseVersion 1.7, ExtensionLevel 3, §3.5.2, Algorithm 3.2 step 1,
/// specifies for an R&lt;=4 password (ISO 32000-1 §7.6 itself says nothing about password character
/// encoding — see <c>StandardSecurityDecryptor.TryComputeFileKeyFromUserPassword</c>'s doc comment).
/// Used only as a fallback when a supplied password fails to authenticate as UTF-8 bytes first — the
/// same order qpdf's password recovery tries them in.
///
/// <para>
/// PDFDocEncoding agrees with Latin-1 (ISO 8859-1) outside the 0x18–0x1F and 0x80–0x9E blocks, with
/// one exception: 0xA0 is EURO SIGN where Latin-1 has NO-BREAK SPACE, so U+00A0 has no
/// representation and U+20AC has that byte. Everything else is the identity, which is what lets this
/// be a table of exceptions rather than 256 entries (compare <c>VellumPdf.Fonts.WinAnsiEncoding</c>,
/// which takes the same shape for the unrelated WinAnsiEncoding table).
/// </para>
/// </summary>
internal static class PdfDocEncoding
{
    /// <summary>
    /// Encodes <paramref name="password"/> to PDFDocEncoding bytes, truncated to 127 bytes to match
    /// <c>StandardSecurityHandler.PasswordBytes</c>'s UTF-8 truncation. Returns <see langword="false"/>
    /// when a character has no PDFDocEncoding representation this table covers.
    /// </summary>
    internal static bool TryEncode(string? password, out byte[] bytes)
    {
        if (string.IsNullOrEmpty(password))
        {
            bytes = [];
            return true;
        }

        var buf = new byte[Math.Min(password.Length, 127)];
        for (var i = 0; i < buf.Length; i++)
        {
            if (!TryGetByte(password[i], out buf[i]))
            {
                bytes = [];
                return false;
            }
        }

        bytes = buf;
        return true;
    }

    private static bool TryGetByte(char c, out byte b)
    {
        // 0xA0 is excluded from the identity range along with the two blocks: PDFDocEncoding gives
        // it to EURO SIGN (Annex D, Latin character set table, PDF column 240 octal) where Latin-1
        // has NO-BREAK SPACE, so U+00A0 has no representation here and U+20AC is in the table below.
        //
        // The two excluded blocks are exactly the bytes Annex D gives to a glyph that Latin-1 puts
        // elsewhere: 0x18-0x1F are breve through tilde, 0x80-0x9E are bullet through zcaron. Those
        // Unicode code points are C0 and C1 controls with no PDFDocEncoding byte of their own, and
        // taking the identity there would silently encode one as an accent or a quotation mark.
        //
        // The code points Annex D marks Undefined — 0x00-0x08, 0x0B, 0x0C, 0x0E-0x17, 0x7F, 0x9F,
        // 0xAD — are NOT excluded: nothing else claims those bytes. This encoding exists to
        // reproduce the BYTES a producer hashed, and refusing a character drops the whole candidate
        // rather than substituting for it, so a document whose /U was derived from the byte 0xAD
        // would stop opening under its correct password. Byte-identity is the useful answer for
        // every one of them, 0x9F — just past the C1 block, and the one an off-by-one would take —
        // included.
        if (c <= 0xFF
            && (c is < (char)0x18 or > (char)0x1F)
            && (c is < (char)0x80 or > (char)0x9E)
            && c != (char)0xA0)
        {
            b = (byte)c;
            return true;
        }

        if (_exceptions.TryGetValue(c, out var mapped))
        {
            b = mapped;
            return true;
        }

        b = 0;
        return false;
    }

    // The 0x18-0x1F and 0x80-0x9F PDFDocEncoding code points, plus Euro at 0xA0. Checked entry by entry against the
    // PDF column of ISO 32000-1 Annex D's Latin character set table, whose codes are octal: breve
    // is 030, dagger 201, scaron 235. 36 of the 39 below were read straight out of that table and
    // matched; bullet, Zcaron and zcaron resisted extraction from the two-column layout, and they
    // fill exactly the three gaps (0x80, 0x99, 0x9E) in the otherwise contiguous run that did
    // extract.
    //
    // This is a different assignment from the CP1252-derived WinAnsiEncoding block at the same byte
    // range (compare VellumPdf.Fonts.WinAnsiEncoding, which is the write-side encoding for
    // Standard-14 text and must not be confused with this one).
    private static readonly Dictionary<char, byte> _exceptions = new()
    {
        ['˘'] = 0x18, // breve
        ['ˇ'] = 0x19, // caron
        ['ˆ'] = 0x1A, // circumflex
        ['˙'] = 0x1B, // dotaccent
        ['˝'] = 0x1C, // hungarumlaut
        ['˛'] = 0x1D, // ogonek
        ['˚'] = 0x1E, // ring
        ['˜'] = 0x1F, // small tilde
        ['•'] = 0x80, // bullet
        ['†'] = 0x81, // dagger
        ['‡'] = 0x82, // daggerdbl
        ['…'] = 0x83, // ellipsis
        ['—'] = 0x84, // emdash
        ['–'] = 0x85, // endash
        ['ƒ'] = 0x86, // florin
        ['⁄'] = 0x87, // fraction
        ['‹'] = 0x88, // guilsinglleft
        ['›'] = 0x89, // guilsinglright
        ['−'] = 0x8A, // minus
        ['‰'] = 0x8B, // perthousand
        ['„'] = 0x8C, // quotedblbase
        ['“'] = 0x8D, // quotedblleft
        ['”'] = 0x8E, // quotedblright
        ['‘'] = 0x8F, // quoteleft
        ['’'] = 0x90, // quoteright
        ['‚'] = 0x91, // quotesinglbase
        ['™'] = 0x92, // trademark
        ['ﬁ'] = 0x93, // fi
        ['ﬂ'] = 0x94, // fl
        ['Ł'] = 0x95, // Lslash
        ['Œ'] = 0x96, // OE
        ['Š'] = 0x97, // Scaron
        ['Ÿ'] = 0x98, // Ydieresis
        ['Ž'] = 0x99, // Zcaron
        ['ı'] = 0x9A, // dotlessi
        ['ł'] = 0x9B, // lslash
        ['œ'] = 0x9C, // oe
        ['š'] = 0x9D, // scaron
        ['ž'] = 0x9E, // zcaron
        ['€'] = 0xA0, // Euro — 240 octal in the table's PDF column, where WinAnsiEncoding has 0x80
    };
}
