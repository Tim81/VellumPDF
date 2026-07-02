// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.7 testNumber 1 (PDF/UA-1): for every character code actually used to render
/// text, the font shall define a mapping from that code to a Unicode value. This does NOT require a
/// <c>/ToUnicode</c> stream in every case — the Unicode value is a <em>derived</em> property. For a
/// standard-encoded simple font the value is derived from the code's glyph name (via the font
/// <c>/Encoding</c>) resolved through the Adobe Glyph List, so no <c>/ToUnicode</c> is needed.
/// </summary>
/// <remarks>
/// <para>
/// The veraPDF predicate (object Glyph, clause 7.21.7, testNumber 1) is
/// <c>toUnicode != null</c> where <c>toUnicode</c> is a <em>computed</em> attribute: veraPDF first
/// tries the font's <c>/ToUnicode</c> CMap for the code; failing that, for a simple font it resolves
/// the code's glyph name (base encoding + <c>/Differences</c>) and looks it up in the Adobe Glyph
/// List (including the algorithmic <c>uniXXXX</c> / <c>uXXXXXX</c> forms). A naive
/// <c>/ToUnicode != null</c> check therefore over-rejects perfectly conformant WinAnsi/MacRoman
/// simple fonts. This rule models the derivation so it only fires when a used code has NO Unicode
/// value by ANY route.
/// </para>
/// <para>
/// A used code's Unicode value is considered "available" when ANY of the following holds:
/// <list type="number">
///   <item>the font's <c>/ToUnicode</c> CMap maps that code, OR</item>
///   <item>(simple fonts) the code resolves through the font <c>/Encoding</c> (a base encoding name
///   plus any <c>/Differences</c>) to a glyph name that the Adobe Glyph List resolves to a Unicode
///   scalar — this also covers the algorithmic <c>uniXXXX</c> / <c>uXXXXXX</c> glyph-name forms via
///   <see cref="AdobeGlyphList.TryGetCodepoint"/>.</item>
/// </list>
/// The rule fires §7.21.7-1 only when a used code has none of these (e.g. a simple font whose
/// <c>/Differences</c> maps a shown code to a custom name such as <c>g17</c> that is not in the AGL,
/// and there is no <c>/ToUnicode</c> stream).
/// </para>
/// <para>
/// Scope is deliberately narrow to stay false-positive-safe:
/// <list type="bullet">
///   <item>Only simple fonts (<c>/Subtype /Type1</c> or <c>/Subtype /TrueType</c>) actually selected
///   by a <c>Tf</c> operator and used to show text are evaluated. Composite (Type0) fonts are
///   SKIPPED — veraPDF derives their Unicode from the CID system / embedded CMap, which is not
///   modelled here; under-detecting is preferred over a false positive.</item>
///   <item>A simple font with NO <c>/Encoding</c> entry is skipped: its built-in program encoding may
///   still let veraPDF derive Unicode, which cannot be reproduced confidently here.</item>
///   <item>A used code whose encoding leaves the glyph-name slot undefined (null) or maps it to
///   <c>.notdef</c> is skipped (no confident glyph name → cannot positively conclude "no Unicode").</item>
///   <item>On any decode/parse failure the code is skipped (no finding).</item>
/// </list>
/// Fires at most once per document.
/// </para>
/// </remarks>
internal sealed class UaToUnicodeCharMappingRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.7-1";

    public string Clause => "ISO 14289-1:2014, 7.21.7";

    private static readonly PdfName _encoding = new("Encoding");
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

            // Build, for each in-scope simple font actually used on this page, the data needed to
            // decide per code whether a Unicode value is available.
            var models = new Dictionary<string, SimpleFontModel>(StringComparer.Ordinal);
            foreach (var entry in fontResources.Entries)
            {
                var name = entry.Key.Value;
                if (!usage.UsedFonts.Contains(name))
                    continue;
                if (TryBuildModel(context, entry.Value) is { } model)
                    models[name] = model;
            }
            if (models.Count == 0)
                continue;

            foreach (var show in usage.TextShows)
            {
                if (reported) break;
                if (show.FontResourceName is null)
                    continue;
                if (!models.TryGetValue(show.FontResourceName, out var model))
                    continue;

                foreach (var b in show.Bytes)
                {
                    var code = b; // simple font: one byte == one character code
                    if (model.HasUnicode(code))
                        continue;
                    if (!model.CanConcludeMissing(code))
                        continue; // no confident glyph name for this code → skip (FP-safe)

                    context.Report(
                        RuleId,
                        Clause,
                        PreflightSeverity.Error,
                        $"A character code (0x{code:X2}) used to render text with a simple font has no "
                        + "Unicode value: its glyph name is not in the Adobe Glyph List and the font has "
                        + "no /ToUnicode entry that maps the code. PDF/UA-1 §7.21.7 requires every used "
                        + "character code to map to a Unicode value.");
                    reported = true;
                    break;
                }
            }
        }
    }

    // Builds a model for an in-scope simple font, or null when the font is out of scope / not
    // confidently analysable (composite font, no /Encoding, unreadable ToUnicode structure, …).
    private static SimpleFontModel? TryBuildModel(PreflightContext context, PdfObject? fontRef)
    {
        if (context.Resolve(fontRef) is not PdfDictionary font)
            return null;

        // Simple fonts only. Type0 (composite) is skipped — Unicode derivation for CIDs is not modelled.
        var subtype = (context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value;
        if (subtype is not ("Type1" or "TrueType"))
            return null;

        // Resolve the /Encoding to a 256-slot glyph-name map. No /Encoding (or an unresolvable one)
        // → skip: the program's built-in encoding may still yield Unicode via veraPDF, unmodelled here.
        var glyphNames = SimpleFontEncoding.Resolve(context, context.Resolve(font.Get(_encoding)));
        if (glyphNames is null)
            return null;

        // Parse the /ToUnicode CMap if present (only the set of mapped codes is needed here).
        HashSet<int>? mappedCodes = null;
        if (context.ResolveStream(font.Get(_toUnicode)) is { } toUnicodeStream
            && context.DecodeStream(toUnicodeStream) is { } toUnicodeBytes)
        {
            mappedCodes = ParseToUnicodeMappedCodes(toUnicodeBytes);
        }

        return new SimpleFontModel(glyphNames, mappedCodes);
    }

    // Parses a ToUnicode CMap and returns the set of source codes it maps (values are irrelevant for
    // §7.21.7-1 — only presence matters). Handles beginbfchar/endbfchar single pairs and
    // beginbfrange/endbfrange ranges (single destination or array). Returns an empty set on failure
    // (FP-safe: an unparseable CMap contributes no "mapped" codes, so the rule falls back to the AGL
    // route, which is the conservative direction).
    private static HashSet<int> ParseToUnicodeMappedCodes(byte[] bytes)
    {
        var codes = new HashSet<int>();
        try
        {
            var text = Encoding.Latin1.GetString(bytes);

            var pos = 0;
            while (pos < text.Length)
            {
                var idx = text.IndexOf("beginbfchar", pos, StringComparison.Ordinal);
                if (idx < 0) break;
                var end = text.IndexOf("endbfchar", idx, StringComparison.Ordinal);
                if (end < 0) break;
                var section = text.Substring(idx + "beginbfchar".Length, end - idx - "beginbfchar".Length);
                ParseBfcharCodes(section, codes);
                pos = end + "endbfchar".Length;
            }

            pos = 0;
            while (pos < text.Length)
            {
                var idx = text.IndexOf("beginbfrange", pos, StringComparison.Ordinal);
                if (idx < 0) break;
                var end = text.IndexOf("endbfrange", idx, StringComparison.Ordinal);
                if (end < 0) break;
                var section = text.Substring(idx + "beginbfrange".Length, end - idx - "beginbfrange".Length);
                ParseBfrangeCodes(section, codes);
                pos = end + "endbfrange".Length;
            }
        }
        catch
        {
            // Parse failure — return whatever was collected (FP-safe: fewer mapped codes only makes
            // the AGL route the deciding factor).
        }
        return codes;
    }

    // bfchar entries: <srcCode> <dstCode>. Records each srcCode.
    private static void ParseBfcharCodes(string text, HashSet<int> codes)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            if (!TryReadHex(text, ref pos, out var src)) break;
            if (!SkipHex(text, ref pos)) break; // consume the destination token
            if (src >= 0)
                codes.Add(src);
        }
    }

    // bfrange entries: <srcLo> <srcHi> <dstStart>  OR  <srcLo> <srcHi> [<dst0> <dst1> ...].
    // Records every code in [srcLo, srcHi].
    private static void ParseBfrangeCodes(string text, HashSet<int> codes)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            if (!TryReadHex(text, ref pos, out var srcLo)) break;
            if (!TryReadHex(text, ref pos, out var srcHi)) break;
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) break;

            if (text[pos] == '[')
            {
                pos++; // skip '['
                for (var code = srcLo; code <= srcHi; code++)
                {
                    if (!SkipHex(text, ref pos)) break;
                    codes.Add(code);
                }
                SkipWhitespace(text, ref pos);
                if (pos < text.Length && text[pos] == ']') pos++;
            }
            else
            {
                if (!SkipHex(text, ref pos)) break; // single destination base
                for (var code = srcLo; code <= srcHi; code++)
                    codes.Add(code);
            }
        }
    }

    // Reads a <XX> or <XXXX> hex token (an even-length source code, 2 or more digits). Returns the
    // value, or false on any other form so range/array parsing stays aligned. Simple (1-byte) fonts
    // commonly emit 2-digit source codes; requiring exactly 4 was a false-positive source.
    private static bool TryReadHex(string text, ref int pos, out int value)
    {
        value = -1;
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length || text[pos] != '<') return false;
        var start = pos + 1;
        var close = text.IndexOf('>', start);
        if (close < 0) return false;
        var hex = text.Substring(start, close - start).Trim();
        pos = close + 1;
        if (hex.Length < 2 || hex.Length % 2 != 0) { value = -1; return false; }
        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    // Skips one <...> hex token of any length (a destination). Returns false when no token is present.
    private static bool SkipHex(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length || text[pos] != '<') return false;
        var close = text.IndexOf('>', pos + 1);
        if (close < 0) return false;
        pos = close + 1;
        return true;
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\r'
                || text[pos] == '\n' || text[pos] == '\f'))
            pos++;
    }

    // Per-font decision model for one simple font.
    private sealed class SimpleFontModel(string?[] glyphNames, HashSet<int>? mappedToUnicodeCodes)
    {
        private readonly string?[] _glyphNames = glyphNames;
        private readonly HashSet<int>? _mappedToUnicodeCodes = mappedToUnicodeCodes;

        // True when a Unicode value is available for the code by ToUnicode or by AGL glyph-name resolution.
        public bool HasUnicode(int code)
        {
            if (_mappedToUnicodeCodes is not null && _mappedToUnicodeCodes.Contains(code))
                return true;

            var glyphName = code >= 0 && code < _glyphNames.Length ? _glyphNames[code] : null;
            if (glyphName is null)
                return false;
            return AdobeGlyphList.TryGetCodepoint(glyphName, out _);
        }

        // True only when we have a confident glyph name for the code that is NOT AGL-resolvable — the
        // only situation in which "no Unicode value" can be positively concluded. When the code has no
        // glyph name (null slot) or maps to .notdef we cannot conclude a violation (FP-safe skip).
        public bool CanConcludeMissing(int code)
        {
            var glyphName = code >= 0 && code < _glyphNames.Length ? _glyphNames[code] : null;
            return glyphName is not null && glyphName != ".notdef";
        }
    }
}
