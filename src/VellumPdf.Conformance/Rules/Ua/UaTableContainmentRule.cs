// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 table-structure containment rules:
/// <list type="bullet">
///   <item>7.2-3  (SETable)   — Table kids ∈ {TR, THead, TBody, TFoot, Caption}</item>
///   <item>7.2-4  (SETR)      — TR parent ∈ {Table, THead, TBody, TFoot}</item>
///   <item>7.2-5  (SETHead)   — THead parent == Table</item>
///   <item>7.2-6  (SETBody)   — TBody parent == Table</item>
///   <item>7.2-7  (SETFoot)   — TFoot parent == Table</item>
///   <item>7.2-8  (SETH)      — TH parent == TR</item>
///   <item>7.2-9  (SETD)      — TD parent == TR</item>
///   <item>7.2-10 (SETR)      — TR kids ∈ {TH, TD}</item>
///   <item>7.2-36 (SETHead)   — THead kids ∈ {TR}</item>
///   <item>7.2-37 (SETBody)   — TBody kids ∈ {TR}</item>
///   <item>7.2-38 (SETFoot)   — TFoot kids ∈ {TR}</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>FP-safety — null parent.</strong> An element whose walker-parent is null means
/// it is a direct /K child of the StructTreeRoot (the StructTreeRoot is not a StructElem and
/// has no StandardType). veraPDF evaluates the parent-type predicate as a string equality/regex
/// test that is false for a missing/null value — so a TR/TH/TD/THead/TBody/TFoot at the root
/// of the tree fires. Probe-confirmed: <c>probe_tr_root</c> (TR as StructTreeRoot child) → fires
/// 7.2-4 (exit 1).</para>
///
/// <para><strong>FP-safety — null-StandardType kid.</strong> A child whose /S is non-standard
/// and unmapped (StandardType == null) is IGNORED by the kid-type rules. veraPDF builds
/// <c>kidsStandardTypes</c> from kids' standard types and skips unknown ones — so a custom
/// unmapped kid does not trigger the kid-type check. Probe-confirmed: <c>probe_tr_custom_kid</c>
/// (TR with /MyCustomCell child, no /RoleMap) → PASS (no 7.2-10 fire). A custom kid
/// role-mapped to TH is also accepted (treated as TH). Probe-confirmed: <c>probe_tr_rolemapped_kid</c>
/// (/MyTH → TH in /RoleMap) → PASS. Implementation: only kids whose <c>StandardType != null</c>
/// are checked against the allowed set.</para>
///
/// <para><strong>FP-safety — empty kid list.</strong> When a Table/TR/THead/TBody/TFoot has no
/// StructElem kids (only MCIDs or no /K at all), <c>node.Children</c> is empty and the kid-type
/// rule is vacuously satisfied — no fire. Probe-confirmed: <c>probe_tr_empty_kids</c> and
/// <c>probe_table_no_elem_kids</c> → PASS.</para>
///
/// <para>Cross-validated against veraPDF 1.30.2 for each rule (violating + compliant fixture).</para>
/// </remarks>
internal sealed class UaTableContainmentRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-3-4-5-6-7-8-9-10-36-37-38";

    public string Clause => "ISO 14289-1:2014, 7.2";

    // Allowed kids for Table (7.2-3)
    private static readonly HashSet<string> TableKids =
        new(["TR", "THead", "TBody", "TFoot", "Caption"], StringComparer.Ordinal);

    // Allowed kids for TR (7.2-10)
    private static readonly HashSet<string> TrKids =
        new(["TH", "TD"], StringComparer.Ordinal);

    // Allowed kids for THead/TBody/TFoot (7.2-36/37/38)
    private static readonly HashSet<string> TheadTbodyTfootKids =
        new(["TR"], StringComparer.Ordinal);

    // Allowed parents for TR (7.2-4)
    private static readonly HashSet<string> TrParents =
        new(["Table", "THead", "TBody", "TFoot"], StringComparer.Ordinal);

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

                // 7.2-4: TR parent must be Table/THead/TBody/TFoot
                case "TR" when parentSt is null || !TrParents.Contains(parentSt):
                    Report(context, "ISO14289-1:7.2-4",
                        $"A TR structure element has parent /{parentSt ?? "<none>"} but TR requires a parent of Table, THead, TBody, or TFoot (§7.2).");
                    return;

                // 7.2-5: THead parent must be Table
                case "THead" when parentSt != "Table":
                    Report(context, "ISO14289-1:7.2-5",
                        $"A THead structure element has parent /{parentSt ?? "<none>"} but THead requires a Table parent (§7.2).");
                    return;

                // 7.2-6: TBody parent must be Table
                case "TBody" when parentSt != "Table":
                    Report(context, "ISO14289-1:7.2-6",
                        $"A TBody structure element has parent /{parentSt ?? "<none>"} but TBody requires a Table parent (§7.2).");
                    return;

                // 7.2-7: TFoot parent must be Table
                case "TFoot" when parentSt != "Table":
                    Report(context, "ISO14289-1:7.2-7",
                        $"A TFoot structure element has parent /{parentSt ?? "<none>"} but TFoot requires a Table parent (§7.2).");
                    return;

                // 7.2-8: TH parent must be TR
                case "TH" when parentSt != "TR":
                    Report(context, "ISO14289-1:7.2-8",
                        $"A TH structure element has parent /{parentSt ?? "<none>"} but TH requires a TR parent (§7.2).");
                    return;

                // 7.2-9: TD parent must be TR
                case "TD" when parentSt != "TR":
                    Report(context, "ISO14289-1:7.2-9",
                        $"A TD structure element has parent /{parentSt ?? "<none>"} but TD requires a TR parent (§7.2).");
                    return;

                // ── KID-TYPE rules ────────────────────────────────────────────────────

                // 7.2-3: Table kids must be TR/THead/TBody/TFoot/Caption (or unmapped/null)
                case "Table":
                    CheckKids(context, node, TableKids, "ISO14289-1:7.2-3",
                        "A Table structure element has a child that is not TR, THead, TBody, TFoot, or Caption (§7.2).");
                    break;

                // 7.2-10: TR kids must be TH or TD (or unmapped/null)
                case "TR":
                    CheckKids(context, node, TrKids, "ISO14289-1:7.2-10",
                        "A TR structure element has a child that is not TH or TD (§7.2).");
                    break;

                // 7.2-36: THead kids must be TR (or unmapped/null)
                case "THead":
                    CheckKids(context, node, TheadTbodyTfootKids, "ISO14289-1:7.2-36",
                        "A THead structure element has a child that is not TR (§7.2).");
                    break;

                // 7.2-37: TBody kids must be TR (or unmapped/null)
                case "TBody":
                    CheckKids(context, node, TheadTbodyTfootKids, "ISO14289-1:7.2-37",
                        "A TBody structure element has a child that is not TR (§7.2).");
                    break;

                // 7.2-38: TFoot kids must be TR (or unmapped/null)
                case "TFoot":
                    CheckKids(context, node, TheadTbodyTfootKids, "ISO14289-1:7.2-38",
                        "A TFoot structure element has a child that is not TR (§7.2).");
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
