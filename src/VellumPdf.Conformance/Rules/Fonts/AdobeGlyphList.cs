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
    private static readonly Lazy<FrozenSet<string>> _names = new(Load, isThreadSafe: true);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="glyphName"/> is a verbatim entry in the
    /// Adobe Glyph List. The comparison is case-sensitive and exact — no glyph-name algorithm
    /// processing (no <c>uniXXXX</c>, no period suffix, no underscore composition).
    /// </summary>
    public static bool Contains(string glyphName) => _names.Value.Contains(glyphName);

    private static FrozenSet<string> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("AdobeGlyphList.txt");
        if (stream is null)
            return FrozenSet<string>.Empty;

        var names = new HashSet<string>(4300, StringComparer.Ordinal);
        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var space = line.IndexOf(' ');
            if (space > 0)
                names.Add(line[..space]);
        }
        return names.ToFrozenSet(StringComparer.Ordinal);
    }
}
