// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 table-of-contents containment and caption-position rules:
/// <list type="bullet">
///   <item>7.2-26 (SETOCI) — TOCI parent == TOC</item>
///   <item>7.2-27 (SETOC)  — TOC kids ∈ {TOC, TOCI, Caption}</item>
///   <item>7.2-28 (SETOC)  — Caption may only be the FIRST kid of a TOC</item>
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
/// <para><strong>7.2-28 position rule.</strong> veraPDF's predicate checks that no Caption
/// appears at index ≥ 1 in the null-omitted kid list. Caption may only be the FIRST kid
/// (unlike 7.2-16 for Table, which also permits last). Probe-confirmed: [TOCI, Caption] →
/// fires 7.2-28; [Caption, TOCI] → passes. Null-omission applies: an unmapped kid preceding
/// Caption makes Caption index 0 → passes. Probe-confirmed: [untyped, Caption, TOCI] → passes.</para>
///
/// <para>Cross-validated against veraPDF 1.30.2 for all rules (violating + compliant fixture).</para>
/// </remarks>
internal sealed class UaTocContainmentRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-26-27-28";

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

                // ── KID-TYPE and POSITION rules ───────────────────────────────────────

                // 7.2-27: TOC kids must be TOC/TOCI/Caption (or unmapped/null)
                // 7.2-28: Caption (if present) may only be the FIRST kid of TOC
                case "TOC":
                {
                    var kidTypes = node.Children
                        .Select(c => c.StandardType)
                        .Where(t => t != null)
                        .Select(t => t!)
                        .ToList();

                    // 7.2-27: kid-type membership
                    foreach (var kidType in kidTypes)
                    {
                        if (!TocKids.Contains(kidType))
                        {
                            Report(context, "ISO14289-1:7.2-27",
                                "A TOC structure element has a child that is not TOC, TOCI, or Caption (§7.2-27).");
                            return;
                        }
                    }

                    // 7.2-28: Caption may only be the first kid (index 0 in null-omitted list)
                    for (int i = 1; i < kidTypes.Count; i++)
                    {
                        if (kidTypes[i] == "Caption")
                        {
                            Report(context, "ISO14289-1:7.2-28",
                                "A TOC structure element has a Caption child that is not the first child (§7.2-28).");
                            return;
                        }
                    }
                    break;
                }
            }
        }
    }

    private static void Report(PreflightContext context, string ruleId, string message)
        => context.Report(ruleId, "ISO 14289-1:2014, 7.2", PreflightSeverity.Error, message);
}
