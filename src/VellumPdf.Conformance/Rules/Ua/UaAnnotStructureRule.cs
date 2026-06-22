// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.18 annotation↔structure-binding rules. Four clauses share a single
/// rule class because they all resolve the same <c>structParentStandardType</c> from the
/// annotation's <c>/StructParent</c> integer → the StructTreeRoot's <c>/ParentTree</c> number
/// tree → the enclosing structure element node.
/// </summary>
/// <remarks>
/// Implemented clauses (all via the B1 structure-tree walker + B5 ParentTree index):
/// <list type="bullet">
///   <item><b>7.18.1-1</b> (PDAnnot): Non-Widget/Link/PrinterMark annotations must be nested
///     within an <c>Annot</c> structure tag (<see cref="RuleIdAnnot"/>).</item>
///   <item><b>7.18.4-1</b> (PDWidgetAnnot): Widget annotations must be nested within a
///     <c>Form</c> tag (<see cref="RuleIdWidget"/>).</item>
///   <item><b>7.18.5-1</b> (PDLinkAnnot): Link annotations must be nested within a
///     <c>Link</c> tag (<see cref="RuleIdLink"/>).</item>
///   <item><b>7.18.8-1</b> (PDPrinterMarkAnnot): PrinterMark annotations must NOT be in
///     the structure tree at all — they must be artifacts. Fires when a visible PrinterMark
///     HAS a structure binding (<see cref="RuleIdPrinterMark"/>).</item>
/// </list>
///
/// <para>
/// <b>structParentStandardType</b>: the role-mapped <see cref="StructureTreeNode.StandardType"/>
/// of the node returned by <see cref="StructureTree.StructParentOf"/>; null when the annotation
/// has no <c>/StructParent</c>, when the key is absent from the <c>/ParentTree</c>, or when the
/// tree is malformed. Null is the FP-safe result for 7.18.1-1/4-1/5-1 (they require a positive
/// "structParentStandardType == target" to pass); a parse failure is treated as "unknown" and
/// the rule fires because null ≠ target. However, 7.18.8-1 is the inverse (fires when binding
/// is PRESENT), so it only fires when the annotation has a <c>/StructParent</c> whose value IS
/// a resolvable integer AND resolves to a non-null structParentType (the raw /S name, per the
/// veraPDF predicate which uses structParentType not structParentStandardType).
/// </para>
///
/// <para>
/// Exemptions (reusing <see cref="UaAnnotationHelper"/>): hidden (<c>F &amp; 2</c>) and
/// outside-crop-box annotations are exempt from all four clauses.
/// </para>
///
/// <para>
/// Verified against veraPDF 1.30.2 probe fixtures:
/// <list type="bullet">
///   <item>Text (visible, no /StructParent) → 7.18.1-1 fires.</item>
///   <item>Text (visible, /StructParent → /S /Annot) → passes.</item>
///   <item>Text (visible, /StructParent → /S /P) → 7.18.1-1 fires.</item>
///   <item>Text (visible, role-mapped /MyAnnot→Annot) → passes.</item>
///   <item>Widget (visible, /StructParent → /S /Form) → passes.</item>
///   <item>Widget (visible, /StructParent → /S /Annot) → 7.18.4-1 fires.</item>
///   <item>Widget (visible, no /StructParent) → 7.18.4-1 fires.</item>
///   <item>Link (visible, /StructParent → /S /Link) → passes.</item>
///   <item>Link (visible, /StructParent → /S /Annot) → 7.18.5-1 fires.</item>
///   <item>Link (visible, no /StructParent) → 7.18.5-1 fires.</item>
///   <item>PrinterMark (visible, no /StructParent) → passes 7.18.8-1.</item>
///   <item>PrinterMark (visible, /StructParent present) → 7.18.8-1 fires.</item>
///   <item>PrinterMark (hidden F=2, /StructParent present) → passes.</item>
///   <item>Text (hidden F=2, no /StructParent) → passes.</item>
/// </list>
/// </para>
/// <para>
/// Clean-room: derived from ISO 14289-1:2014 §7.18 and the veraPDF 1.30.2 profile predicates,
/// empirically validated. Not derived from any third-party implementation.
/// </para>
/// </remarks>
internal sealed class UaAnnotStructureRule : IConformanceRule
{
    // Rule IDs used for reporting — one per clause so results are individually addressable.
    public const string RuleIdAnnot = "ISO14289-1:7.18.1-1";
    public const string RuleIdWidget = "ISO14289-1:7.18.4-1";
    public const string RuleIdLink = "ISO14289-1:7.18.5-1";
    public const string RuleIdPrinterMark = "ISO14289-1:7.18.8-1";

    // IConformanceRule.RuleId is used for rule registration; we return the first of the four.
    public string RuleId => RuleIdAnnot;

    public string Clause => "ISO 14289-1:2014, 7.18";

    private static readonly PdfName _structParent = new("StructParent");

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);

        foreach (var page in context.EnumeratePages())
        {
            if (context.Resolve(page.Get(PdfName.Annots)) is not PdfArray annots)
                continue;

            for (var i = 0; i < annots.Count; i++)
            {
                if (context.Resolve(annots[i]) is not PdfDictionary annot)
                    continue;

                var subtype = (context.Resolve(annot.Get(PdfName.Subtype)) as PdfName)?.Value;

                // Exempt: hidden (F&2) or entirely outside the crop box — all §7.18 rules share
                // these two exemptions, verified against veraPDF 1.30.2.
                if (UaAnnotationHelper.IsExempt(context, annot, page))
                    continue;

                // Resolve the annotation's /StructParent key → enclosing StructElem node.
                // This is the FP-critical path: we must distinguish three cases:
                //   (a) /StructParent absent or not an integer → structParentKey = null
                //       (annotation definitely has no struct binding)
                //   (b) /StructParent is an integer but the node cannot be found in the index →
                //       "unknown" (malformed or unresolvable) — treated as null for FP safety on
                //       7.18.8-1 (don't fire "has binding" when we can't prove it)
                //   (c) /StructParent is an integer and the node IS found → reliable result
                int? structParentKey = null;
                if (context.Resolve(annot.Get(_structParent)) is PdfInteger spInt)
                    structParentKey = (int)spInt.Value;

                StructureTreeNode? parentNode = null;
                if (structParentKey.HasValue)
                    parentNode = tree.StructParentOf(structParentKey.Value);

                // The role-mapped standard type of the enclosing struct elem (null = no binding
                // or unresolvable).
                var structParentStdType = parentNode?.StandardType;
                // The raw /S name (used only by 7.18.8-1, which uses structParentType in its
                // veraPDF predicate — the raw name, not the standard type).
                var structParentRawType = parentNode?.RawType;

                EvaluateAnnot(context, annot, page, subtype, structParentStdType,
                    structParentKey, structParentRawType);
            }
        }
    }

    // ── Per-annotation predicate checks ─────────────────────────────────────────────────────────

    private static void EvaluateAnnot(
        PreflightContext context,
        PdfDictionary annot,
        PdfDictionary page,
        string? subtype,
        string? structParentStdType,
        int? structParentKey,
        string? structParentRawType)
    {
        switch (subtype)
        {
            case "Widget":
                // 7.18.4-1: Widget must be nested in a Form tag.
                // veraPDF predicate: structParentStandardType == 'Form' || isOutsideCropBox || (F&2)==2
                // Both exemptions already checked above; fire when structParentStdType != 'Form'.
                if (structParentStdType != "Form")
                {
                    context.Report(
                        RuleIdWidget, "ISO 14289-1:2014, 7.18.4", PreflightSeverity.Error,
                        "A Widget annotation is not nested within a Form structure tag. "
                        + $"PDF/UA-1 §7.18.4 requires Widget annotations to be enclosed in a Form tag; "
                        + $"the enclosing structure element has standard type: {structParentStdType ?? "null"} "
                        + "(ISO 14289-1:2014, 7.18.4-1).");
                }
                break;

            case "Link":
                // 7.18.5-1: Link must be nested in a Link tag.
                // veraPDF predicate: structParentStandardType == 'Link' || isOutsideCropBox || (F&2)==2
                if (structParentStdType != "Link")
                {
                    context.Report(
                        RuleIdLink, "ISO 14289-1:2014, 7.18.5", PreflightSeverity.Error,
                        "A Link annotation is not nested within a Link structure tag. "
                        + $"PDF/UA-1 §7.18.5 requires Link annotations to be enclosed in a Link tag; "
                        + $"the enclosing structure element has standard type: {structParentStdType ?? "null"} "
                        + "(ISO 14289-1:2014, 7.18.5-1).");
                }
                break;

            case "PrinterMark":
                // 7.18.8-1: PrinterMark must NOT be in the structure tree (must be artifact).
                // veraPDF predicate: structParentType == null || isOutsideCropBox || (F&2)==2
                // NOTE: veraPDF uses structParentType (raw /S name), not structParentStandardType.
                // We only fire when we can positively confirm a struct binding:
                //   - structParentKey must be present (annotation has /StructParent integer)
                //   - AND the parentNode must be non-null (we found it in the tree)
                //   - AND it has a non-null rawType (it has an /S name, i.e. is a real struct elem)
                // If structParentKey is present but parentNode is null (unresolvable), we do NOT
                // fire — that is the FP-safe choice (treat as "unknown", not "confirmed binding").
                if (structParentKey.HasValue && structParentRawType is not null)
                {
                    context.Report(
                        RuleIdPrinterMark, "ISO 14289-1:2014, 7.18.8", PreflightSeverity.Error,
                        "A PrinterMark annotation is included in the logical structure tree. "
                        + "PDF/UA-1 §7.18.8 requires PrinterMark annotations to be treated as "
                        + "Incidental Artifacts (not bound into the structure tree) "
                        + "(ISO 14289-1:2014, 7.18.8-1).");
                }
                break;

            default:
                // 7.18.1-1: Every other annotation (not Widget, not Link, not PrinterMark) must
                // be nested within an Annot tag.
                // veraPDF predicate:
                //   Subtype == 'Widget' || Subtype == 'PrinterMark' || Subtype == 'Link'
                //   || isOutsideCropBox || (F&2)==2 || structParentStandardType == 'Annot'
                // The Subtype cases and the two exemptions are already dispatched; fire when
                // structParentStdType != 'Annot'.
                if (structParentStdType != "Annot")
                {
                    var label = subtype is null ? "An annotation" : $"A /{subtype} annotation";
                    context.Report(
                        RuleIdAnnot, "ISO 14289-1:2014, 7.18.1", PreflightSeverity.Error,
                        $"{label} is not nested within an Annot structure tag. "
                        + "PDF/UA-1 §7.18.1 requires annotations (other than Widget, Link, or "
                        + $"PrinterMark) to be enclosed in an Annot tag; the enclosing structure "
                        + $"element has standard type: {structParentStdType ?? "null"} "
                        + "(ISO 14289-1:2014, 7.18.1-1).");
                }
                break;
        }
    }
}
