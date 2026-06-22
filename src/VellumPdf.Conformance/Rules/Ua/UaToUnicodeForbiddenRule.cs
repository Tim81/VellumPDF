// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.7 testNumber 2 (PDF/UA-1: forbidden ToUnicode values for shown glyphs).
/// For each glyph actually shown, its ToUnicode mapping shall not be U+0000, U+FEFF, or U+FFFE.
/// </summary>
/// <remarks>
/// The veraPDF predicate (object Glyph, clause 7.21.7, testNumber 2) is:
/// <c>toUnicode == null || (no U+0000, U+FEFF, U+FFFE in toUnicode)</c>. A null mapping (no
/// /ToUnicode stream, or the glyph's code is not mapped) is COMPLIANT — only an explicit bad value
/// fires this rule.
/// <para>
/// CRITICAL INVARIANT: only check codes that are ACTUALLY SHOWN in the document's content streams.
/// A /ToUnicode stream may contain bfchar/bfrange entries for codes that are never shown; veraPDF
/// does NOT flag those entries. Checking the whole CMap was the root cause of the previous false
/// positive that prompted the rule to be reverted (git ab5dc76). This implementation only evaluates
/// codes present in <see cref="TextShow.Bytes"/> records from <see cref="ContentStreamUsage"/>.
/// </para>
/// <para>
/// Scope: same as <see cref="UaNotdefGlyphRule"/> — composite (Type0) fonts with /Encoding =
/// /Identity-H or /Identity-V, descendant CIDFontType2, /CIDToGIDMap absent or /Identity.
/// Shown bytes are split as 2-byte big-endian codes. All rendering modes are checked (no Tr
/// exemption in the veraPDF predicate).
/// </para>
/// <para>
/// On any parse failure the rule silently skips (no finding) to stay FP-safe.
/// </para>
/// <para>
/// Cross-validated against veraPDF 1.30.2:
/// (a) ToUnicode maps a SHOWN code to U+0000 → veraPDF fires 7.21.7-2 (exit 1);
/// (b) ToUnicode maps a SHOWN code to a normal value → veraPDF does NOT fire 7.21.7-2 (exit 0);
/// (c) ToUnicode maps an UNUSED code to U+0000, only good codes shown → veraPDF does NOT fire
///     7.21.7-2 (exit 0) — the critical regression guard for the prior FP.
/// </para>
/// </remarks>
internal sealed class UaToUnicodeForbiddenRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.7-2";

    public string Clause => "ISO 14289-1:2014, 7.21.7";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");
    private static readonly PdfName _toUnicode = new("ToUnicode");

    public void Evaluate(PreflightContext context)
    {
        var reported = false; // fire at most once per document

        foreach (var page in context.EnumeratePages())
        {
            if (reported) break;

            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fontResources)
                continue;

            var usage = ContentStreamUsage.Analyze(context, page);
            if (usage.TextShows.Count == 0)
                continue;

            // Build map: resource name → parsed ToUnicode lookup (only for in-scope fonts that have
            // a /ToUnicode stream; skip fonts without /ToUnicode — null mapping is compliant).
            var codeToUnicode = new Dictionary<string, Dictionary<int, int[]>>(StringComparer.Ordinal);
            foreach (var entry in fontResources.Entries)
            {
                var name = entry.Key.Value;
                if (!usage.UsedFonts.Contains(name))
                    continue;
                if (!IsInScopeFont(context, entry.Value, out var type0Font))
                    continue;
                if (type0Font is null)
                    continue;
                // Attempt to get and parse the /ToUnicode stream.
                var toUnicodeStream = context.ResolveStream(type0Font.Get(_toUnicode));
                if (toUnicodeStream is null)
                    continue; // no ToUnicode — compliant (null mapping is fine)
                var streamBytes = context.DecodeStream(toUnicodeStream);
                if (streamBytes is null)
                    continue; // can't decode — skip (FP-safe)
                var map = ParseToUnicodeCMap(streamBytes);
                if (map is not null)
                    codeToUnicode[name] = map;
            }
            if (codeToUnicode.Count == 0)
                continue;

            // Check only the SHOWN codes.
            foreach (var show in usage.TextShows)
            {
                if (reported) break;
                if (show.FontResourceName is null)
                    continue;
                if (!codeToUnicode.TryGetValue(show.FontResourceName, out var map))
                    continue;

                var bytes = show.Bytes;
                for (var i = 0; i + 1 < bytes.Length; i += 2)
                {
                    var code = (bytes[i] << 8) | bytes[i + 1];
                    if (!map.TryGetValue(code, out var unicodeValues))
                        continue; // not mapped — compliant (null is fine)
                    foreach (var uv in unicodeValues)
                    {
                        if (uv == 0x0000 || uv == 0xFEFF || uv == 0xFFFE)
                        {
                            context.Report(
                                RuleId,
                                Clause,
                                PreflightSeverity.Error,
                                $"A shown glyph (code 0x{code:X4}) in a composite font maps to a forbidden "
                                + $"Unicode value (U+{uv:X4}) in the /ToUnicode CMap. PDF/UA-1 §7.21.7 "
                                + "prohibits U+0000, U+FEFF, and U+FFFE as ToUnicode values for shown glyphs.");
                            reported = true;
                            break;
                        }
                    }
                    if (reported) break;
                }
            }
        }
    }

    // Returns true when fontRef resolves to an in-scope Type0 font (Identity-H/V + CIDFontType2 +
    // Identity/absent CIDToGIDMap). When true, type0Font is set to the resolved Type0 font dict
    // (for /ToUnicode lookup).
    private static bool IsInScopeFont(PreflightContext context, PdfObject? fontRef, out PdfDictionary? type0Font)
    {
        type0Font = null;
        if (context.Resolve(fontRef) is not PdfDictionary font)
            return false;
        if ((context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value != "Type0")
            return false;
        if ((context.Resolve(font.Get(_encoding)) as PdfName)?.Value is not ("Identity-H" or "Identity-V"))
            return false;
        if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants || descendants.Count == 0)
            return false;
        if (context.Resolve(descendants[0]) is not PdfDictionary cidFont)
            return false;
        if ((context.Resolve(cidFont.Get(PdfName.Subtype)) as PdfName)?.Value != "CIDFontType2")
            return false;

        // /CIDToGIDMap must be absent or the name /Identity.
        var cidToGidMapObj = context.Resolve(cidFont.Get(_cidToGidMap));
        if (cidToGidMapObj is not null
            && (cidToGidMapObj is not PdfName cidToGidMapName
                || cidToGidMapName.Value != "Identity"))
            return false;

        type0Font = font;
        return true;
    }

    /// <summary>
    /// Parses a ToUnicode CMap stream and returns a code → Unicode codepoint(s) lookup.
    /// Handles <c>beginbfchar</c>/<c>endbfchar</c> single-entry pairs and
    /// <c>beginbfrange</c>/<c>endbfrange</c> ranges with either a single UTF-16BE destination
    /// or an array of per-code destination values. Source codes are 2-byte hex (e.g. &lt;0041&gt;).
    /// Returns null on any parse failure (caller must not fire a finding when null).
    /// </summary>
    private static Dictionary<int, int[]>? ParseToUnicodeCMap(byte[] bytes)
    {
        try
        {
            var text = Encoding.Latin1.GetString(bytes);
            var map = new Dictionary<int, int[]>();

            // Parse beginbfchar / endbfchar sections.
            var pos = 0;
            while (pos < text.Length)
            {
                var bfcharIdx = text.IndexOf("beginbfchar", pos, StringComparison.Ordinal);
                if (bfcharIdx < 0) break;
                var endbfcharIdx = text.IndexOf("endbfchar", bfcharIdx, StringComparison.Ordinal);
                if (endbfcharIdx < 0) break;
                var section = text.AsSpan(bfcharIdx + "beginbfchar".Length, endbfcharIdx - bfcharIdx - "beginbfchar".Length);
                ParseBfchar(section, map);
                pos = endbfcharIdx + "endbfchar".Length;
            }

            // Parse beginbfrange / endbfrange sections.
            pos = 0;
            while (pos < text.Length)
            {
                var bfrangeIdx = text.IndexOf("beginbfrange", pos, StringComparison.Ordinal);
                if (bfrangeIdx < 0) break;
                var endbfrangeIdx = text.IndexOf("endbfrange", bfrangeIdx, StringComparison.Ordinal);
                if (endbfrangeIdx < 0) break;
                var section = text.AsSpan(bfrangeIdx + "beginbfrange".Length, endbfrangeIdx - bfrangeIdx - "beginbfrange".Length);
                ParseBfrange(section, map);
                pos = endbfrangeIdx + "endbfrange".Length;
            }

            return map;
        }
        catch
        {
            return null; // parse failure → no finding (FP-safe)
        }
    }

    // Parses bfchar entries: <srcCode> <dstCode>
    // srcCode and dstCode are 2-byte hex strings: <XXXX>
    private static void ParseBfchar(ReadOnlySpan<char> section, Dictionary<int, int[]> map)
    {
        var text = section.ToString();
        var pos = 0;
        while (pos < text.Length)
        {
            if (!TryReadHexString(text, ref pos, out var src)) break;
            if (!TryReadHexDestination(text, ref pos, out var dst)) break;
            if (src >= 0 && dst is not null)
                map[src] = dst;
        }
    }

    // Parses bfrange entries: <srcLo> <srcHi> <dstStart>  OR  <srcLo> <srcHi> [<dst0> <dst1> ...]
    private static void ParseBfrange(ReadOnlySpan<char> section, Dictionary<int, int[]> map)
    {
        var text = section.ToString();
        var pos = 0;
        while (pos < text.Length)
        {
            if (!TryReadHexString(text, ref pos, out var srcLo)) break;
            if (!TryReadHexString(text, ref pos, out var srcHi)) break;
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) break;

            if (text[pos] == '[')
            {
                // Array form: each code in [srcLo..srcHi] has its own destination.
                pos++; // skip '['
                for (var code = srcLo; code <= srcHi; code++)
                {
                    if (!TryReadHexDestination(text, ref pos, out var dst)) break;
                    if (dst is not null)
                        map[code] = dst;
                }
                SkipWhitespace(text, ref pos);
                if (pos < text.Length && text[pos] == ']') pos++;
            }
            else
            {
                // Single destination: dst code for srcLo; subsequent codes increment the BMP codepoint.
                if (!TryReadHexString(text, ref pos, out var dstBase)) break;
                for (var code = srcLo; code <= srcHi; code++)
                {
                    var uv = dstBase + (code - srcLo);
                    map[code] = [uv];
                }
            }
        }
    }

    // Reads a <XXXX> or <XXXXXXXX> hex string from position pos, advancing pos past it.
    // Returns the integer value, or -1 on failure. Only handles 2-byte (4 hex digit) or
    // 4-byte (8 hex digit) forms for destination; source codes are always 2-byte.
    private static bool TryReadHexString(string text, ref int pos, out int value)
    {
        value = -1;
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length || text[pos] != '<') return false;
        var start = pos + 1;
        var end = text.IndexOf('>', start);
        if (end < 0) return false;
        var hex = text.AsSpan(start, end - start).ToString().Trim();
        // For source codes we expect 4 hex digits (2 bytes); ignore others — return false.
        if (hex.Length != 4) { pos = end + 1; value = -1; return false; }
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out value))
        {
            pos = end + 1;
            return false;
        }
        pos = end + 1;
        return true;
    }

    // Reads a destination hex string that may be 2-byte or 4-byte (UTF-16BE pair).
    // Returns an array of Unicode codepoints. Returns null (and advances pos) on failure.
    private static bool TryReadHexDestination(string text, ref int pos, out int[]? value)
    {
        value = null;
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length || text[pos] != '<') return false;
        var start = pos + 1;
        var end = text.IndexOf('>', start);
        if (end < 0) return false;
        var hex = text.AsSpan(start, end - start).ToString().Trim();
        pos = end + 1;

        // 4 hex digits = 1 BMP codepoint (2 bytes)
        if (hex.Length == 4)
        {
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var uv))
                return true; // parse failure → skip (null value returned)
            value = [uv];
            return true;
        }
        // 8 hex digits = UTF-16BE surrogate pair → one supplementary codepoint
        if (hex.Length == 8)
        {
            if (!int.TryParse(hex[..4], System.Globalization.NumberStyles.HexNumber, null, out var hi))
                return true;
            if (!int.TryParse(hex[4..], System.Globalization.NumberStyles.HexNumber, null, out var lo))
                return true;
            // UTF-16BE decode: surrogates D800–DFFF
            if (hi >= 0xD800 && hi <= 0xDBFF && lo >= 0xDC00 && lo <= 0xDFFF)
            {
                var cp = 0x10000 + ((hi - 0xD800) << 10) + (lo - 0xDC00);
                value = [cp];
            }
            else
            {
                // Two separate BMP codepoints (unusual but valid in CMap)
                value = [hi, lo];
            }
            return true;
        }
        // Other lengths: skip (not a standard size — return null, no finding)
        return true;
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\r'
                || text[pos] == '\n' || text[pos] == '\f'))
            pos++;
    }
}
