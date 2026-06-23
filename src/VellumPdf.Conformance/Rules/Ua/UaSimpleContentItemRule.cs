// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.1-3 (<c>SESimpleContentItem</c>): every real-content operator must be either
/// tagged (its nearest enclosing BDC with an MCID resolves in the document's /ParentTree) or
/// present inside content marked as Artifact.
/// </summary>
/// <remarks>
/// <para><strong>veraPDF predicate (PDFUA-1.xml):</strong>
/// object <c>SESimpleContentItem</c>, clause <c>7.1</c>, testNumber <c>3</c>:
/// <c>isTaggedContent == true || parentsTags.contains('Artifact') == true</c>.
/// Fires when a content item is NEITHER tagged NOR inside an Artifact.</para>
///
/// <para><strong>Content-item operator set (probe-verified, §7.1-3 note in ConformanceCatalog):</strong></para>
/// <list type="bullet">
///   <item>CREATE a content item: <c>Tj</c>, <c>TJ</c>, <c>'</c>, <c>"</c> (text-show);
///         <c>S</c>, <c>s</c>, <c>f</c>, <c>F</c>, <c>f*</c>, <c>B</c>, <c>B*</c>, <c>b</c>,
///         <c>b*</c> (painting path ops); <c>EI</c> (inline-image terminator, emitted at
///         <c>ID</c>); <c>Do</c> when the named XObject has <c>/Subtype /Image</c>; <c>sh</c>.</item>
///   <item>Do NOT create a content item: <c>n</c>, <c>W n</c>, <c>W* n</c> (clip/no-paint),
///         path-construction only (<c>m</c>/<c>l</c>/<c>c</c>/<c>re</c> with no painting
///         terminal), color/state ops, <c>Do</c> on a <c>/Form</c> XObject.</item>
/// </list>
///
/// <para><strong>isTaggedContent model:</strong> a content item is tagged when its
/// <see cref="SimpleContentItem.EffectiveMcid"/> (nearest enclosing BDC MCID, direct or
/// inherited via <see cref="MarkedContentSequence.AncestorMcid"/>) is non-null. When
/// <see cref="SimpleContentItem.EffectiveMcid"/> is null the item has no enclosing BDC with
/// an MCID at all — it is definitively untagged.</para>
///
/// <para><strong>parentsTags.contains('Artifact') model:</strong> true when
/// <see cref="SimpleContentItem.IsInsideArtifact"/> is set — the MC-stack top at item-emit time
/// is or has an Artifact ancestor.</para>
///
/// <para><strong>FP-safe gate:</strong> fires ONLY when
/// <see cref="SimpleContentItem.EffectiveMcid"/> is <see langword="null"/> AND
/// <see cref="SimpleContentItem.IsInsideArtifact"/> is <see langword="false"/>. When an MCID is
/// present (even if the ParentTree lookup would fail), the item is skipped — preferring not to
/// fire over false positives on documents with malformed or partial ParentTrees.
/// <see langword="null"/> from <see cref="StructureTree.StructNodeForMcid"/> is therefore not
/// consulted: the check is purely on the MC-stack context captured at emit time.</para>
///
/// <para><strong>Fire once per page:</strong> at most one violation per page is reported to
/// avoid flooding on pages with many untagged operators.</para>
///
/// <para><strong>Scope:</strong> page content streams only. Form XObjects, Type 3 CharProcs,
/// and annotation appearance streams are not walked — under-detection, FP-safe.</para>
///
/// <para>Verified FP-free against veraPDF 1.30.2 across the FP battery (Batch C3).</para>
/// </remarks>
internal sealed class UaSimpleContentItemRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.1-3";

    public string Clause => "ISO 14289-1:2014, 7.1";

    public void Evaluate(PreflightContext context)
    {
        foreach (var page in context.EnumeratePages())
        {
            var usage = ContentStreamUsage.Analyze(context, page);
            if (usage.SimpleContentItems.Count == 0)
                continue;

            // Fire at most once per page: find first untagged non-artifact content item.
            foreach (var item in usage.SimpleContentItems)
            {
                // Artifact: any Artifact ancestor (or direct Artifact) → OK.
                if (item.IsInsideArtifact)
                    continue;

                // Tagged: any enclosing BDC carries an MCID → treat as tagged (FP-safe: even if the
                // MCID does not resolve in the ParentTree, prefer silence over a false positive).
                if (item.EffectiveMcid is not null)
                    continue;

                // No MCID at all, not inside any Artifact → definitively untagged real content.
                context.Report(
                    "ISO14289-1:7.1-3",
                    Clause,
                    PreflightSeverity.Error,
                    "A real-content operator creates a content item (SESimpleContentItem) "
                    + "that is neither tagged (no enclosing BDC with an MCID) nor inside content "
                    + "marked as Artifact. ISO 14289-1:2014, §7.1, testNumber 3.");
                break;
            }
        }
    }
}
