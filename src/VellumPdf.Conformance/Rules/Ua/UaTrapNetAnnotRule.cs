// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.18.2 (TrapNet annotations). A TrapNet annotation (<c>/Subtype /TrapNet</c>)
/// shall not appear in a conforming PDF/UA-1 file unless it is exempt from the requirement:
/// the annotation's <c>/Rect</c> lies entirely outside the effective crop box
/// (<c>isOutsideCropBox == true</c>) or the Hidden flag is set (<c>(F &amp; 2) == 2</c>).
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.18.2 (PDTrapNetAnnot predicate:
/// <c>isOutsideCropBox == true || (F &amp; 2) == 2</c>) and empirically validated against
/// veraPDF 1.30.2 (clause 7.18.2, testNumber 1). Clean-room: derived from the specification
/// text and the veraPDF profile, not from any third-party implementation.
/// <para>
/// A visible TrapNet annotation inside the crop box always fails regardless of whether it has
/// an appearance stream or other entries — the annotation type itself is the violation.
/// A TrapNet with the Hidden flag set (<c>/F 2</c>) passes the check even inside the crop box;
/// an annotation whose <c>/Rect</c> is entirely outside the page's effective crop box
/// (<c>/CropBox</c> or, when absent, <c>/MediaBox</c>) also passes. Both exemptions were
/// confirmed against veraPDF 1.30.2.
/// </para>
/// </remarks>
internal sealed class UaTrapNetAnnotRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.18.2-1";

    public string Clause => "ISO 14289-1:2014, 7.18.2";

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
                if (subtype != "TrapNet")
                    continue;

                // The annotation is exempt when hidden or outside the crop box.
                if (UaAnnotationHelper.IsExempt(context, annot, page))
                    continue;

                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "A TrapNet annotation (/Subtype /TrapNet) is present and visible inside the crop box. "
                    + "PDF/UA-1 forbids TrapNet annotations unless they are hidden (F & 2) or entirely "
                    + "outside the page's crop box (ISO 14289-1:2014, 7.18.2).");
            }
        }
    }
}
