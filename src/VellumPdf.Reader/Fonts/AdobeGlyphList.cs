// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace VellumPdf.Reader.Fonts;

/// <summary>
/// Maps a glyph name to Unicode per the Adobe Glyph List (AGL) Specification, for the glyph-name
/// route of ISO 32000-2 §9.10.2. Backed by the embedded <c>AdobeGlyphList.txt</c> resource, the
/// same file <c>src/VellumPdf.Conformance/Resources/AdobeGlyphList.txt</c> ships (copied
/// byte-for-byte; see NOTICE), parsed once per process into a name-to-Unicode-string dictionary of
/// 4282 entries. 81 of those entries carry more than one code point (mostly Hebrew presentation
/// forms whose AGL name decomposes into a base letter plus a combining point), so the map's value
/// is a string, not a single <see langword="char"/>.
/// </summary>
/// <remarks>
/// This reader's own <see cref="TryMapToUnicode"/> departs from the AGL Specification's algorithm
/// in three ways, each because the two ends of the departure are indistinguishable to a caller
/// that only gets a Unicode string back:
/// <list type="bullet">
/// <item><description>Only uppercase <c>uni</c>/<c>u</c> hex digits are recognised. The AGL
/// Specification itself writes the synthetic forms in uppercase; the Conformance package's own
/// copy (<c>src/VellumPdf.Conformance/Rules/Fonts/AdobeGlyphList.cs</c>) additionally accepts
/// lowercase, which this reader does not.</description></item>
/// <item><description>A component with no mapping fails the whole name. The AGL Specification
/// maps such a component to the empty string and continues, but an empty string is
/// indistinguishable from a mapped control character once concatenated into the result, so this
/// reader treats it as no mapping instead.</description></item>
/// <item><description>A result of exactly U+0000 is also treated as no mapping. This covers both
/// <c>.notdef</c>, which the bundled list maps to U+0000, and the literal name
/// <c>uni0000</c>.</description></item>
/// </list>
/// </remarks>
internal static class AdobeGlyphList
{
    /// <summary>
    /// The longest glyph name this reader accepts. A <c>uniXXXX</c> name is <c>3 + 4k</c>
    /// characters for <c>k</c> hex groups, so 127 (31 groups) is the longest accepted and 131 the
    /// shortest rejected; an underscore-joined chain of single-character components follows the
    /// same bound per component count (64 components of one character each is 127 characters with
    /// 63 joining underscores).
    /// </summary>
    public const int MaxGlyphNameLength = 128;

    private static readonly Lazy<Dictionary<string, string>> _map = new(Load, isThreadSafe: true);

    /// <summary>Entry count of the loaded list: test-only visibility for pinning its size (4282)
    /// and the count of multi-code-point entries (81) directly, rather than through
    /// behaviour.</summary>
    internal static int Count => _map.Value.Count;

    /// <summary>
    /// Maps <paramref name="glyphName"/> to Unicode per the AGL Specification: the name is
    /// truncated at the first <c>.</c> (a production tag, e.g. <c>f.alt</c> or <c>uni0041.sc</c>),
    /// split on <c>_</c>, and each component is looked up in the list, then as <c>uniXXXX</c> (one
    /// or more 4-hex-digit groups, uppercase, each in <c>0000..D7FF</c> or <c>E000..FFFF</c>), then
    /// as <c>uXXXX</c> through <c>uXXXXXX</c> (uppercase, <c>0000..10FFFF</c> excluding
    /// surrogates). Returns <see langword="false"/> when the name is longer than
    /// <see cref="MaxGlyphNameLength"/>, any component has no mapping, any component is empty (a
    /// leading, trailing, or doubled <c>_</c>), or the mapped result is exactly U+0000.
    /// </summary>
    public static bool TryMapToUnicode(string glyphName, out string unicode)
    {
        unicode = "";
        if (glyphName.Length == 0 || glyphName.Length > MaxGlyphNameLength)
            return false;

        var dot = glyphName.IndexOf('.');
        var trimmed = dot < 0 ? glyphName : glyphName[..dot];
        if (trimmed.Length == 0)
            return false;

        var map = _map.Value;
        var result = new System.Text.StringBuilder();
        var start = 0;
        while (start <= trimmed.Length)
        {
            var underscore = trimmed.IndexOf('_', start);
            var end = underscore < 0 ? trimmed.Length : underscore;
            if (end == start)
                return false; // empty component: leading, trailing, or doubled '_'

            var component = trimmed[start..end];
            if (!TryMapComponent(map, component, out var piece))
                return false;
            result.Append(piece);

            if (underscore < 0)
                break;
            start = underscore + 1;
        }

        if (result.Length == 1 && result[0] == '\0')
            return false; // .notdef and uni0000 both resolve here; treated as unmapped.

        unicode = result.ToString();
        return true;
    }

    private static bool TryMapComponent(Dictionary<string, string> map, string component, out string piece)
    {
        if (map.TryGetValue(component, out var mapped))
        {
            piece = mapped;
            return true;
        }

        if (TryUniName(component, out var uni))
        {
            piece = uni;
            return true;
        }

        if (TryUName(component, out var cp))
        {
            piece = char.ConvertFromUtf32(cp);
            return true;
        }

        piece = "";
        return false;
    }

    private static bool TryUniName(string component, out string unicode)
    {
        unicode = "";
        // "uni" + one or more 4-hex-digit groups, each mapped independently and concatenated:
        // uni00660066 is "ff", the same result "f_f" would give through the AGL list itself.
        if (component.Length < 7 || (component.Length - 3) % 4 != 0
            || !component.StartsWith("uni", StringComparison.Ordinal))
            return false;

        var groups = (component.Length - 3) / 4;
        var sb = new System.Text.StringBuilder(groups);
        for (var g = 0; g < groups; g++)
        {
            if (!TryParseHex4(component, 3 + g * 4, out var cp))
                return false;
            if (cp is >= 0xD800 and <= 0xDFFF)
                return false; // a surrogate group is not a valid BMP scalar on its own.
            sb.Append((char)cp);
        }

        unicode = sb.ToString();
        return true;
    }

    private static bool TryUName(string component, out int codePoint)
    {
        codePoint = 0;
        if (component.Length < 5 || component.Length > 7
            || !component.StartsWith("u", StringComparison.Ordinal)
            || component.StartsWith("uni", StringComparison.Ordinal))
            return false;

        var hexLen = component.Length - 1;
        if (!TryParseHex(component, 1, hexLen, out var cp))
            return false;
        if (cp > 0x10FFFF || (cp is >= 0xD800 and <= 0xDFFF))
            return false;

        codePoint = cp;
        return true;
    }

    private static bool TryParseHex4(string s, int start, out int value) => TryParseHex(s, start, 4, out value);

    private static bool TryParseHex(string s, int start, int length, out int value)
    {
        value = 0;
        for (var i = start; i < start + length; i++)
        {
            var c = s[i];
            int digit;
            if (c is >= '0' and <= '9') digit = c - '0';
            else if (c is >= 'A' and <= 'F') digit = c - 'A' + 10;
            else return false; // lowercase a-f deliberately rejected; see this class's own remarks.
            value = (value << 4) | digit;
        }
        return true;
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(4300, StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("AdobeGlyphList.txt");
        if (stream is null)
            return map;

        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var space = line.IndexOf(' ');
            if (space <= 0 || space >= line.Length - 1)
                continue;

            var name = line[..space];
            var codes = line[(space + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder(codes.Length);
            var ok = true;
            foreach (var code in codes)
            {
                if (!int.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out var cp))
                {
                    ok = false;
                    break;
                }
                sb.Append(char.ConvertFromUtf32(cp));
            }
            if (ok)
                map[name] = sb.ToString();
        }
        return map;
    }
}
