// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Fonts;

/// <summary>
/// Caches a <see cref="PdfFontReader"/> per indirect font object, so a page that shows text with
/// the same <c>/Font</c> resource repeatedly (the common case) builds it once. Keyed on
/// <c>(objectNumber, generation)</c>; a direct font dictionary (no object number, legal per ISO
/// 32000-2 §7.8.3 though unusual) is never cached, since there is no stable key to cache it
/// under, and is rebuilt on every lookup.
/// </summary>
/// <remarks>
/// Insert-only: past <see cref="MaxCachedFonts"/> entries, a lookup still builds and returns a
/// reader, just without adding it to the cache. This is a deliberate departure from evicting the
/// least-recently-used entry: an LRU cache is more machinery than a document with more than
/// 10,000 distinct font objects (far past what any PDF in practice carries) is worth building for,
/// and the fallback costs only a rebuilt reader, not a wrong one.
/// <para>
/// Retained size at the cap, measured with <c>GC.GetTotalMemory(true)</c> before and after
/// building 10,000 fonts and keeping every one reachable: a bare <c>SimpleFontReader</c> retains
/// about 6,464 B each (about 62 MiB total); a full 10,000-entry cache built through
/// <c>PdfDocumentReader.GetFontReader</c>, which also grows the reader's own resolved-object
/// cache alongside it, retains about 7,639 B per font (about 73 MiB total).
/// <c>AdobeGlyphList.TryMapToUnicode</c> returns the mapped string for a single-component glyph
/// name directly rather than routing it through a <c>StringBuilder</c>, which accounts for most
/// of that per-font retained cost.
/// </para>
/// <para>
/// A font <c>PdfDocumentReader.GetFontReader</c> itself rejects (an unreadable font resource, or
/// a <c>/Subtype</c> naming a type this reader does not know) never reaches
/// <see cref="GetOrCreate"/> at all, so it is not cached here: that rejection's diagnostic is
/// reported again on every page that names the same font object, unlike a font whose own
/// <c>SimpleFontReader.Create</c> reported a diagnostic, which is cached like any other. Not
/// thread-safe, like every other cache the reader keeps.
/// </para>
/// </remarks>
internal sealed class FontCache
{
    internal const int MaxCachedFonts = 10_000;

    private readonly Dictionary<(int ObjectNumber, int Generation), PdfFontReader> _cache = [];

    /// <summary>Returns the cached reader for this object number and generation when one exists;
    /// otherwise builds one with <paramref name="build"/>, caching it unless the font dictionary
    /// was direct (<paramref name="objectNumber"/> is <see langword="null"/>) or the cache is
    /// already at <see cref="MaxCachedFonts"/>.</summary>
    internal PdfFontReader GetOrCreate(int? objectNumber, int? generation, Func<PdfFontReader> build)
    {
        if (objectNumber is null)
            return build();

        var key = (objectNumber.Value, generation ?? 0);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var built = build();
        if (_cache.Count < MaxCachedFonts)
            _cache[key] = built;
        return built;
    }
}
