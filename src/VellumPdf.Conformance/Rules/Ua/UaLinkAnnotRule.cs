// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.18.5 (Link annotations). A Link annotation (<c>/Subtype /Link</c>) that is
/// visible (not hidden and not outside the crop box) shall have a non-empty <c>/Contents</c>
/// entry providing an alternate text description.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.18.5 (PDLinkAnnot predicate for testNumber 2:
/// <c>(Contents != null &amp;&amp; Contents != '') || isOutsideCropBox == true || (F &amp; 2) == 2</c>)
/// and empirically validated against veraPDF 1.30.2 (clause 7.18.5, testNumber 2). Clean-room:
/// derived from the specification text and the veraPDF profile, not from any third-party implementation.
/// <para>
/// This rule implements testNumber 2 only (the alt-text / Contents requirement). testNumber 1
/// is a structure-tree rule (the Link annotation must be linked to the structure tree) that
/// requires the tagged-content walker — it is deferred.
/// </para>
/// <para>
/// A Link annotation with a non-empty <c>/Contents</c> satisfies the requirement regardless of
/// the annotation rectangle. An annotation with <c>/Contents</c> set to the empty string is
/// treated the same as absent (veraPDF behaviour confirmed empirically).
/// Both the Hidden exemption and the outside-crop-box exemption were confirmed against veraPDF 1.30.2:
/// a hidden Link (F=2) and a Link whose rect is entirely outside the MediaBox both pass 7.18.5-2.
/// </para>
/// </remarks>
internal sealed class UaLinkAnnotRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.18.5-2";

    public string Clause => "ISO 14289-1:2014, 7.18.5";

    private static readonly PdfName _contents = new("Contents");

    public void Evaluate(PreflightContext context)
    {
        foreach (var page in context.EnumeratePages())
        {
            if (context.Resolve(page.Get(PdfName.Annots)) is not PdfArray annots)
                continue;
            for (var i = 0; i < annots.Count; i++)
            {
                if (context.Resolve(annots[i]) is not PdfDictionary annot)
                    continue;
                var subtype = (context.Resolve(annot.Get(PdfName.Subtype)) as PdfName)?.Value;
                if (subtype != "Link")
                    continue;

                // Exempt: hidden or entirely outside the crop box.
                if (UaAnnotationHelper.IsExempt(context, annot, page))
                    continue;

                // Require a non-empty /Contents entry.
                if (!HasNonEmptyString(context, annot, _contents))
                {
                    context.Report(
                        RuleId, Clause, PreflightSeverity.Error,
                        "A Link annotation (/Subtype /Link) visible inside the crop box lacks a non-empty "
                        + "/Contents entry. PDF/UA-1 requires Link annotations to carry an alternate text "
                        + "description in /Contents (ISO 14289-1:2014, 7.18.5).");
                }
            }
        }
    }

    // Returns true when the dictionary carries a non-null, non-empty string under key.
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
