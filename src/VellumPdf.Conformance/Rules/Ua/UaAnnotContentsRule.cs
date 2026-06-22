// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.18.1 testNumber 2 — alternate-text requirement for non-Widget annotations.
/// Every visible, non-Widget annotation must have either a non-empty <c>/Contents</c> entry or a
/// non-empty <c>/Alt</c> entry (either on the annotation itself or on its <em>direct</em> enclosing
/// structure element, reached via the annotation's <c>/StructParent</c> → the StructTreeRoot's
/// <c>/ParentTree</c>).
/// </summary>
/// <remarks>
/// veraPDF 1.30.2 predicate (7.18.1, testNumber 2):
/// <code>
/// Subtype == 'Widget' || isOutsideCropBox == true || (F &amp; 2) == 2
///    || (Contents != null &amp;&amp; Contents != '')
///    || (Alt != null &amp;&amp; Alt != '')
/// </code>
/// where <c>Alt</c> is the <c>/Alt</c> entry of the <em>direct</em> enclosing structure element
/// (not ancestors). This was confirmed empirically against veraPDF 1.30.2:
/// <list type="bullet">
///   <item>An annotation with no /Contents, no annotation-level /Alt, but whose DIRECT enclosing
///     struct element has a non-empty /Alt does NOT trigger testNumber 2.</item>
///   <item>An annotation whose ANCESTOR (not the direct struct elem) has /Alt but the direct struct
///     elem does not DOES trigger testNumber 2 — veraPDF does not walk ancestors.</item>
///   <item>An annotation with no /StructParent (no struct binding) and no /Contents triggers
///     testNumber 2.</item>
/// </list>
/// <para>
/// The rule now resolves the struct-element /Alt via the B5 ParentTree index (Batch B5) and no
/// longer skips struct-bound annotations. 7.18.1-2 is fully Implemented.
/// </para>
/// <para>
/// This rule does NOT duplicate 7.18.5-2 (the Link-specific Contents requirement). Both rules
/// fire independently when a Link annotation lacks /Contents — consistent with veraPDF 1.30.2
/// behaviour observed during probe testing.
/// </para>
/// <para>
/// Clean-room: derived from ISO 14289-1:2014 §7.18.1 and the veraPDF 1.30.2 profile predicates,
/// empirically validated. Not derived from any third-party implementation.
/// </para>
/// </remarks>
internal sealed class UaAnnotContentsRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.18.1-2";

    public string Clause => "ISO 14289-1:2014, 7.18.1";

    private static readonly PdfName _contents = new("Contents");
    private static readonly PdfName _alt = new("Alt");
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

                // Widget annotations are unconditionally exempt from this requirement.
                if (subtype == "Widget")
                    continue;

                // Exempt: hidden (F&2) or entirely outside the crop box.
                if (UaAnnotationHelper.IsExempt(context, annot, page))
                    continue;

                // Check the annotation's own /Contents and /Alt first (fast path, no tree lookup).
                if (HasNonEmptyString(context, annot, _contents) || HasNonEmptyString(context, annot, _alt))
                    continue;

                // Resolve the DIRECT enclosing structure element's /Alt via the ParentTree index.
                // veraPDF checks the direct struct elem's /Alt only (not ancestors — confirmed
                // empirically: a struct elem without /Alt whose parent carries /Alt still fires).
                // If /StructParent is absent or the ParentTree lookup returns null, structElemAlt
                // stays false — the annotation has no structure-provided alt text, and we proceed
                // to the violation report.
                if (context.Resolve(annot.Get(_structParent)) is PdfInteger spInt)
                {
                    var parentNode = tree.StructParentOf((int)spInt.Value);
                    if (parentNode is not null && HasNonEmptyString(context, parentNode.Dict, _alt))
                        continue; // struct element provides the /Alt — requirement satisfied
                }

                var label = subtype is null ? "An annotation" : $"A /{subtype} annotation";
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    $"{label} visible inside the crop box has no non-empty /Contents or /Alt entry "
                    + "(on the annotation itself or on its enclosing structure element). "
                    + "PDF/UA-1 requires every non-Widget annotation to carry an alternate text "
                    + "description in /Contents or /Alt (ISO 14289-1:2014, 7.18.1, testNumber 2).");
            }
        }
    }

    private static bool HasNonEmptyString(PreflightContext context, PdfDictionary dict, PdfName key)
    {
        var raw = context.Resolve(dict.Get(key));
        return raw switch
        {
            PdfLiteralString s => s.Bytes.Length > 0,
            PdfHexString s => s.Bytes.Length > 0,
            _ => false,
        };
    }
}
