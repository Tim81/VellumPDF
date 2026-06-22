// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.3 and §7.7 alternate-text rules:
/// <list type="bullet">
///   <item>7.3-1 (SEFigure)  — a Figure element must have a non-empty /Alt OR an /ActualText entry.</item>
///   <item>7.7-1 (SEFormula) — a Formula element must have a non-empty /Alt OR an /ActualText entry.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>Predicate (both clauses):</strong>
/// <c>(Alt != null &amp;&amp; Alt != '') || ActualText != null</c>.
/// The /Alt value is a PDFDocEncoding / UTF-16BE text string on the StructElem dict.
/// The /ActualText key must be present; its value may be an empty string — veraPDF accepts
/// an empty /ActualText as satisfying the condition (presence alone is sufficient).
/// Confirmed by direct probing against veraPDF 1.30.2:
/// <list type="bullet">
///   <item><c>probe_figure_empty_alt</c>: /Alt () with no /ActualText → fires 7.3-1 (exit 1).</item>
///   <item><c>probe_figure_actualtext_empty</c>: /ActualText () with no /Alt → PASS (exit 0).</item>
///   <item><c>probe_figure_actualtext_nonempty</c>: /ActualText (text) → PASS (exit 0).</item>
///   <item><c>probe_figure_good_alt</c>: /Alt (desc) → PASS (exit 0).</item>
///   <item><c>probe_figure_no_alt</c>: neither → fires 7.3-1 (exit 1).</item>
///   <item><c>probe_formula_good_alt</c>: /Alt (desc) → PASS (exit 0).</item>
///   <item><c>probe_formula_no_alt</c>: neither → fires 7.7-1 (exit 1).</item>
/// </list>
/// </para>
/// <para><strong>FP-safety — role-mapped types.</strong> The check keys on
/// <see cref="StructureTreeNode.StandardType"/> (the role-map-resolved type), not the raw /S value.
/// An element with /S /MyFigure role-mapped to Figure is subject to 7.3-1.</para>
/// <para><strong>FP-safety — null StandardType.</strong> Elements with an unknown/unmapped /S are
/// skipped (StandardType == null); only elements that resolve to exactly "Figure" or "Formula"
/// are checked.</para>
/// <para>Cross-validated against veraPDF 1.30.2 for each probe (violating + compliant fixture).</para>
/// </remarks>
internal sealed class UaAltTextRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.3-1-7.7-1";
    public string Clause => "ISO 14289-1:2014, 7.3 / 7.7";

    private static readonly PdfName _alt = new("Alt");
    private static readonly PdfName _actualText = new("ActualText");

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return;

        foreach (var node in tree.AllNodes)
        {
            var st = node.StandardType;
            if (st is not ("Figure" or "Formula"))
                continue;

            var dict = node.Dict;

            // /ActualText key present (any value, including empty string) → passes.
            // The raw dict entry being present at all is enough; an empty literal/hex string satisfies.
            var actualTextRaw = dict.Get(_actualText);
            if (actualTextRaw is not null)
                continue;

            // /Alt present and non-empty (at least one byte) → passes.
            var altObj = context.Resolve(dict.Get(_alt));
            var altIsNonEmpty = altObj switch
            {
                PdfLiteralString s => s.Bytes.Length > 0,
                PdfHexString s => s.Bytes.Length > 0,
                _ => false,
            };

            if (altIsNonEmpty)
                continue;

            // Neither condition satisfied → violation.
            if (st == "Figure")
            {
                context.Report(
                    "ISO14289-1:7.3-1",
                    "ISO 14289-1:2014, 7.3",
                    PreflightSeverity.Error,
                    "A Figure structure element has neither a non-empty /Alt nor an /ActualText entry (§7.3).");
            }
            else // Formula
            {
                context.Report(
                    "ISO14289-1:7.7-1",
                    "ISO 14289-1:2014, 7.7",
                    PreflightSeverity.Error,
                    "A Formula structure element has neither a non-empty /Alt nor an /ActualText entry (§7.7).");
            }
        }
    }
}
