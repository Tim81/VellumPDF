// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Graphics;

/// <summary>
/// ISO 19005-2 §6.2.8 (Images) and §6.2.9 (XObjects). A conforming file shall not draw:
/// a <b>PostScript XObject</b> (<c>/Subtype /PS</c>, §6.2.9); an <b>Image XObject</b> carrying
/// <c>/Alternates</c>, <c>/OPI</c>, <c>/Interpolate true</c>, or an out-of-range
/// <c>/BitsPerComponent</c> (§6.2.8); or a <b>form XObject</b> carrying <c>/OPI</c>, <c>/PS</c>,
/// <c>/Subtype2 /PS</c>, or a reference (<c>/Ref</c>) — a reference XObject (§6.2.9).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.8 and 6.2.9. Clean-room: derived from the specification
/// text, not from any third-party validation profile.
/// <para>
/// Scoped to XObjects the page actually paints with a <c>Do</c> operator (determined by
/// <see cref="ContentStreamUsage"/>). An XObject that is present in <c>/Resources</c> but never
/// drawn is not part of the rendered document and is not a violation — matching veraPDF, which
/// validates the content-usage graph rather than every resource merely present (issues #127, #128).
/// Cross-checked against veraPDF 1.x: drawn instances of each forbidden construct fail the
/// corresponding clause while the same objects present-but-undrawn pass.
/// </para>
/// <para>
/// This slice inspects XObjects drawn from a page's own or inherited <c>/Resources /XObject</c>.
/// XObjects reached only from within another form XObject, a tiling pattern, a Type 3 glyph
/// procedure, or an annotation appearance stream are deferred to the later resource-graph slice.
/// The JPEG2000 codestream constraints (§6.2.8.3) need image-data parsing and are also deferred.
/// </para>
/// </remarks>
internal sealed class ForbiddenXObjectRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.9-postscript-xobject";

    public string Clause => "ISO 19005-2:2011, 6.2.9";

    private static readonly PdfName _xobject = new("XObject");
    private static readonly PdfName _interpolate = new("Interpolate");
    private static readonly PdfName _alternates = new("Alternates");
    private static readonly PdfName _opi = new("OPI");
    private static readonly PdfName _ps = new("PS");
    private static readonly PdfName _subtype2 = new("Subtype2");
    private static readonly PdfName _ref = new("Ref");
    private static readonly PdfName _imageMask = new("ImageMask");
    private static readonly PdfName _bitsPerComponent = new("BitsPerComponent");

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
            // §6.2.9-3: a conforming file shall not contain any PostScript XObject (/Subtype /PS).
            case "PS":
                Report(context, "6.2.9-postscript-xobject", "6.2.9",
                    "The document draws a PostScript XObject (/Subtype /PS), which is not permitted in PDF/A-2.");
                break;
            case "Image":
                CheckImage(context, xobject);
                break;
            case "Form":
                CheckForm(context, xobject);
                break;
        }
    }

    private void CheckImage(PreflightContext context, PdfDictionary image)
    {
        // §6.2.8-1 / -2: an Image dictionary shall not contain the Alternates or OPI key.
        if (image.Get(_alternates) is not null)
            Report(context, "6.2.8-image-alternates", "6.2.8",
                "An Image XObject contains the /Alternates key, which is not permitted in PDF/A-2.");
        if (image.Get(_opi) is not null)
            Report(context, "6.2.8-image-opi", "6.2.8",
                "An Image XObject contains the /OPI key, which is not permitted in PDF/A-2.");

        // §6.2.8-3: a present /Interpolate entry shall be false.
        if (context.Resolve(image.Get(_interpolate)) is PdfBoolean { Value: true })
            Report(context, "6.2.8-image-interpolate", "6.2.8",
                "An Image XObject sets /Interpolate to true; image interpolation is not permitted in PDF/A-2.");

        // §6.2.8-4 / -5: a present /BitsPerComponent shall be 1, 2, 4, 8, or 16; for an image mask
        // (/ImageMask true) it shall be 1.
        if (context.Resolve(image.Get(_bitsPerComponent)) is PdfInteger bpc)
        {
            var isMask = context.Resolve(image.Get(_imageMask)) is PdfBoolean { Value: true };
            var allowed = isMask ? bpc.Value == 1 : bpc.Value is 1 or 2 or 4 or 8 or 16;
            if (!allowed)
                Report(context, "6.2.8-image-bitspercomponent", "6.2.8",
                    $"An Image XObject has /BitsPerComponent {bpc.Value}, which is not permitted in PDF/A-2 "
                    + (isMask ? "(an image mask shall use 1)." : "(shall be 1, 2, 4, 8, or 16)."));
        }
    }

    private void CheckForm(PreflightContext context, PdfDictionary form)
    {
        // §6.2.9-1: a form XObject shall not contain the OPI key, the PS key, or a Subtype2 of PS.
        if (form.Get(_opi) is not null)
            Report(context, "6.2.9-form-opi", "6.2.9",
                "A form XObject contains the /OPI key, which is not permitted in PDF/A-2.");
        if (form.Get(_ps) is not null)
            Report(context, "6.2.9-form-ps", "6.2.9",
                "A form XObject contains the /PS key, which is not permitted in PDF/A-2.");
        if (context.Resolve(form.Get(_subtype2)) is PdfName { Value: "PS" })
            Report(context, "6.2.9-form-subtype2-ps", "6.2.9",
                "A form XObject has /Subtype2 /PS, which is not permitted in PDF/A-2.");

        // §6.2.9-2: a conforming file shall not contain a reference XObject (a form XObject with /Ref).
        if (form.Get(_ref) is not null)
            Report(context, "6.2.9-reference-xobject", "6.2.9",
                "The document draws a reference XObject (a form XObject with a /Ref key), which is not permitted in PDF/A-2.");
    }

    private static void Report(PreflightContext context, string ruleSuffix, string clauseSuffix, string message)
        => context.Report(
            $"ISO19005-2:{ruleSuffix}", $"ISO 19005-2:2011, {clauseSuffix}", PreflightSeverity.Error, message);
}
