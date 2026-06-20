// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Graphics;

/// <summary>
/// ISO 19005-2 §6.2.8 (Images) and §6.2.9 (PostScript XObjects). A conforming file shall not
/// contain a PostScript XObject — an external object with <c>/Subtype /PS</c> (§6.2.9) — and an
/// Image XObject that carries an <c>/Interpolate</c> entry shall set it to <see langword="false"/>
/// (§6.2.8): image interpolation is not permitted in PDF/A-2.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.8 and 6.2.9. Clean-room: derived from the specification
/// text, not from any third-party validation profile.
/// <para>
/// Scoped to XObjects the page actually paints with a <c>Do</c> operator (determined by
/// <see cref="ContentStreamUsage"/>). An XObject that is present in <c>/Resources</c> but never
/// drawn is not part of the rendered document and is not a violation — matching veraPDF, which
/// validates the content-usage graph rather than every resource merely present (issues #127, #128).
/// Cross-checked against veraPDF 1.x: a drawn PostScript XObject fails clause 6.2.9-3 and a drawn
/// image with <c>/Interpolate true</c> fails clause 6.2.8-3, while the same objects present but
/// undrawn both pass.
/// </para>
/// <para>
/// This slice inspects XObjects drawn from a page's own or inherited <c>/Resources /XObject</c>.
/// XObjects reached only from within another form XObject, a tiling pattern, a Type 3 glyph
/// procedure, or an annotation appearance stream are deferred to the later resource-graph slice.
/// The sibling forbidden keys in these clauses (image <c>/Alternates</c> and <c>/OPI</c>;
/// form-XObject <c>/OPI</c>, <c>/PS</c>, <c>/Subtype2 /PS</c>, and reference XObjects via
/// <c>/Ref</c>) are deferred to their own rule.
/// </para>
/// </remarks>
internal sealed class ForbiddenXObjectRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.9-postscript-xobject";

    public string Clause => "ISO 19005-2:2011, 6.2.9";

    private static readonly PdfName _xobject = new("XObject");
    private static readonly PdfName _interpolate = new("Interpolate");

    public void Evaluate(PreflightContext context)
    {
        // An XObject may be drawn on several pages; check each one only once.
        var checkedXObjects = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(_xobject)) is not PdfDictionary xobjects)
                continue;

            // §6.2.8/§6.2.9 constrain the XObjects that are actually painted. An XObject present in
            // the resource dictionary but never invoked by a `Do` operator is not rendered, so scope
            // to the drawn ones (matching veraPDF — see remarks).
            var drawn = ContentStreamUsage.Analyze(context, page).DrawnXObjects;

            foreach (var entry in xobjects.Entries)
            {
                if (!drawn.Contains(entry.Key.Value))
                    continue;
                if (entry.Value is PdfIndirectReference r && !checkedXObjects.Add(r.ObjectNumber))
                    continue;
                if (context.ResolveStream(entry.Value) is { } stream)
                    CheckXObject(context, stream.Dictionary);
            }
        }
    }

    private void CheckXObject(PreflightContext context, PdfDictionary xobject)
    {
        var subtype = (context.Resolve(xobject.Get(PdfName.Subtype)) as PdfName)?.Value;
        switch (subtype)
        {
            case "PS":
                context.Report(
                    "ISO19005-2:6.2.9-postscript-xobject",
                    "ISO 19005-2:2011, 6.2.9",
                    PreflightSeverity.Error,
                    "The document draws a PostScript XObject (/Subtype /PS), which is not permitted in PDF/A-2.");
                break;
            case "Image" when context.Resolve(xobject.Get(_interpolate)) is PdfBoolean { Value: true }:
                context.Report(
                    "ISO19005-2:6.2.8-image-interpolate",
                    "ISO 19005-2:2011, 6.2.8",
                    PreflightSeverity.Error,
                    "An Image XObject sets /Interpolate to true; image interpolation is not permitted in PDF/A-2.");
                break;
        }
    }
}
