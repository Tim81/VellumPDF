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
    private static readonly byte[] _lenIVKey = "/lenIV"u8.ToArray();
    private static readonly byte[] _fontMatrix = "/FontMatrix"u8.ToArray();

    private const ushort CharstringR = 4330;

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

    /// <summary>
    /// Returns a dictionary of glyph name to advance width (in glyph-space units) extracted by
    /// decrypting each charstring and reading the leading <c>hsbw</c> or <c>sbw</c> operator.
    /// Returns <see langword="null"/> when the program cannot be parsed. The returned widths are
    /// scaled to PDF text-space units (thousandths of a text space unit, matching <c>/Widths</c>)
    /// using the font's <c>/FontMatrix</c> first element. For a standard 1000-unit font (matrix[0] =
    /// 0.001) the scaling factor is exactly 1 and no transformation is applied.
    /// </summary>
    /// <param name="fontFile">De-segmented Type 1 font program bytes.</param>
    /// <param name="length1">The clear-text portion length (<c>/Length1</c>).</param>
    public static Dictionary<string, double>? TryGetWidths(byte[] fontFile, int length1)
    {
        var encStart = EncryptedStart(fontFile, length1);
        if (encStart < 0 || encStart >= fontFile.Length)
            return null;

        var plain = EexecDecrypt(fontFile, encStart);
        var charStrings = IndexOf(plain, _charStrings, 0);
        if (charStrings < 0)
            return null;

        // /FontMatrix [a b c d e f] is in the clear-text section (fontFile[0..length1]).
        // The first element (a) converts glyph-space advance widths to text space:
        // pdfWidth = glyphWidth * a * 1000. For a standard 1000-unit em, a = 0.001 and
        // the scale is exactly 1.0. Non-standard em sizes (e.g. 2048-unit fonts with a ≈ 0.000488)
        // require explicit scaling so the returned values match the /Widths array units.
        var clearText = length1 > 0 ? fontFile.AsSpan(0, Math.Min(length1, fontFile.Length)) : fontFile.AsSpan();
        var fontMatrixScale = ParseFontMatrixScale(clearText);

        var lenIV = ParseLenIV(plain, charStrings);
        var raw = ScanCharStringWidths(plain, charStrings + _charStrings.Length, lenIV);

        if (Math.Abs(fontMatrixScale - 1.0) < 1e-9)
            return raw; // standard 1000-unit em — no scaling needed

        // Scale each raw glyph-space width to PDF /Widths units.
        var scaled = new Dictionary<string, double>(raw.Count, StringComparer.Ordinal);
        foreach (var (name, w) in raw)
            scaled[name] = w * fontMatrixScale;
        return scaled;
    }

    // Reads the advance width from the first hsbw or sbw in a decrypted Type 1 charstring.
    // Type 1 charstring numbers: 32–246 → b-139; 247–254 → ±two-byte; 255 → four-byte literal.
    // Type 1 ops: 13=hsbw (sbx, wx), 7=seac/sbw (4-operand sbw).
    private static double? ExtractType1Width(byte[] cs, int lenIV)
    {
        if (lenIV < 0 || lenIV > cs.Length) return null;
        var stack = new double[8];
        var stackDepth = 0;
        var i = lenIV; // skip IV bytes

        while (i < cs.Length)
        {
            var b = cs[i];
            if (b >= 32 && b <= 246)
            {
                if (stackDepth < 8) stack[stackDepth] = b - 139;
                stackDepth++;
                i++;
                continue;
            }
            if (b >= 247 && b <= 250)
            {
                if (i + 1 >= cs.Length) return null;
                if (stackDepth < 8) stack[stackDepth] = (b - 247) * 256 + cs[i + 1] + 108;
                stackDepth++;
                i += 2;
                continue;
            }
            if (b >= 251 && b <= 254)
            {
                if (i + 1 >= cs.Length) return null;
                if (stackDepth < 8) stack[stackDepth] = -(b - 251) * 256 - cs[i + 1] - 108;
                stackDepth++;
                i += 2;
                continue;
            }
            if (b == 255)
            {
                if (i + 4 >= cs.Length) return null;
                if (stackDepth < 8)
                    stack[stackDepth] = (cs[i + 1] << 24) | (cs[i + 2] << 16) | (cs[i + 3] << 8) | cs[i + 4];
                stackDepth++;
                i += 5;
                continue;
            }
            if (b == 12)
            {
                // Escape: two-byte operator.
                if (i + 1 >= cs.Length) return null;
                var op2 = cs[i + 1];
                if (op2 == 7 && stackDepth >= 4)
                    return stack[2]; // sbw: stack = [sbx, sby, wx, wy] → advance width = wx
                stackDepth = 0;
                i += 2;
                continue;
            }
            // Single-byte operator
            if (b == 13 && stackDepth >= 2)
                return stack[1]; // hsbw: stack = [sbx, wx] → advance width = wx
            stackDepth = 0;
            i++;
        }
        return null;
    }

    // Parses the first element of /FontMatrix from the clear-text section and returns the scale
    // factor fontMatrixA * 1000. The default (1000-unit em, matrix[0] = 0.001) yields 1.0.
    // Returns 1.0 when the matrix cannot be parsed so widths are used unscaled (safe fallback).
    private static double ParseFontMatrixScale(ReadOnlySpan<byte> clearText)
    {
        // Convert to byte array for IndexOf; the clear-text section is short (typically < 2 KB).
        var ct = clearText.ToArray();
        var fmPos = IndexOf(ct, _fontMatrix, 0);
        if (fmPos < 0)
            return 1.0;

        // Skip past "/FontMatrix" and optional whitespace to the opening bracket "[" or "{".
        var i = fmPos + _fontMatrix.Length;
        while (i < ct.Length && IsWhite(ct[i]))
            i++;
        if (i >= ct.Length || (ct[i] != (byte)'[' && ct[i] != (byte)'{'))
            return 1.0;
        i++; // skip '[' or '{'

        // Read the first number (the 'a' element of the 6-element matrix).
        while (i < ct.Length && IsWhite(ct[i]))
            i++;
        if (i >= ct.Length)
            return 1.0;

        // Parse a decimal number (may be like 0.000488281 or 0.001).
        var numStart = i;
        if (ct[i] == (byte)'-' || ct[i] == (byte)'+')
            i++;
        while (i < ct.Length && (ct[i] >= (byte)'0' && ct[i] <= (byte)'9' || ct[i] == (byte)'.'))
            i++;
        if (i == numStart)
            return 1.0;

        var numStr = System.Text.Encoding.ASCII.GetString(ct, numStart, i - numStart);
        if (!double.TryParse(numStr, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var a))
            return 1.0;

        // a * 1000 converts glyph-space widths to PDF /Widths units.
        return a * 1000.0;
    }

    // Reads /lenIV from the Private dict in the decrypted eexec plaintext. Scans only the region before
    // the /CharStrings dict (the Private dict precedes CharStrings in every well-formed Type 1 program).
    // Returns 4 (the Type 1 spec default) if /lenIV is absent or cannot be parsed.
    private static int ParseLenIV(byte[] plain, int charStringsOffset)
    {
        // The /lenIV entry looks like: /lenIV <integer> def
        // We scan from position 0 up to the CharStrings dict for the /lenIV token.
        var pos = IndexOf(plain, _lenIVKey, 0);
        if (pos < 0 || pos >= charStringsOffset)
            return 4; // absent — use the spec default

        // Skip past the /lenIV token to the integer that follows.
        var i = SkipWhite(plain, pos + _lenIVKey.Length);
        var (value, after) = ReadSignedInt(plain, i);
        if (after < 0)
            return 4;
        return value;
    }

    // Reads an optional leading minus sign followed by decimal digits. Returns (value, afterPos)
    // or (0, -1) when no integer is found.
    private static (int Value, int After) ReadSignedInt(byte[] data, int pos)
    {
        if (pos >= data.Length)
            return (0, -1);
        var neg = data[pos] == (byte)'-';
        var start = neg ? pos + 1 : pos;
        var (abs, after) = ReadInt(data, start);
        if (after < 0)
            return (0, -1);
        return (neg ? -abs : abs, after);
    }

    // Variant of ScanCharStringNames that also decrypts each charstring and extracts advance widths.
    private static Dictionary<string, double> ScanCharStringWidths(byte[] data, int pos, int lenIV)
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
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
                break;
            var name = System.Text.Encoding.ASCII.GetString(data, nameStart, i - nameStart);

            i = SkipWhite(data, i);
            var (length, afterLen) = ReadInt(data, i);
            if (afterLen < 0)
                break;

            i = SkipWhite(data, afterLen);
            var opStart = i;
            while (i < data.Length && !IsWhite(data[i]))
                i++;
            var op = i - opStart;
            if (!(op == 2 && ((data[opStart] == 'R' && data[opStart + 1] == 'D')
                || (data[opStart] == '-' && data[opStart + 1] == '|'))))
                break;
            if (length < 0)
                break;

            i++; // skip single space after RD
            var next = i + length;
            if (next < i || next > data.Length)
                break;

            // Decrypt the charstring and extract the advance width.
            var csBytes = DecryptCharstring(data, i, length);
            var width = ExtractType1Width(csBytes, lenIV);
            if (width.HasValue)
                widths[name] = width.Value;

            pos = next;
        }
        return widths;
    }

    // Decrypts a Type 1 charstring at data[start..start+length] using the charstring cipher (R=4330).
    private static byte[] DecryptCharstring(byte[] data, int start, int length)
    {
        var r = CharstringR;
        var result = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var cipher = data[start + i];
            result[i] = (byte)(cipher ^ (r >> 8));
            r = (ushort)((cipher + r) * C1 + C2);
        }
        return result;
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
