// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using System.IO;
using System.Reflection;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// Provides a set-membership test against the Adobe Glyph List (AGL), for use in §7.21.6-2
/// <c>differencesAreUnicodeCompliant</c> checking. The AGL is the copy bundled with veraPDF 1.30.2
/// (<c>font/AdobeGlyphList.txt</c>), which is identical to the Adobe master except for the added
/// <c>.notdef 0000</c> entry. Lookup is a verbatim name match — no <c>uniXXXX</c> synthesis, no
/// period-suffix stripping — matching veraPDF's own resolution behaviour.
/// </summary>
/// <remarks>
/// Data source: Adobe Glyph List, BSD-3-Clause. See the NOTICE file for attribution.
/// AOT-safe: no reflection at call time; the set is loaded once from a manifest resource stream.
/// </remarks>
internal static class AdobeGlyphList
{
    private static readonly Lazy<FrozenSet<string>> _names = new(LoadNames, isThreadSafe: true);

    private static readonly Lazy<FrozenDictionary<string, int>> _codepoints =
        new(LoadCodepoints, isThreadSafe: true);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="glyphName"/> is a verbatim entry in the
    /// Adobe Glyph List. The comparison is case-sensitive and exact — no glyph-name algorithm
    /// processing (no <c>uniXXXX</c>, no period suffix, no underscore composition).
    /// </summary>
    public static bool Contains(string glyphName) => _names.Value.Contains(glyphName);

    /// <summary>
    /// Resolves a glyph name to a Unicode code point. Handles:
    /// <list type="bullet">
    ///   <item>AGL verbatim entries (e.g. <c>A</c> → U+0041).</item>
    ///   <item><c>uniXXXX</c> synthetic names (exactly 4 upper-hex digits after <c>uni</c>).</item>
    ///   <item><c>uXXXXXX</c> synthetic names (4–6 upper-hex digits after <c>u</c>).</item>
    /// </list>
    /// Returns <see langword="false"/> when the name cannot be resolved (no mapping, malformed
    /// synthetic name, or a surrogate / non-Unicode code point).
    /// </summary>
    public static bool TryGetCodepoint(string name, out int codePoint)
    {
        if (_codepoints.Value.TryGetValue(name, out codePoint))
            return true;

        // uniXXXX: exactly "uni" + 4 upper-hex digits → BMP scalar
        if (name.Length == 7 && name.StartsWith("uni", StringComparison.Ordinal))
        {
            if (TryParseHex(name, 3, 4, out var cp) && cp is >= 0 and <= 0xFFFF and not (>= 0xD800 and <= 0xDFFF))
            {
                codePoint = cp;
                return true;
            }
        }

        // uXXXXXX: "u" + 4–6 upper-hex digits → full Unicode scalar
        if (name.Length >= 5 && name.Length <= 7 && name.StartsWith("u", StringComparison.Ordinal)
            && !name.StartsWith("uni", StringComparison.Ordinal))
        {
            var hexLen = name.Length - 1;
            if (hexLen >= 4 && TryParseHex(name, 1, hexLen, out var cp)
                && cp is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
            {
                codePoint = cp;
                return true;
            }
        }

        codePoint = 0;
        return false;
    }

    private static bool TryParseHex(string s, int start, int length, out int value)
    {
        value = 0;
        for (var i = start; i < start + length; i++)
        {
            var c = s[i];
            int digit;
            if (c >= '0' && c <= '9') digit = c - '0';
            else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
            else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
            else return false;
            value = (value << 4) | digit;
        }
        return true;
    }

    private static FrozenSet<string> LoadNames()
    {
        var names = new HashSet<string>(4300, StringComparer.Ordinal);
        LoadResource((name, _) => names.Add(name));
        return names.ToFrozenSet(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, int> LoadCodepoints()
    {
        var map = new Dictionary<string, int>(4300, StringComparer.Ordinal);
        LoadResource((name, hex) =>
        {
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var cp))
                map[name] = cp;
        });
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void LoadResource(Action<string, string> onEntry)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("AdobeGlyphList.txt");
        if (stream is null)
            return;

        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var space = line.IndexOf(' ');
            if (space > 0 && space < line.Length - 1)
                onEntry(line[..space], line[(space + 1)..]);
        }
    }
}
