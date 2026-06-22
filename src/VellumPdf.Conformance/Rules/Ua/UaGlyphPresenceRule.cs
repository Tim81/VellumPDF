// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.4.1 testNumber 2 (PDF/UA-1: glyph presence — every shown glyph must be
/// present in the embedded font program). A glyph referenced in a visible text rendering mode
/// (Tr 0–8 except 3) must have a corresponding entry in the embedded TrueType program; a glyph
/// drawn only in Tr 3 (invisible) is exempt by the veraPDF predicate.
/// </summary>
/// <remarks>
/// The veraPDF predicate (object Glyph, clause 7.21.4.1, testNumber 2) is:
/// <c>renderingMode == 3 || isGlyphPresent == null || isGlyphPresent == true</c>. Translated:
/// only a glyph that is (a) drawn visibly (Tr ≠ 3) AND (b) whose presence is
/// <em>positively determined</em> to be false fires. Glyphs where presence can't be determined
/// (<c>isGlyphPresent == null</c>) are compliant — this maps to the "SfntMetrics returned null"
/// case below (parse failure → don't fire).
/// <para>
/// Scope: only composite (Type0) fonts with /Encoding = /Identity-H or /Identity-V (a name,
/// not a CMap stream), a descendant CIDFontType2, /CIDToGIDMap absent or the name /Identity,
/// AND an embedded TrueType program (/FontFile2 on the descendant's FontDescriptor). For such a
/// font, the shown bytes are 2-byte big-endian codes, and code == CID == GID. A GID is
/// "present" iff GID &lt; numGlyphs of the embedded TrueType program. If the program cannot be
/// parsed (SfntMetrics returns null) the rule does not fire (isGlyphPresent == null → compliant).
/// </para>
/// <para>
/// If /FontFile2 is absent (font not embedded) this clause does NOT apply — embedding itself
/// is §7.21.4.1-1's job (UaFontEmbeddingRule). Only fonts with a /FontFile2 program are checked.
/// </para>
/// <para>
/// All other font forms (simple 1-byte fonts, non-Identity encodings, stream /CIDToGIDMap, CFF)
/// are deferred — code-to-glyph resolution requires encoding/cmap tables we do not have; guessing
/// would risk false positives.
/// </para>
/// <para>
/// Scope is usage-based: only fonts whose resource name appears in a Tf operator on the page are
/// evaluated. A font present in /Resources/Font but never selected is not checked.
/// </para>
/// <para>
/// FP-safety invariants:
/// (1) Never fire for RenderingMode == 3 (invisible text — veraPDF Tr-3 exemption).
/// (2) Never fire for RenderingMode == -1 (unknown/indeterminate — treat as "can't determine").
/// (3) Never fire when SfntMetrics.TryParse returns null (program unreadable → isGlyphPresent == null).
/// (4) Never fire when /FontFile2 is absent (§7.21.4.1-1's job).
/// </para>
/// <para>
/// Cross-validated against veraPDF 1.30.2:
/// (a) draws GID 0xEA60 (60000) with Identity-H CIDFontType2 using Tr 0 → veraPDF fires
///     clause 7.21.4.1-2 (exit 1);
/// (b) draws GID 0xEA60 with the same font using Tr 3 (invisible) → veraPDF does NOT fire
///     7.21.4.1-2 (Tr-3 exemption confirmed);
/// (c) the normal UA-1 tagged baseline (in-range glyphs, visible) → veraPDF does NOT fire
///     7.21.4.1-2 (exit 0).
/// </para>
/// </remarks>
internal sealed class UaGlyphPresenceRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.4.1-2";

    public string Clause => "ISO 14289-1:2014, 7.21.4.1";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");
    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile2 = new("FontFile2");

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

            // Build a map: resource name → (programObjectNumber, SfntMetrics) for each in-scope font.
            // In-scope = Identity-H/V CIDFontType2 with an embedded /FontFile2 whose program parses.
            var inScopeFonts = new Dictionary<string, (int ProgramObj, SfntMetrics Metrics)>(StringComparer.Ordinal);
            foreach (var entry in fontResources.Entries)
            {
                if (!usage.UsedFonts.Contains(entry.Key.Value))
                    continue;
                if (TryGetInScopeFont(context, entry.Value) is { } info)
                    inScopeFonts[entry.Key.Value] = info;
            }
            if (inScopeFonts.Count == 0)
                continue;

            // Check every TextShow for this page whose font resource is in-scope.
            foreach (var show in usage.TextShows)
            {
                if (show.FontResourceName is null || !inScopeFonts.TryGetValue(show.FontResourceName, out var font))
                    continue;

                // FP-safety: only fire on a positively-determined VISIBLE rendering mode.
                // RenderingMode -1 = unknown/indeterminate → skip (isGlyphPresent==null direction).
                // RenderingMode 3 = invisible → the veraPDF Tr-3 exemption.
                if (show.RenderingMode < 0 || show.RenderingMode == 3)
                    continue;

                // Split bytes as 2-byte big-endian GIDs.
                var bytes = show.Bytes;
                for (var i = 0; i + 1 < bytes.Length; i += 2)
                {
                    var gid = (bytes[i] << 8) | bytes[i + 1];

                    // GID 0 is .notdef — that's §7.21.8-1's job; skip it here to avoid double-fire.
                    if (gid == 0)
                        continue;

                    // A GID >= numGlyphs is positively absent from the embedded program.
                    if (gid >= font.Metrics.NumGlyphs)
                    {
                        context.Report(
                            RuleId,
                            Clause,
                            PreflightSeverity.Error,
                            $"A glyph (index {gid}) shown in a composite font with Identity encoding is not "
                            + $"present in the embedded font program, which defines {font.Metrics.NumGlyphs} glyphs. "
                            + "PDF/UA-1 §7.21.4.1 requires every shown glyph to be present in the embedded program.");
                        reported = true;
                        break;
                    }
                }
                if (reported) break;
            }
        }
    }

    // Returns (programObjectNumber, SfntMetrics) when fontRef resolves to a Type0 font that is:
    //   - /Encoding = /Identity-H or /Identity-V (a name, not a stream)
    //   - descendant is CIDFontType2
    //   - /CIDToGIDMap absent or the name /Identity (so code == CID == GID)
    //   - /FontFile2 is present as an indirect reference and its decoded bytes parse with SfntMetrics
    // Returns null for any out-of-scope font (unembedded, non-Identity, CFF, etc.).
    private static (int ProgramObj, SfntMetrics Metrics)? TryGetInScopeFont(
        PreflightContext context, PdfObject? fontRef)
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

        // /CIDToGIDMap must be absent or the name /Identity (so code == CID == GID).
        var cidToGidMapObj = context.Resolve(cidFont.Get(_cidToGidMap));
        if (cidToGidMapObj is not null
            && (cidToGidMapObj is not PdfName cidToGidMapName || cidToGidMapName.Value != "Identity"))
            return null;

        // §7.21.4.1-2 only applies when the font IS embedded (presence is §7.21.4.1-1's job).
        if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;
        if (descriptor.Get(_fontFile2) is not PdfIndirectReference fontFileRef)
            return null;
        if (context.ResolveStream(fontFileRef) is not { } program
            || context.DecodeStream(program) is not { } programBytes)
            return null;

        // If SfntMetrics can't parse the program → isGlyphPresent == null → don't fire.
        if (SfntMetrics.TryParse(programBytes) is not { } metrics)
            return null;

        return (fontFileRef.ObjectNumber, metrics);
    }
}
