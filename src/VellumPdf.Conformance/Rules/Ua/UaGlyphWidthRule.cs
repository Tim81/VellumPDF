// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.5 testNumber 1 (PDF/UA-1: glyph width consistency). For every embedded font
/// used for rendering, the glyph width in the font dictionary (<c>/W</c> or <c>/DW</c>) shall be
/// consistent with the advance width in the embedded font program, within ±1 unit in 1000-unit
/// glyph space.
/// </summary>
/// <remarks>
/// The veraPDF predicate (object Glyph, clause 7.21.5, testNumber 1) is:
/// <c>renderingMode == 3 || widthFromFontProgram == null || widthFromDictionary == null ||
/// Math.abs(widthFromFontProgram - widthFromDictionary) &lt;= 1</c>. Translated: fires when
/// (a) the glyph is drawn visibly (Tr ≠ 3) AND (b) both widths are determinable AND (c) they
/// differ by more than 1 unit in 1000-unit PDF glyph space. This rule fires at a more conservative
/// &gt; 2 threshold to absorb a ≤1-unit divergence between its own rounding of the scaled program
/// advance and veraPDF's (see the FP-safety note at the comparison site) — never over-rejecting a
/// font veraPDF accepts, at the cost of not flagging an exact-2-unit mismatch.
/// <para>
/// Scope: only composite (Type0) fonts with /Encoding = /Identity-H or /Identity-V (a name,
/// not a CMap stream), a descendant CIDFontType2, /CIDToGIDMap absent or the name /Identity (so
/// code == CID == GID), AND an embedded TrueType program (/FontFile2 on the descendant's
/// FontDescriptor). For such a font the shown bytes are 2-byte big-endian GIDs, the program
/// advance is read from hmtx via <see cref="SfntMetrics.AdvanceWidth1000"/>, and the dictionary
/// width comes from the /W array or /DW default on the CIDFont dictionary.
/// </para>
/// <para>
/// All other font forms (simple 1-byte fonts, non-Identity encodings, stream /CIDToGIDMap, CFF)
/// are deferred — code-to-glyph resolution requires encoding/cmap tables whose exact semantics
/// in veraPDF are not fully verified; guessing would risk false positives.
/// </para>
/// <para>
/// Scope is usage-based: only fonts whose resource name appears in a <c>Tf</c> operator on the
/// page are evaluated. A font present in /Resources/Font but never selected is not checked
/// (veraPDF validates only shown glyphs — a width mismatch on an unused glyph is not a failure).
/// </para>
/// <para>
/// FP-safety invariants:
/// (1) Never fire for RenderingMode == 3 (invisible text — veraPDF Tr-3 exemption).
/// (2) Never fire for RenderingMode == -1 (unknown/indeterminate — widthFromFontProgram==null direction).
/// (3) Never fire when SfntMetrics.TryParse returns null (widthFromFontProgram==null → compliant).
/// (4) Never fire when /FontFile2 is absent (§7.21.4.1-1's job).
/// </para>
/// <para>
/// Cross-validated against veraPDF 1.30.2:
/// (a) /W removed from CIDFont so shown glyphs fall to /DW=1000 while hmtx widths differ → fires
///     clause 7.21.5-1 (19 failed checks, one per shown glyph); exit 1.
/// (b) Normal UA-1 tagged baseline with matching widths → does NOT fire 7.21.5-1; exit 0.
/// (c) /W removed on a font that is NOT selected via Tf (unused) → does NOT fire; exit 0.
/// (d) /W removed, font shown only with Tr=3 → does NOT fire (Tr-3 exemption); exit 0.
/// </para>
/// </remarks>
internal sealed class UaGlyphWidthRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.5-1";

    public string Clause => "ISO 14289-1:2014, 7.21.5";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");
    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile2 = new("FontFile2");

    public void Evaluate(PreflightContext context)
    {
        var reported = false;

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

            var inScopeFonts = new Dictionary<string, InScopeFont>(StringComparer.Ordinal);
            foreach (var entry in fontResources.Entries)
            {
                if (!usage.UsedFonts.Contains(entry.Key.Value))
                    continue;
                if (TryGetInScopeFont(context, entry.Value) is { } info)
                    inScopeFonts[entry.Key.Value] = info;
            }
            if (inScopeFonts.Count == 0)
                continue;

            foreach (var show in usage.TextShows)
            {
                if (show.FontResourceName is null || !inScopeFonts.TryGetValue(show.FontResourceName, out var font))
                    continue;

                // FP-safety: only fire on a positively-determined VISIBLE rendering mode.
                if (show.RenderingMode < 0 || show.RenderingMode == 3)
                    continue;

                var bytes = show.Bytes;
                for (var i = 0; i + 1 < bytes.Length; i += 2)
                {
                    var gid = (bytes[i] << 8) | bytes[i + 1];

                    // widthFromFontProgram == null → compliant per veraPDF predicate.
                    if (font.Metrics.AdvanceWidth1000(gid) is not { } programWidth)
                        continue;

                    var dictWidth = font.Widths.GetWidth(gid);

                    // veraPDF's tolerance is ±1, but it scales the program advance to 1000-space and
                    // rounds it with its own (Java) rounding, which can differ from this rule's
                    // Math.Round by up to 1 unit for the same advance (round-half-up vs round-half-even,
                    // or truncation). Firing at |diff| > 1 could therefore over-reject a font veraPDF
                    // accepts when its rounding lands the other side of the ±1 boundary. A ±2 margin
                    // absorbs that ≤1 rounding divergence: |diff| > 2 guarantees veraPDF's own |diff|
                    // is > 1 too, so the rule never fires on a font veraPDF accepts (the cost is
                    // under-detecting an exact-2-unit mismatch, which is FP-safe).
                    if (Math.Abs(programWidth - dictWidth) > 2)
                    {
                        context.Report(
                            RuleId,
                            Clause,
                            PreflightSeverity.Error,
                            $"The width declared in the font dictionary for glyph {gid} ({dictWidth}) "
                            + $"is not consistent with the embedded font program's advance width ({programWidth}). "
                            + "PDF/UA-1 §7.21.5 requires glyph widths to match within ±1 unit in 1000-unit space.");
                        reported = true;
                        break;
                    }
                }
                if (reported) break;
            }
        }
    }

    private static InScopeFont? TryGetInScopeFont(PreflightContext context, PdfObject? fontRef)
    {
        if (context.Resolve(fontRef) is not PdfDictionary font)
            return null;
        if ((context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value != "Type0")
            return null;
        if ((context.Resolve(font.Get(_encoding)) as PdfName)?.Value is not ("Identity-H" or "Identity-V"))
            return null;
        if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants || descendants.Count == 0)
            return null;
        if (context.Resolve(descendants[0]) is not PdfDictionary cidFont)
            return null;
        if ((context.Resolve(cidFont.Get(PdfName.Subtype)) as PdfName)?.Value != "CIDFontType2")
            return null;

        var cidToGidMapObj = context.Resolve(cidFont.Get(_cidToGidMap));
        if (cidToGidMapObj is not null
            && (cidToGidMapObj is not PdfName cidToGidMapName || cidToGidMapName.Value != "Identity"))
            return null;

        if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;
        if (descriptor.Get(_fontFile2) is not PdfIndirectReference fontFileRef)
            return null;
        if (context.ResolveStream(fontFileRef) is not { } program
            || context.DecodeStream(program) is not { } programBytes)
            return null;

        if (SfntMetrics.TryParse(programBytes) is not { } metrics)
            return null;

        return new InScopeFont(metrics, CidWidths.Parse(context, cidFont));
    }

    private sealed class InScopeFont(SfntMetrics metrics, CidWidths widths)
    {
        public SfntMetrics Metrics { get; } = metrics;
        public CidWidths Widths { get; } = widths;
    }

    // Per-CID widths from the /W array and /DW default (ISO 32000-1 §9.7.4.3).
    private sealed class CidWidths
    {
        private readonly Dictionary<int, int> _singles = new();
        private readonly List<(int First, int Last, int Width)> _ranges = [];
        private readonly int _default;

        private CidWidths(int defaultWidth) => _default = defaultWidth;

        public int GetWidth(int cid)
        {
            if (_singles.TryGetValue(cid, out var w))
                return w;
            foreach (var (first, last, width) in _ranges)
                if (cid >= first && cid <= last)
                    return width;
            return _default;
        }

        public static CidWidths Parse(PreflightContext context, PdfDictionary cidFont)
        {
            var dw = context.Resolve(cidFont.Get(new PdfName("DW"))) is PdfInteger d ? (int)d.Value : 1000;
            var result = new CidWidths(dw);
            if (context.Resolve(cidFont.Get(new PdfName("W"))) is not PdfArray w)
                return result;

            var i = 0;
            while (i < w.Count)
            {
                if (context.Resolve(w[i]) is not PdfInteger c)
                    break;
                i++;
                if (i < w.Count && context.Resolve(w[i]) is PdfArray run)
                {
                    for (var j = 0; j < run.Count; j++)
                        if (AsInt(context.Resolve(run[j])) is { } value)
                            result._singles[(int)c.Value + j] = value;
                    i++;
                }
                else if (i + 1 < w.Count
                    && AsInt(context.Resolve(w[i])) is { } last
                    && AsInt(context.Resolve(w[i + 1])) is { } rangeWidth)
                {
                    result._ranges.Add(((int)c.Value, last, rangeWidth));
                    i += 2;
                }
                else
                {
                    break;
                }
            }
            return result;
        }

        private static int? AsInt(PdfObject? obj) => obj switch
        {
            PdfInteger n => (int)n.Value,
            PdfReal r => (int)Math.Round(r.Value),
            _ => null,
        };
    }
}
