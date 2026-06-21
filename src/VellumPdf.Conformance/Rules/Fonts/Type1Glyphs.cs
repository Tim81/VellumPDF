// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// A minimal, defensive reader that enumerates the glyph names defined in an embedded Type 1 font
/// program's <c>/CharStrings</c> dictionary. It decrypts the eexec-encrypted Private portion and
/// tokenises the charstring entries, returning the set of glyph names — never the charstring
/// outlines, and never throwing: any malformation yields a <see langword="null"/> result.
/// </summary>
/// <remarks>
/// Authored from Adobe's <em>Type 1 Font Format</em> (the eexec cipher, R = 55665, and the
/// <c>/name length RD &lt;binary&gt; ND</c> charstring layout). Clean-room and AOT-safe: pure byte
/// arithmetic, no reflection. Each charstring's binary is length-skipped, so bytes inside an outline
/// can never be mistaken for a following <c>/name … RD</c> entry. Only glyph names are extracted; the
/// outlines themselves are never decoded.
/// </remarks>
internal static class Type1Glyphs
{
    private const ushort EexecR = 55665;
    private const int C1 = 52845;
    private const int C2 = 22719;
    private const int EexecSkip = 4; // the eexec layer always discards the first 4 plaintext bytes.

    private static readonly byte[] _eexec = "eexec"u8.ToArray();
    private static readonly byte[] _charStrings = "/CharStrings"u8.ToArray();

    /// <summary>
    /// Returns the glyph names defined in the embedded Type 1 program's <c>/CharStrings</c>, or
    /// <see langword="null"/> when the program cannot be parsed. <paramref name="length1"/> is the
    /// font program's <c>/Length1</c> (the length of the clear-text portion); when it does not point
    /// just past the <c>eexec</c> keyword the encrypted section is located by scanning for the keyword
    /// instead.
    /// </summary>
    public static HashSet<string>? TryEnumerate(byte[] fontFile, int length1)
    {
        var encStart = EncryptedStart(fontFile, length1);
        if (encStart < 0 || encStart >= fontFile.Length)
            return null;

        var plain = EexecDecrypt(fontFile, encStart);
        var charStrings = IndexOf(plain, _charStrings, 0);
        if (charStrings < 0)
            return null;

        return ScanCharStringNames(plain, charStrings + _charStrings.Length);
    }

    // The offset of the first encrypted byte. /Length1 normally points exactly there; otherwise fall
    // back to the byte just after "eexec" and its single trailing white-space.
    private static int EncryptedStart(byte[] fontFile, int length1)
    {
        if (length1 > 0 && length1 < fontFile.Length)
            return length1;

        var e = IndexOf(fontFile, _eexec, 0);
        if (e < 0)
            return -1;
        var p = e + _eexec.Length;
        while (p < fontFile.Length && IsWhite(fontFile[p]))
            p++;
        return p;
    }

    // Decrypts fontFile[start..] with the eexec cipher and drops the 4 leading plaintext bytes.
    // Decrypting past the real encrypted section into the clear trailer yields trailing noise that the
    // charstring scanner ignores (it stops at the last well-formed entry).
    private static byte[] EexecDecrypt(byte[] fontFile, int start)
    {
        var r = EexecR;
        var n = fontFile.Length - start;
        var outBuf = new byte[n];
        for (var i = 0; i < n; i++)
        {
            var cipher = fontFile[start + i];
            outBuf[i] = (byte)(cipher ^ (r >> 8));
            r = (ushort)((cipher + r) * C1 + C2);
        }
        return n > EexecSkip ? outBuf[EexecSkip..] : [];
    }

    // From just after the "/CharStrings" keyword, reads each `/name length RD <length bytes> …` entry,
    // skipping the binary outline by its declared length. Stops at the first token that is not a
    // well-formed charstring entry (e.g. the closing "end").
    private static HashSet<string> ScanCharStringNames(byte[] data, int pos)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (pos < data.Length)
        {
            var slash = NextSlash(data, pos);
            if (slash < 0)
                break;

            var i = slash + 1;
            var nameStart = i;
            while (i < data.Length && !IsDelimiter(data[i]))
                i++;
            if (i == nameStart)
                break; // an empty name is not a charstring entry.
            var name = System.Text.Encoding.ASCII.GetString(data, nameStart, i - nameStart);

            i = SkipWhite(data, i);
            var (length, afterLen) = ReadInt(data, i);
            if (afterLen < 0)
                break; // no length follows the name — not a charstring entry (e.g. "end").

            i = SkipWhite(data, afterLen);
            var opStart = i;
            while (i < data.Length && !IsWhite(data[i]))
                i++;
            var op = i - opStart;
            // The read operator is "RD" or "-|"; anything else means we have left the dictionary.
            if (!(op == 2 && ((data[opStart] == 'R' && data[opStart + 1] == 'D')
                || (data[opStart] == '-' && data[opStart + 1] == '|'))))
                break;
            if (length < 0)
                break;

            // Exactly one space separates the operator from the binary; skip it and the outline.
            i++;
            var next = i + length;
            if (next < i || next > data.Length)
                break; // declared length overruns the buffer — stop rather than misread.

            names.Add(name);
            pos = next;
        }
        return names;
    }

    private static int NextSlash(byte[] data, int pos)
    {
        for (var i = pos; i < data.Length; i++)
            if (data[i] == (byte)'/')
                return i;
        return -1;
    }

    private static (int Value, int After) ReadInt(byte[] data, int pos)
    {
        var i = pos;
        var value = 0;
        while (i < data.Length && data[i] >= (byte)'0' && data[i] <= (byte)'9')
        {
            value = value * 10 + (data[i] - '0');
            i++;
        }
        return i == pos ? (0, -1) : (value, i);
    }

    private static int SkipWhite(byte[] data, int pos)
    {
        while (pos < data.Length && IsWhite(data[pos]))
            pos++;
        return pos;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            if (match)
                return i;
        }
        return -1;
    }

    private static bool IsWhite(byte b) => b is 0x20 or 0x09 or 0x0A or 0x0D or 0x0C or 0x00;

    // A PostScript token delimiter: white-space or one of ()<>[]{}/% — anything that ends a name.
    private static bool IsDelimiter(byte b)
        => IsWhite(b) || b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
            or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
