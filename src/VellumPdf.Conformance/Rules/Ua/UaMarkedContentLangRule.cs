// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 natural-language determination rules (Batch C1):
/// <list type="bullet">
///   <item>7.2-30 (SEMarkedContent): a <c>/Span</c>-tagged BDC with <c>/ActualText</c> must have a
///         determinable language.</item>
///   <item>7.2-31 (SEMarkedContent): a <c>/Span</c>-tagged BDC with <c>/Alt</c> must have a
///         determinable language.</item>
///   <item>7.2-32 (SEMarkedContent): a <c>/Span</c>-tagged BDC with <c>/E</c> must have a
///         determinable language.</item>
///   <item>7.2-34 (SETextItem): each text-show operator must have a determinable language.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>Language resolution order (most specific to least):</strong></para>
/// <list type="number">
///   <item>Catalog <c>/Lang</c> present (short-circuit, dominant path — all conformant UA-1 docs).</item>
///   <item>BDC <c>/Lang</c> or inherited BDC <c>/Lang</c> on the enclosing MC sequence.</item>
///   <item>Owning struct element's <c>/Lang</c>, or any ancestor's <c>/Lang</c>, resolved via
///         MCID → /ParentTree page-MCID-array → StructElem → walk up via /Parent.</item>
/// </list>
///
/// <para>The MCID→struct-element path is the fix for the confirmed false positive: a UA-1 doc
/// with NO catalog /Lang but /Lang on the /P struct element wrapping the text is accepted by
/// veraPDF (no 7.2-34 failure), but the pre-fix rule fired 7.2-34. The struct-elem /Lang is
/// the normal tagged-PDF pattern.</para>
///
/// <para><strong>gContainsCatalogLang short-circuit (the dominant path).</strong>
/// Every conformant PDF/UA-1 document has a catalog <c>/Lang</c> (required by UaLangRule).
/// These rules can only fire in a document that already fails 7.2-lang. The short-circuit keys
/// on the mere presence of the catalog <c>/Lang</c> key, matching veraPDF's <c>containsLang</c>.</para>
///
/// <para><strong>Scope:</strong> page content streams only. Form XObjects, Type 3 CharProcs,
/// and annotation appearance streams are not walked.</para>
///
/// <para>Clean-room: derived from ISO 14289-1:2014 §7.2 and empirically validated against
/// veraPDF 1.30.2. Not derived from any third-party implementation.</para>
/// </remarks>
internal sealed class UaMarkedContentLangRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-30-31-32-34";

    public string Clause => "ISO 14289-1:2014, 7.2";

    private static readonly PdfName _lang = new("Lang");

    public void Evaluate(PreflightContext context)
    {
        // ── gContainsCatalogLang short-circuit ────────────────────────────────
        // veraPDF's containsLang is satisfied by the mere PRESENCE of the catalog
        // /Lang key — an empty /Lang () still counts as containsLang == true
        // (verified against veraPDF 1.30.2; see UaNaturalLanguageRule). An empty
        // /Lang is separately flagged by the Lang-syntax rule (7.2-29 /
        // UaLangSyntaxRule), so short-circuiting here on key presence does NOT mask
        // that violation; it only prevents a clause-level false positive on
        // 7.2-30/31/32/34 (which veraPDF passes whenever the /Lang key exists).
        if (context.Catalog.Get(_lang) is not null)
            return;

        // Catalog has no /Lang — run per-page content-stream checks.
        var structTree = StructureTree.Analyze(context);
        var firedTextNoLang = false;

        foreach (var page in context.EnumeratePages())
        {
            var usage = ContentStreamUsage.Analyze(context, page);

            // ── 7.2-30/31/32: /Span BDC with ActualText/Alt/E and no lang ────
            foreach (var seq in usage.MarkedContentSequences)
            {
                if (!string.Equals(seq.Tag, "Span", StringComparison.Ordinal))
                    continue;

                var hasLang = seq.Lang is not null || seq.InheritedLang is not null;
                if (hasLang)
                    continue;

                // Check struct-element /Lang via MCID→ParentTree resolution.
                if (seq.Mcid is int mcid)
                {
                    var node = structTree.StructNodeForMcid(context, page, mcid);
                    if (StructureTree.HasLangInHierarchy(node))
                        continue;
                }

                if (seq.ActualText is not null)
                {
                    context.Report(
                        "ISO14289-1:7.2-30",
                        Clause,
                        PreflightSeverity.Error,
                        "A /Span-tagged marked-content sequence has an /ActualText property but no "
                        + "determinable natural language: neither the sequence, its ancestor "
                        + "marked-content sequences, nor the owning struct element hierarchy "
                        + "carries a /Lang property, and the document catalog has no /Lang "
                        + "(ISO 14289-1:2014, 7.2, testNumber 30).");
                }

                if (seq.Alt is not null)
                {
                    context.Report(
                        "ISO14289-1:7.2-31",
                        Clause,
                        PreflightSeverity.Error,
                        "A /Span-tagged marked-content sequence has an /Alt property but no "
                        + "determinable natural language: neither the sequence, its ancestor "
                        + "marked-content sequences, nor the owning struct element hierarchy "
                        + "carries a /Lang property, and the document catalog has no /Lang "
                        + "(ISO 14289-1:2014, 7.2, testNumber 31).");
                }

                if (seq.Expansion is not null)
                {
                    context.Report(
                        "ISO14289-1:7.2-32",
                        Clause,
                        PreflightSeverity.Error,
                        "A /Span-tagged marked-content sequence has an /E property but no "
                        + "determinable natural language: neither the sequence, its ancestor "
                        + "marked-content sequences, nor the owning struct element hierarchy "
                        + "carries a /Lang property, and the document catalog has no /Lang "
                        + "(ISO 14289-1:2014, 7.2, testNumber 32).");
                }
            }

            // ── 7.2-34: text show with no determinable language ───────────────
            // Fire at most once per page to avoid flooding; the per-show FP risk is the same for
            // every show on the same page.
            if (!firedTextNoLang)
            {
                foreach (var ctx in usage.TextShowContexts)
                {
                    if (ctx.DirectLang is not null || ctx.InheritedLang is not null)
                        continue;

                    // Check struct-element /Lang via MCID→ParentTree resolution.
                    if (ctx.Mcid is int mcid)
                    {
                        var node = structTree.StructNodeForMcid(context, page, mcid);
                        if (StructureTree.HasLangInHierarchy(node))
                            continue;
                    }

                    context.Report(
                        "ISO14289-1:7.2-34",
                        Clause,
                        PreflightSeverity.Error,
                        "A text-show operator (Tj/TJ/'/\") occurs with no determinable natural "
                        + "language: the enclosing marked-content sequence (if any) carries no "
                        + "/Lang property, the owning struct element hierarchy carries no /Lang, "
                        + "and the document catalog has no /Lang "
                        + "(ISO 14289-1:2014, 7.2, testNumber 34).");
                    firedTextNoLang = true;
                    break;
                }
            }
        }
    }
}
