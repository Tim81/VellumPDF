// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.4.4 heading-level rules:
/// <list type="bullet">
///   <item>7.4.4-1 (PDStructElem) — an element may have AT MOST ONE child whose standard type is "H".</item>
///   <item>7.4.4-2 (SEH)          — an H element is non-conformant when the document also uses H1–H6 (Hn).</item>
///   <item>7.4.4-3 (SEHn)         — an H1–H6 element is non-conformant when the document also uses H.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>7.4.4-1 predicate:</strong>
/// <c>kidsStandardTypes.filter(t =&gt; t == 'H').length &lt;= 1</c>.
/// Only children whose standard type is exactly "H" count; H1–H6 children do NOT count.
/// Confirmed by probing against veraPDF 1.30.2:
/// <list type="bullet">
///   <item><c>probe_two_H_kids</c>: Document with two H children → fires 7.4.4-1 (exit 1).</item>
///   <item><c>probe_one_H_one_H1</c>: Document with one H child and one H1 child → does NOT fire 7.4.4-1
///     (but does fire 7.4.4-2 and 7.4.4-3 because both H and Hn are present).</item>
/// </list>
/// </para>
/// <para><strong>7.4.4-2 / 7.4.4-3 predicate (mixing weak and strong heading structure):</strong>
/// A document must be either weakly structured (uses H only) or strongly structured (uses H1–H6 only).
/// <list type="bullet">
///   <item>7.4.4-2 fires on every H element when <c>usesHn == true</c> (both H and Hn present).</item>
///   <item>7.4.4-3 fires on every Hn element when <c>usesH == true</c> (both H and Hn present).</item>
/// </list>
/// In practice: if both H and any of H1–H6 exist in the document, both 7.4.4-2 and 7.4.4-3 fire.
/// Confirmed by probing against veraPDF 1.30.2:
/// <list type="bullet">
///   <item><c>probe_H_and_H1</c>: Document with H and H1 → fires 7.4.4-2 AND 7.4.4-3 (exit 1).</item>
///   <item><c>probe_one_H_one_H1</c>: same document layout → fires 7.4.4-2 AND 7.4.4-3 (exit 1).</item>
///   <item><c>probe_only_Hn</c>: H1 + H2, no H → PASS for both (exit 0).</item>
///   <item><c>probe_only_H</c>: H only, no Hn → PASS for both (exit 0).</item>
/// </list>
/// </para>
/// <para><strong>FP-safety — role-mapped types.</strong> Checks key on
/// <see cref="StructureTreeNode.StandardType"/> (role-map-resolved). An element /S /MyH
/// role-mapped to H is subject to 7.4.4-2 and to being counted under 7.4.4-1.</para>
/// <para><strong>FP-safety — null StandardType.</strong> Elements with an unknown/unmapped /S
/// are skipped for all three checks.</para>
/// <para>Cross-validated against veraPDF 1.30.2 for each probe (violating + compliant fixture).</para>
/// </remarks>
internal sealed class UaHeadingRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.4.4-1-2-3";
    public string Clause => "ISO 14289-1:2014, 7.4.4";

    private static readonly HashSet<string> HnTypes =
        new(["H1", "H2", "H3", "H4", "H5", "H6"], StringComparer.Ordinal);

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return;

        // --- Pass 1: compute document-wide usesH / usesHn for 7.4.4-2 / 7.4.4-3 ---
        var usesH = false;
        var usesHn = false;
        foreach (var node in tree.AllNodes)
        {
            var st = node.StandardType;
            if (st is null) continue;
            if (st == "H") usesH = true;
            else if (HnTypes.Contains(st)) usesHn = true;
            if (usesH && usesHn) break; // early-out: both flags set
        }

        // --- Pass 2: per-element checks ---
        foreach (var node in tree.AllNodes)
        {
            var st = node.StandardType;
            if (st is null)
                continue;

            // 7.4.4-1: count H (not Hn) children; more than one is a violation.
            var hChildCount = 0;
            foreach (var kid in node.Children)
            {
                if (kid.StandardType == "H")
                    hChildCount++;
            }
            if (hChildCount > 1)
            {
                context.Report(
                    "ISO14289-1:7.4.4-1",
                    "ISO 14289-1:2014, 7.4.4",
                    PreflightSeverity.Error,
                    $"A structure element has {hChildCount} children with standard type H; at most one H child is permitted (§7.4.4).");
            }

            // 7.4.4-2: an H element is non-conformant when the document also uses Hn (H1–H6).
            if (st == "H" && usesHn)
            {
                context.Report(
                    "ISO14289-1:7.4.4-2",
                    "ISO 14289-1:2014, 7.4.4",
                    PreflightSeverity.Error,
                    "A document uses both the H (weakly structured) and Hn (H1–H6, strongly structured) heading types; H and Hn must not be mixed (§7.4.4).");
            }

            // 7.4.4-3: an Hn element is non-conformant when the document also uses H.
            if (HnTypes.Contains(st) && usesH)
            {
                context.Report(
                    "ISO14289-1:7.4.4-3",
                    "ISO 14289-1:2014, 7.4.4",
                    PreflightSeverity.Error,
                    "A document uses both the Hn (H1–H6, strongly structured) and H (weakly structured) heading types; H and Hn must not be mixed (§7.4.4).");
            }
        }
    }
}
