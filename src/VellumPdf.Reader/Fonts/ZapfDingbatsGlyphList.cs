// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace VellumPdf.Reader.Fonts;

/// <summary>
/// Maps a ZapfDingbats glyph name (<c>a1</c>, <c>a2</c>, ...) to Unicode, for a Symbol-flag font
/// whose base font resolves to ZapfDingbats (see <see cref="SimpleFontReader"/> step 8). Backed by
/// the embedded <c>ZapfDingbatsGlyphList.txt</c> resource: the Adobe AGL repository's own
/// <c>zapfdingbats.txt</c> (BSD-3-Clause; see NOTICE), normalised the same way
/// <c>eng/generate-symbol-font-metrics.py</c> normalises the AFM files it reads, and committed with
/// its <c>#</c>-comment header intact.
/// </summary>
/// <remarks>
/// The file carries 201 name-to-codepoint lines, one for every <c>ZapfDingbats.afm</c> glyph name
/// except <c>space</c> (which needs no lookup: it is U+0020 under every encoding this reader
/// builds). That includes the 14 names assigned to the codes <c>SymbolFontMetrics</c>' own
/// remarks name as <c>ZapfDingbats.afm</c>-only (0x80 through 0x8D: <c>a85</c> through
/// <c>a96</c>, <c>a205</c>, <c>a206</c>). Those 14 names carry ordinary Unicode mappings in
/// Adobe's own <c>zapfdingbats.txt</c> (the ornamental-bracket block, U+2768–U+2775), which the
/// Adobe Glyph List proper does not list; omitting them here would leave those codes with no
/// Unicode route at all, which <c>SimpleFontReaderTests</c> pins directly against 0x80.
/// </remarks>
internal static class ZapfDingbatsGlyphList
{
    private static readonly Lazy<Dictionary<string, string>> _map = new(Load, isThreadSafe: true);

    /// <summary>Entry count of the loaded list: test-only visibility for pinning its size (201)
    /// directly, rather than through behaviour.</summary>
    internal static int Count => _map.Value.Count;

    /// <summary>
    /// Maps <paramref name="name"/> (a ZapfDingbats glyph name, verbatim, no <c>uniXXXX</c> or
    /// <c>_</c>-composition) to its Unicode code point. Returns <see langword="false"/> when the
    /// name is not in the list.
    /// </summary>
    public static bool TryMap(string name, out string unicode)
    {
        if (_map.Value.TryGetValue(name, out var mapped))
        {
            unicode = mapped;
            return true;
        }
        unicode = "";
        return false;
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(210, StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("ZapfDingbatsGlyphList.txt");
        if (stream is null)
            return map;

        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var semi = line.IndexOf(';');
            if (semi <= 0 || semi >= line.Length - 1)
                continue;
            if (int.TryParse(line[(semi + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var cp))
            {
                // Unguarded: ZapfDingbatsGlyphList.txt is a pinned embedded resource (NOTICE
                // records its source commit and SHA-256), never a surrogate half in practice.
                map[line[..semi]] = char.ConvertFromUtf32(cp);
            }
        }
        return map;
    }
}
