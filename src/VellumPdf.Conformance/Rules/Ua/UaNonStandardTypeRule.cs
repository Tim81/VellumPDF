// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.1 testNumber 5 (PDF/UA-1: PDStructElem — non-standard structure type
/// that is not role-mapped to a Table 333 standard type).
/// </summary>
/// <remarks>
/// The veraPDF predicate (object PDStructElem, clause 7.1, testNumber 5) is:
/// <c>isNotMappedToStandardType == false</c>. Translated: every structure element whose
/// <c>/S</c> is a NON-standard type must be role-mapped (via the StructTreeRoot
/// <c>/RoleMap</c>, possibly multi-hop) to one of the ISO 32000-1 Table 333 standard
/// structure types. We fire when a walked StructElem has a non-null <see cref="StructureTreeNode.RawType"/>
/// that does not resolve to a standard type (<see cref="StructureTreeNode.StandardType"/> is null).
///
/// <para>FP-safety — standard-type-set completeness:</para>
/// <para>
/// The firing condition depends on <see cref="StructureTree.StandardTypes"/> exactly matching
/// what veraPDF treats as "standard". A missing entry would fire 7.1-5 for a type that
/// veraPDF accepts, producing a false positive. The set was empirically verified against
/// veraPDF 1.30.2 by probing every name in <see cref="StructureTree.StandardTypes"/>
/// (Document, Part, Art, … Form — all 47 entries) as a bare <c>/S</c> without a
/// <c>/RoleMap</c>: veraPDF does NOT fire 7.1-5 for any of them.
/// </para>
/// <para>
/// Additional probes covered types that veraPDF MIGHT treat as standard but are NOT in our
/// set (PDF 2.0 types Aside, Title, Sub, FENote, Em, Strong, DocumentFragment, Artifact,
/// and hypothetical Hn, LILabel): veraPDF fires 7.1-5 for all of them — our set is
/// complete for all probed candidates.
/// </para>
/// <para>
/// Positive-case probes:
/// (a) <c>/MyCustomThing</c> without a <c>/RoleMap</c>: veraPDF fires 7.1-5 (exit 1).
/// (b) <c>/MyCustomThing</c> with <c>/RoleMap &lt;&lt; /MyCustomThing /P &gt;&gt;</c>:
///     veraPDF does NOT fire 7.1-5 (exit 0).
/// </para>
/// <para>
/// Cross-validated against veraPDF 1.30.2 (local oracle, June 2026):
/// pdfua1-non-standard-type-unmapped: veraPDF exits 1 (non-compliant, 7.1-5 fires);
/// pdfua1-non-standard-type-rolemapped: veraPDF exits 0 (compliant);
/// pdfua1-tagged (standard type baseline): veraPDF exits 0 (compliant).
/// </para>
/// </remarks>
internal sealed class UaNonStandardTypeRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.1-5";

    public string Clause => "ISO 14289-1:2014, 7.1";

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return; // No structure tree — UaTaggingRule covers the missing-StructTreeRoot case.

        foreach (var node in tree.AllNodes)
        {
            // Fire only when the element has a /S entry (RawType != null) and the type is not
            // a standard Table 333 type and does not resolve to one via the /RoleMap chain.
            // Elements with no /S are malformed — UaTaggingRule and other rules cover that case.
            if (node.RawType is not null && node.StandardType is null)
            {
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    $"A structure element uses the non-standard structure type /{node.RawType}, "
                    + "which is not role-mapped (via the StructTreeRoot /RoleMap) to one of the "
                    + "ISO 32000-1 Table 333 standard structure types. PDF/UA-1 §7.1 requires "
                    + "every non-standard structure type to be role-mapped to a standard type.");
                return; // report at most once per document
            }
        }
    }
}
