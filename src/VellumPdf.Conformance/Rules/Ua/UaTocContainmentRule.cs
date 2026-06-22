// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 table-of-contents containment rules:
/// <list type="bullet">
///   <item>7.2-26 (SETOCI) — TOCI parent == TOC</item>
///   <item>7.2-27 (SETOC)  — TOC kids ∈ {TOC, TOCI, Caption}</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>FP-safety — null parent.</strong> An element whose walker-parent is null is a
/// direct /K child of the StructTreeRoot (no parent StructElem). veraPDF's parent-type predicate
/// is a string equality test that fails for null — so a TOCI at the root fires 7.2-26.
/// Probe-confirmed: <c>probe_toci_wrong_parent</c> (TOCI in Document) → fires 7.2-26 (exit 1).</para>
///
/// <para><strong>FP-safety — null-StandardType kid.</strong> Kids whose StandardType is null
/// (non-standard, unmapped) are silently skipped — veraPDF's kidsStandardTypes omits them.
/// A custom unmapped kid does not cause the kid-type check to fire. Consistent with table and
/// list rule semantics, confirmed by <c>probe_tr_custom_kid</c> analogy.</para>
///
/// <para><strong>FP-safety — empty kid list.</strong> If a TOC has no StructElem kids the
/// check is vacuously satisfied — no fire. Consistent with table/list semantics.</para>
///
/// <para>Cross-validated against veraPDF 1.30.2 for both rules (violating + compliant fixture).</para>
/// </remarks>
internal sealed class UaTocContainmentRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-26-27";

    public string Clause => "ISO 14289-1:2014, 7.2";

    // Allowed kids for TOC (7.2-27)
    private static readonly HashSet<string> TocKids =
        new(["TOC", "TOCI", "Caption"], StringComparer.Ordinal);

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return;

        foreach (var node in tree.AllNodes)
        {
            var st = node.StandardType;
            if (st is null)
                continue;

            var parentSt = node.Parent?.StandardType; // null when parent is StructTreeRoot

            switch (st)
            {
                // ── PARENT-TYPE rule ──────────────────────────────────────────────────

                // 7.2-26: TOCI parent must be TOC
                case "TOCI" when parentSt != "TOC":
                    Report(context, "ISO14289-1:7.2-26",
                        $"A TOCI structure element has parent /{parentSt ?? "<none>"} but TOCI requires a TOC parent (§7.2).");
                    return;

                // ── KID-TYPE rule ─────────────────────────────────────────────────────

                // 7.2-27: TOC kids must be TOC/TOCI/Caption (or unmapped/null)
                case "TOC":
                    foreach (var kid in node.Children)
                    {
                        var kidSt = kid.StandardType;
                        if (kidSt is null)
                            continue; // unmapped/non-standard — ignored by veraPDF
                        if (!TocKids.Contains(kidSt))
                        {
                            Report(context, "ISO14289-1:7.2-27",
                                "A TOC structure element has a child that is not TOC, TOCI, or Caption (§7.2).");
                            return;
                        }
                    }
                    break;
            }
        }
    }

    private static void Report(PreflightContext context, string ruleId, string message)
        => context.Report(ruleId, "ISO 14289-1:2014, 7.2", PreflightSeverity.Error, message);
}
