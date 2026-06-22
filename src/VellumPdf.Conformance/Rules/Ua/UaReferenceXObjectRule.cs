// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.20 (XObjects). A Form XObject (<c>/Subtype /Form</c>) drawn in a conforming
/// PDF/UA-1 file shall not be a reference XObject (it shall not contain a <c>/Ref</c> entry).
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.20 (PDXForm predicate: <c>containsRef == false</c>) and
/// empirically validated against veraPDF 1.30.2 (clause 7.20, testNumber 1). Clean-room: derived
/// from the specification text and the veraPDF profile, not from any third-party implementation.
/// <para>
/// Reference XObjects embed or reference external content (ISO 32000-1:2008, 8.10.4). They are
/// also forbidden by PDF/A-2 (<c>ForbiddenXObjectRule</c> §6.2.9-2); this UA rule
/// duplicates that detection under the ISO 14289-1:2014 §7.20 clause, for documents validated as
/// PDF/UA-1 without also being PDF/A-2.
/// </para>
/// <para>
/// Scoped to Form XObjects that are actually drawn via the <c>Do</c> operator (matching veraPDF,
/// which validates the content-usage graph). An XObject present in <c>/Resources</c> but never
/// invoked is not flagged.
/// </para>
/// </remarks>
internal sealed class UaReferenceXObjectRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.20-1";

    public string Clause => "ISO 14289-1:2014, 7.20";

    private static readonly PdfName _xobject = new("XObject");
    private static readonly PdfName _ref = new("Ref");

    public void Evaluate(PreflightContext context)
    {
        var checkedXObjects = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(_xobject)) is not PdfDictionary xobjects)
                continue;

            // Only check XObjects actually drawn by a Do operator.
            var drawn = ContentStreamUsage.Analyze(context, page).DrawnXObjects;

            foreach (var entry in xobjects.Entries)
            {
                if (!drawn.Contains(entry.Key.Value))
                    continue;
                if (entry.Value is PdfIndirectReference r && !checkedXObjects.Add(r.ObjectNumber))
                    continue;
                if (context.ResolveStream(entry.Value) is not { } stream)
                    continue;

                var subtype = (context.Resolve(stream.Dictionary.Get(PdfName.Subtype)) as PdfName)?.Value;
                if (subtype != "Form")
                    continue;

                // A /Ref entry makes this a reference XObject — forbidden by §7.20-1.
                if (stream.Dictionary.Get(_ref) is not null)
                {
                    context.Report(
                        RuleId, Clause, PreflightSeverity.Error,
                        "A Form XObject drawn by this page contains a /Ref entry, making it a reference "
                        + "XObject. Reference XObjects are not permitted in PDF/UA-1 (ISO 14289-1:2014, 7.20).");
                }
            }
        }
    }
}
