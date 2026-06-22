// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.1 testNumber 12 (PDF/UA-1: PDStructElem — every structure element shall
/// contain a <c>/P</c> (parent) entry).
/// </summary>
/// <remarks>
/// The veraPDF predicate (object PDStructElem, clause 7.1, testNumber 12) is:
/// <c>containsParent == true</c>. Translated: every StructElem dictionary in the tree shall
/// carry a <c>/P</c> entry (pointing to its parent StructElem or to the StructTreeRoot).
///
/// <para>FP-safety notes:</para>
/// <para>
/// (1) The StructTreeRoot itself is NOT a StructElem — we walk the tree starting from the
///     StructTreeRoot's /K children, so the root is never in the node list.
/// </para>
/// <para>
/// (2) We read <c>/P</c> directly from the raw StructElem dict using <c>Dict.Get()</c> —
///     a key is "present" when it exists in the dict regardless of its resolved value. The
///     veraPDF predicate is <c>containsParent</c> (key presence), not a type check.
/// </para>
/// <para>
/// (3) Malformed nodes (non-dict /K entries, MCR/OBJR kids) are silently skipped by the
///     walker and never appear in <see cref="StructureTree.AllNodes"/>.
/// </para>
///
/// <para>
/// Cross-validated against veraPDF 1.30.2:
/// (a) a StructElem missing its /P fires clause 7.1, testNumber 12 (exit 1);
/// (b) the normal UA-1 tagged baseline has /P on every StructElem — veraPDF does NOT fire
///     7.1-12 (exit 0).
/// </para>
/// </remarks>
internal sealed class UaStructElemParentRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.1-12";

    public string Clause => "ISO 14289-1:2014, 7.1";

    private static readonly PdfName _p = new("P");

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return; // No structure tree — UaTaggingRule covers the missing-StructTreeRoot case.

        foreach (var node in tree.AllNodes)
        {
            // Check key presence only: the veraPDF predicate is containsParent (exists), not type.
            if (node.Dict.Get(_p) is null)
            {
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    $"A structure element (/{node.RawType ?? "<no /S>"}) is missing the required "
                    + "/P (parent) entry. PDF/UA-1 §7.1 requires every structure element to contain a "
                    + "parent reference.");
                return; // report at most once per document
            }
        }
    }
}
