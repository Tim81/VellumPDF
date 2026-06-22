// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.1 artifact / tagged-content nesting rules (Batch C2):
/// <list type="bullet">
///   <item>§7.1-1 (<c>SEMarkedContent</c>, tag=Artifact): content marked as Artifact must not be
///         present inside tagged content (i.e. an Artifact BDC whose BDC ancestor chain contains a
///         sequence with an MCID that resolves in the document's /ParentTree).</item>
///   <item>§7.1-2 (<c>SEMarkedContent</c>): tagged content (BDC with MCID resolving in /ParentTree)
///         must not be present inside content marked as Artifact (i.e. its BDC ancestor chain
///         contains an Artifact tag).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>veraPDF predicate translation (from the bundled PDFUA-1.xml profile):</strong></para>
/// <list type="bullet">
///   <item>7.1-1: <c>tag != 'Artifact' || isTaggedContent == false</c> — fails when
///         <c>tag == 'Artifact' &amp;&amp; isTaggedContent == true</c>.</item>
///   <item>7.1-2: <c>isTaggedContent == false || parentsTags.contains('Artifact') == false</c> —
///         fails when <c>isTaggedContent == true &amp;&amp; parentsTags.contains('Artifact')</c>.</item>
/// </list>
///
/// <para><strong>isTaggedContent model:</strong></para>
/// <list type="bullet">
///   <item>For 7.1-1 (Artifact BDC): <c>isTaggedContent == true</c> when any ancestor BDC in the
///         MC stack at push time carries an MCID that resolves to a struct element in the page's
///         /ParentTree (i.e. the Artifact is nested inside a struct-linked content region).
///         Captured via <see cref="MarkedContentSequence.AncestorMcid"/>, resolved with
///         <see cref="StructureTree.StructNodeForMcid"/>.</item>
///   <item>For 7.1-2 (any non-trivially-Artifact BDC): <c>isTaggedContent == true</c> when the
///         BDC's <em>own</em> MCID resolves to a struct element in the page's /ParentTree.
///         Captured via <see cref="MarkedContentSequence.Mcid"/>.</item>
/// </list>
///
/// <para><strong>parentsTags model:</strong> <c>parentsTags.contains('Artifact')</c> is true when any
/// ancestor BDC in the MC stack at push time carries the tag "Artifact". Captured via
/// <see cref="MarkedContentSequence.HasArtifactAncestor"/>.</para>
///
/// <para><strong>FP-safe design:</strong></para>
/// <list type="bullet">
///   <item>For 7.1-1: an Artifact BMC/BDC at top level (no ancestor BDC → <c>AncestorMcid == null</c>)
///         is always silent. Only fires when <c>AncestorMcid</c> resolves in the ParentTree.</item>
///   <item>For 7.1-2: a BDC without its own MCID, or with an MCID not in the ParentTree, is not
///         tagged content and cannot trigger 7.1-2. Only fires when both conditions are confirmed.</item>
///   <item>Null from <see cref="StructureTree.StructNodeForMcid"/> is treated as "unresolvable, skip"
///         (FP-safe: prefer not firing over false positives).</item>
/// </list>
///
/// <para><strong>Scope:</strong> page content streams only. Form XObjects, Type 3 CharProcs,
/// and annotation appearance streams are not walked.</para>
///
/// <para>Empirically validated against veraPDF 1.30.2 (probe series, Batch C2).</para>
/// </remarks>
internal sealed class UaArtifactTaggingRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.1-1-2";

    public string Clause => "ISO 14289-1:2014, 7.1";

    public void Evaluate(PreflightContext context)
    {
        var structTree = StructureTree.Analyze(context);

        foreach (var page in context.EnumeratePages())
        {
            var usage = ContentStreamUsage.Analyze(context, page);

            foreach (var seq in usage.MarkedContentSequences)
            {
                // ── §7.1-1: Artifact must not be inside tagged content ─────────────
                // Fires when tag == "Artifact" AND isTaggedContent == true.
                // isTaggedContent (for an Artifact) = any ancestor BDC has MCID in ParentTree.
                if (seq.IsArtifact && seq.AncestorMcid is int ancestorMcid)
                {
                    // Resolve the ancestor MCID against the ParentTree.
                    // null return = unresolvable → FP-safe, skip.
                    var ancestorNode = structTree.StructNodeForMcid(context, page, ancestorMcid);
                    if (ancestorNode is not null)
                    {
                        context.Report(
                            "ISO14289-1:7.1-1",
                            Clause,
                            PreflightSeverity.Error,
                            "Content marked as Artifact is present inside tagged content: "
                            + "the Artifact BDC is nested within a marked-content sequence "
                            + $"(MCID {ancestorMcid}) that resolves to a struct element in the "
                            + "/ParentTree. ISO 14289-1:2014, §7.1, testNumber 1.");
                    }
                }

                // ── §7.1-2: Tagged content must not be inside an Artifact ──────────
                // Fires when isTaggedContent == true AND parentsTags.contains("Artifact").
                // isTaggedContent (for a non-Artifact BDC) = the BDC's own MCID is in ParentTree.
                // parentsTags.contains("Artifact") = HasArtifactAncestor.
                if (!seq.IsArtifact && seq.HasArtifactAncestor && seq.Mcid is int mcid)
                {
                    var node = structTree.StructNodeForMcid(context, page, mcid);
                    if (node is not null)
                    {
                        context.Report(
                            "ISO14289-1:7.1-2",
                            Clause,
                            PreflightSeverity.Error,
                            "Tagged content (BDC with MCID resolving to a struct element) is "
                            + "present inside content marked as Artifact: the enclosing ancestor "
                            + "BDC/BMC chain contains an Artifact tag. "
                            + "ISO 14289-1:2014, §7.1, testNumber 2.");
                    }
                }
            }
        }
    }
}
