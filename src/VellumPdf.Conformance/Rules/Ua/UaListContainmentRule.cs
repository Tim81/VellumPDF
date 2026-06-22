// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 list-structure containment rules:
/// <list type="bullet">
///   <item>7.2-17 (SELI)      — LI parent == L</item>
///   <item>7.2-18 (SELBody)   — LBody parent == LI</item>
///   <item>7.2-19 (SEL)       — L kids ∈ {L, LI, Caption}</item>
///   <item>7.2-20 (SELI)      — LI kids ∈ {Lbl, LBody}</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>FP-safety — null parent.</strong> An element whose walker-parent is null is a
/// direct /K child of the StructTreeRoot (no parent StructElem). veraPDF's parent-type predicate
/// is a string equality test that fails for null → LI/LBody at the root fires.
/// Probe-confirmed: <c>probe_li_wrong_parent</c> (LI in Document) → fires 7.2-17 (exit 1).</para>
///
/// <para><strong>FP-safety — null-StandardType kid.</strong> Kids whose StandardType is null
/// (non-standard, unmapped) are silently skipped — veraPDF's kidsStandardTypes omits them.
/// So a custom unmapped kid does not cause the kid-type check to fire. Same semantics as the
/// table rules, confirmed by <c>probe_tr_custom_kid</c> analogy.</para>
///
/// <para><strong>FP-safety — empty kid list.</strong> If an L or LI has no StructElem kids the
/// check is vacuously satisfied — no fire. Probe-confirmed: <c>probe_list_compliant</c> → PASS
/// (a well-formed list passes all list rules).</para>
///
/// <para>Cross-validated against veraPDF 1.30.2 for each rule (violating + compliant fixture).</para>
/// </remarks>
internal sealed class UaListContainmentRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-17-18-19-20";

    public string Clause => "ISO 14289-1:2014, 7.2";

    // Allowed kids for L (7.2-19)
    private static readonly HashSet<string> LKids =
        new(["L", "LI", "Caption"], StringComparer.Ordinal);

    // Allowed kids for LI (7.2-20)
    private static readonly HashSet<string> LiKids =
        new(["Lbl", "LBody"], StringComparer.Ordinal);

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
                // ── PARENT-TYPE rules ─────────────────────────────────────────────────

                // 7.2-17: LI parent must be L
                case "LI" when parentSt != "L":
                    Report(context, "ISO14289-1:7.2-17",
                        $"A LI structure element has parent /{parentSt ?? "<none>"} but LI requires a L parent (§7.2).");
                    return;

                // 7.2-18: LBody parent must be LI
                case "LBody" when parentSt != "LI":
                    Report(context, "ISO14289-1:7.2-18",
                        $"A LBody structure element has parent /{parentSt ?? "<none>"} but LBody requires a LI parent (§7.2).");
                    return;

                // ── KID-TYPE rules ────────────────────────────────────────────────────

                // 7.2-19: L kids must be L/LI/Caption (or unmapped/null)
                case "L":
                    CheckKids(context, node, LKids, "ISO14289-1:7.2-19",
                        "A L structure element has a child that is not L, LI, or Caption (§7.2).");
                    break;

                // 7.2-20: LI kids must be Lbl or LBody (or unmapped/null)
                case "LI":
                    CheckKids(context, node, LiKids, "ISO14289-1:7.2-20",
                        "A LI structure element has a child that is not Lbl or LBody (§7.2).");
                    break;
            }
        }
    }

    /// <summary>
    /// Checks that every StructElem child of <paramref name="node"/> whose StandardType is
    /// non-null is in <paramref name="allowed"/>. Null-type kids (non-standard/unmapped) are
    /// skipped — veraPDF ignores them in the kidsStandardTypes predicate.
    /// </summary>
    private static void CheckKids(
        PreflightContext context,
        StructureTreeNode node,
        HashSet<string> allowed,
        string ruleId,
        string message)
    {
        foreach (var kid in node.Children)
        {
            var kidSt = kid.StandardType;
            if (kidSt is null)
                continue; // unmapped/non-standard — ignored by veraPDF
            if (!allowed.Contains(kidSt))
            {
                Report(context, ruleId, message);
                return;
            }
        }
    }

    private static void Report(PreflightContext context, string ruleId, string message)
        => context.Report(ruleId, "ISO 14289-1:2014, 7.2", PreflightSeverity.Error, message);
}
