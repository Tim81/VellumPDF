// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 table count and caption-position rules:
/// <list type="bullet">
///   <item>7.2-11 (SETable) — at most one THead child</item>
///   <item>7.2-12 (SETable) — at most one TFoot child</item>
///   <item>7.2-13 (SETable) — TFoot requires at least one TBody sibling</item>
///   <item>7.2-14 (SETable) — THead requires at least one TBody sibling</item>
///   <item>7.2-39 (SETable) — at most one Caption child</item>
///   <item>7.2-16 (SETable) — Caption may only be the FIRST or LAST child</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>FP-safety — null-StandardType kid.</strong> All predicates operate on the
/// null-omitted ordered child list: <c>node.Children.Select(c =&gt; c.StandardType).Where(t =&gt; t != null)</c>.
/// Kids whose /S is non-standard and unmapped (StandardType == null) are excluded, matching
/// veraPDF's kidsStandardTypes semantics. Probe-confirmed: a Caption at raw index 1 whose
/// only preceding sibling is an unmapped /MyCustom kid is treated as index 0 in the
/// null-omitted list → 7.2-16 does NOT fire.</para>
///
/// <para><strong>FP-safety — empty kid list.</strong> When a Table has no StructElem children,
/// all these rules are vacuously satisfied. Empty kid list → no fire.</para>
///
/// <para><strong>7.2-16 position rule.</strong> veraPDF's predicate tests
/// <c>kidsStandardTypes.indexOf('&amp;Caption&amp;') &lt; 0</c> on the '&amp;'-joined string,
/// which is false only when Caption appears with siblings on both sides (middle position).
/// Equivalent: Caption fires only when its index in the null-omitted list is neither 0 nor last.
/// Probe-confirmed: [TR, Caption, TR] → fires 7.2-16; [Caption, TR] and [TR, Caption] → pass.</para>
///
/// <para>Cross-validated against veraPDF 1.30.2 for each clause (violating + compliant fixture).</para>
/// </remarks>
internal sealed class UaTableCountRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-11-12-13-14-16-39";

    public string Clause => "ISO 14289-1:2014, 7.2";

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return;

        foreach (var node in tree.AllNodes)
        {
            if (node.StandardType != "Table")
                continue;

            // Build the null-omitted ordered kid-type list (matches veraPDF kidsStandardTypes).
            var kidTypes = node.Children
                .Select(c => c.StandardType)
                .Where(t => t != null)
                .ToList()!;

            if (kidTypes.Count == 0)
                continue;

            // 7.2-11: at most one THead
            int theadCount = kidTypes.Count(t => t == "THead");
            if (theadCount > 1)
            {
                Report(context, "ISO14289-1:7.2-11",
                    "A Table structure element has more than one THead child (§7.2-11).");
                continue;
            }

            // 7.2-12: at most one TFoot
            int tfootCount = kidTypes.Count(t => t == "TFoot");
            if (tfootCount > 1)
            {
                Report(context, "ISO14289-1:7.2-12",
                    "A Table structure element has more than one TFoot child (§7.2-12).");
                continue;
            }

            int tbodyCount = kidTypes.Count(t => t == "TBody");

            // 7.2-13: if TFoot present, TBody must also be present
            if (tfootCount > 0 && tbodyCount == 0)
            {
                Report(context, "ISO14289-1:7.2-13",
                    "A Table structure element has a TFoot child but no TBody child (§7.2-13).");
                continue;
            }

            // 7.2-14: if THead present, TBody must also be present
            if (theadCount > 0 && tbodyCount == 0)
            {
                Report(context, "ISO14289-1:7.2-14",
                    "A Table structure element has a THead child but no TBody child (§7.2-14).");
                continue;
            }

            // 7.2-39: at most one Caption
            int captionCount = kidTypes.Count(t => t == "Caption");
            if (captionCount >= 2)
            {
                Report(context, "ISO14289-1:7.2-39",
                    "A Table structure element has more than one Caption child (§7.2-39).");
                continue;
            }

            // 7.2-16: Caption (if present) may only be the first or last kid
            // Fire if any Caption is at an index that is neither 0 nor (count-1).
            if (captionCount == 1)
            {
                int last = kidTypes.Count - 1;
                int captionIdx = kidTypes.IndexOf("Caption");
                if (captionIdx != 0 && captionIdx != last)
                {
                    Report(context, "ISO14289-1:7.2-16",
                        "A Table structure element has a Caption child that is not the first or last child (§7.2-16).");
                }
            }
        }
    }

    private static void Report(PreflightContext context, string ruleId, string message)
        => context.Report(ruleId, "ISO 14289-1:2014, 7.2", PreflightSeverity.Error, message);
}
