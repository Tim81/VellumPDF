// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 list-structure containment and caption-position rules:
/// <list type="bullet">
///   <item>7.2-17 (SELI)      — LI parent == L</item>
///   <item>7.2-18 (SELBody)   — LBody parent == LI</item>
///   <item>7.2-19 (SEL)       — L kids ∈ {L, LI, Caption}</item>
///   <item>7.2-20 (SELI)      — LI kids ∈ {Lbl, LBody}</item>
///   <item>7.2-40 (SEL)       — Caption may only be the FIRST kid of an L</item>
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
/// <para><strong>7.2-40 position rule.</strong> Same shape as 7.2-28: Caption may only appear
/// as the first kid of an L (index 0 in the null-omitted list). Probe-confirmed: [LI, Caption] →
/// fires 7.2-40; [Caption, LI] → passes; [LI, LI, Caption] → fires 7.2-40.</para>
///
/// <para>Cross-validated against veraPDF 1.30.2 for each rule (violating + compliant fixture).</para>
/// </remarks>
internal sealed class UaListContainmentRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-17-18-19-20-40";

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
            if (!EvaluateNode(context, node))
                return; // early-exit on parent-type violation (matches B2 semantics)
        }
    }

    // Returns false when a parent-type violation is found (halts the walk, matching B2 behaviour).
    private static bool EvaluateNode(PreflightContext context, StructureTreeNode node)
    {
        var st = node.StandardType;
        if (st is null)
            return true;

        var parentSt = node.Parent?.StandardType; // null when parent is StructTreeRoot

        // ── PARENT-TYPE rules ─────────────────────────────────────────────────

        // 7.2-17: LI parent must be L
        if (st == "LI" && parentSt != "L")
        {
            Report(context, "ISO14289-1:7.2-17",
                $"A LI structure element has parent /{parentSt ?? "<none>"} but LI requires a L parent (§7.2).");
            return false;
        }

        // 7.2-18: LBody parent must be LI
        if (st == "LBody" && parentSt != "LI")
        {
            Report(context, "ISO14289-1:7.2-18",
                $"A LBody structure element has parent /{parentSt ?? "<none>"} but LBody requires a LI parent (§7.2).");
            return false;
        }

        // ── KID-TYPE and POSITION rules ───────────────────────────────────────

        if (st == "L")
        {
            var kidTypes = node.Children
                .Select(c => c.StandardType)
                .Where(t => t != null)
                .Select(t => t!)
                .ToList();

            // 7.2-19: kid-type membership
            foreach (var kidType in kidTypes)
            {
                if (!LKids.Contains(kidType))
                {
                    Report(context, "ISO14289-1:7.2-19",
                        "A L structure element has a child that is not L, LI, or Caption (§7.2-19).");
                    return true; // continue to next node (not a fatal parent-type error)
                }
            }

            // 7.2-40: Caption may only be the first kid (index 0 in null-omitted list)
            for (int i = 1; i < kidTypes.Count; i++)
            {
                if (kidTypes[i] == "Caption")
                {
                    Report(context, "ISO14289-1:7.2-40",
                        "A L structure element has a Caption child that is not the first child (§7.2-40).");
                    return true;
                }
            }
        }
        else if (st == "LI")
        {
            // 7.2-20: LI kids must be Lbl or LBody (or unmapped/null)
            CheckKids(context, node, LiKids, "ISO14289-1:7.2-20",
                "A LI structure element has a child that is not Lbl or LBody (§7.2).");
        }

        return true;
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
