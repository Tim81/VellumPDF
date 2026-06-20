// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Forms;

/// <summary>
/// ISO 19005-2 §6.4.1 (Interactive forms) and §6.4.2 (rendering). A widget annotation and a form
/// field shall not carry an action — neither <c>/A</c> nor <c>/AA</c> (§6.4.1); the interactive
/// form dictionary's <c>/NeedAppearances</c> flag shall be absent or <see langword="false"/>
/// (§6.4.1); and the document catalog shall not contain the <c>/NeedsRendering</c> key (§6.4.2).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.4.1 and 6.4.2. Clean-room: derived from the specification
/// text, not from any third-party validation profile. The <c>/XFA</c> prohibition (§6.4.2) lives in
/// <see cref="XfaRule"/>; the digital-signature constraints (§6.4.3) need PKCS#7/ByteRange parsing
/// and are deferred.
/// </remarks>
internal sealed class InteractiveFormRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.4.1-form-action";

    public string Clause => "ISO 19005-2:2011, 6.4.1";

    private const int MaxDepth = 64;

    private static readonly PdfName _acroForm = new("AcroForm");
    private static readonly PdfName _needAppearances = new("NeedAppearances");
    private static readonly PdfName _needsRendering = new("NeedsRendering");
    private static readonly PdfName _fields = new("Fields");
    private static readonly PdfName _a = new("A");
    private static readonly PdfName _aa = new("AA");
    private static readonly PdfName _widget = new("Widget");

    public void Evaluate(PreflightContext context)
    {
        // §6.4.2-2: the document catalog shall not contain the NeedsRendering key.
        if (context.Catalog.Get(_needsRendering) is not null)
            Report(context, "6.4.2-needs-rendering", "6.4.2",
                "The document catalog contains the /NeedsRendering key, which is not permitted in PDF/A-2.");

        if (context.Resolve(context.Catalog.Get(_acroForm)) is PdfDictionary acroForm)
        {
            // §6.4.1-3: the interactive form's NeedAppearances flag shall be absent or false.
            if (context.Resolve(acroForm.Get(_needAppearances)) is PdfBoolean { Value: true })
                Report(context, "6.4.1-need-appearances", "6.4.1",
                    "The interactive form dictionary sets /NeedAppearances to true, which is not permitted in PDF/A-2.");

            // §6.4.1-2: no form field shall contain an /A or /AA action.
            if (context.Resolve(acroForm.Get(_fields)) is PdfArray fields)
                WalkFields(context, fields, new HashSet<int>(), 0);
        }

        // §6.4.1-1: no widget annotation shall contain an /A or /AA action.
        foreach (var annot in context.EnumerateAnnotations())
        {
            if (context.Resolve(annot.Get(PdfName.Subtype)) is PdfName { Value: "Widget" })
                CheckActionKeys(context, annot, "A widget annotation", "6.4.1-widget-action");
        }
    }

    private void WalkFields(PreflightContext context, PdfArray fields, HashSet<int> visited, int depth)
    {
        if (depth > MaxDepth)
            return;
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i] is PdfIndirectReference r && !visited.Add(r.ObjectNumber))
                continue;
            if (context.Resolve(fields[i]) is not PdfDictionary field)
                continue;

            // A merged field/widget is also enumerated as a /Widget annotation (§6.4.1-1); reporting
            // both matches veraPDF, which validates the field and the widget as distinct objects.
            CheckActionKeys(context, field, "A form field", "6.4.1-field-action");

            if (context.Resolve(field.Get(PdfName.Kids)) is PdfArray kids)
                WalkFields(context, kids, visited, depth + 1);
        }
    }

    private void CheckActionKeys(PreflightContext context, PdfDictionary dict, string label, string ruleSuffix)
    {
        if (dict.Get(_a) is not null)
            Report(context, ruleSuffix, "6.4.1", $"{label} contains an /A action, which is not permitted in PDF/A-2.");
        if (dict.Get(_aa) is not null)
            Report(context, ruleSuffix, "6.4.1",
                $"{label} contains an /AA additional-actions dictionary, which is not permitted in PDF/A-2.");
    }

    private static void Report(PreflightContext context, string ruleSuffix, string clauseSuffix, string message)
        => context.Report(
            $"ISO19005-2:{ruleSuffix}", $"ISO 19005-2:2011, {clauseSuffix}", PreflightSeverity.Error, message);
}
