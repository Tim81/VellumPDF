// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// PDF/A-2a: reports real content that no structure element describes — content that is neither
/// tagged nor marked as an artifact — in a document whose conformance level requires a logical
/// structure that describes its content.
/// </summary>
/// <remarks>
/// <para><strong>This check has no counterpart in veraPDF's PDF/A-2a profile, which is why it
/// reports <see cref="PreflightSeverity.Warning"/> rather than
/// <see cref="PreflightSeverity.Error"/>.</strong> veraPDF 1.30.2's bundled <c>PDFA-2A.xml</c>
/// carries 153 rules, of which exactly one sits at clause 6.7.3.3 — the check that the document
/// catalog contains a <c>/StructTreeRoot</c> — and the profile contains no
/// <c>SESimpleContentItem</c> rule at all. The equivalent content-item predicate exists only in
/// veraPDF's <c>PDFUA-1.xml</c>. Reporting an error here would therefore make this library
/// contradict the reference implementation on a file veraPDF certifies as compliant, and would
/// break the corpus invariant that both validators agree on every fixture's verdict. A warning
/// keeps <see cref="PreflightResult.IsCompliant"/> aligned with veraPDF while still telling the
/// caller what is wrong; <c>vellum-preflight --fail-on warning</c> turns it into a build failure
/// for callers who want that.</para>
///
/// <para><strong>Why the check is needed at all.</strong> Presence of a <c>/StructTreeRoot</c> is
/// not evidence that the structure tree describes anything. A document carrying
/// <c>/MarkInfo /Marked true</c>, a <c>/StructTreeRoot</c>, and a <c>/Document</c> element with an
/// empty <c>/K</c> satisfies every structural rule either validator implements for PDF/A-2a while
/// leaving every glyph on the page undescribed — which is precisely what level A exists to
/// prevent. Before the writer began emitting a structure tree for any tagged document (issue
/// #120), the absent <c>/StructTreeRoot</c> incidentally caught this case; it no longer does, so
/// the condition is now checked directly instead of as a side effect.</para>
///
/// <para><strong>Predicate.</strong> Deliberately the same one
/// <see cref="Ua.UaSimpleContentItemRule"/> implements for ISO 14289-1 §7.1-3, over the same
/// <see cref="ContentStreamUsage"/> analysis, so the two cannot drift: a content item is reported
/// only when its <see cref="SimpleContentItem.EffectiveMcid"/> is <see langword="null"/> (no
/// enclosing marked-content sequence carries an MCID) and
/// <see cref="SimpleContentItem.IsInsideArtifact"/> is <see langword="false"/>. An item with any
/// MCID is skipped even if that MCID would not resolve in the <c>/ParentTree</c> — false-positive
/// safety is preferred over completeness, since this fires on documents a caller believes are
/// conformant.</para>
///
/// <para><strong>Scope.</strong> Page content streams only, at most one report per page, matching
/// <see cref="Ua.UaSimpleContentItemRule"/>. Form XObjects, Type 3 CharProcs and annotation
/// appearance streams are not walked, so this under-detects rather than over-detects.</para>
/// </remarks>
internal sealed class A2aContentItemTaggingRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.7.3.3-content-items";

    public string Clause => "ISO 19005-2:2011, 6.7.3.3";

    public void Evaluate(PreflightContext context)
    {
        foreach (var page in context.EnumeratePages())
        {
            var usage = ContentStreamUsage.Analyze(context, page);
            if (usage.SimpleContentItems.Count == 0)
                continue;

            foreach (var item in usage.SimpleContentItems)
            {
                if (item.IsInsideArtifact)
                    continue;

                if (item.EffectiveMcid is not null)
                    continue;

                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Warning,
                    "A real-content operator on this page is neither tagged (no enclosing "
                    + "marked-content sequence carries an MCID) nor marked as an artifact, so no "
                    + "structure element describes it. A PDF/A-2a document's logical structure is "
                    + "required to describe its content, and a /StructTreeRoot that describes "
                    + "nothing does not satisfy that. Note that veraPDF's PDF/A-2a profile does "
                    + "not implement this check, so it reports such a file as compliant; this is "
                    + "reported as a warning for that reason. Use --fail-on warning to treat it "
                    + "as a failure.");
                break;
            }
        }
    }
}
