// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Actions;

/// <summary>
/// ISO 19005-2 §6.6.1 (Actions). A PDF/A file shall not contain certain action types:
/// <c>Launch</c>, <c>Sound</c>, <c>Movie</c>, <c>ResetForm</c>, <c>ImportData</c>,
/// <c>JavaScript</c>, <c>SetOCGState</c>, <c>Rendition</c>, <c>Trans</c>, and <c>GoTo3DView</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.6.1 and ISO 32000-1:2008, 12.6. Clean-room: derived from the
/// specification text, not from any third-party validation profile. Inspects the document catalog's
/// <c>/OpenAction</c>, each annotation's <c>/A</c>, and the additional-action (<c>/AA</c>)
/// dictionaries on the catalog, pages, and annotations, following any <c>/Next</c> chain. Form-field
/// <c>/AA</c> reached only through the AcroForm field tree (not via a widget annotation) is deferred.
/// </remarks>
internal sealed class ActionRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.6.1-action";

    public string Clause => "ISO 19005-2:2011, 6.6.1";

    private static readonly PdfName _s = new("S");
    private static readonly PdfName _next = new("Next");
    private static readonly PdfName _openAction = new("OpenAction");
    private static readonly PdfName _a = new("A");
    private static readonly PdfName _aa = new("AA");

    private static readonly HashSet<string> _forbidden = new(StringComparer.Ordinal)
    {
        "Launch", "Sound", "Movie", "ResetForm", "ImportData",
        "JavaScript", "SetOCGState", "Rendition", "Trans", "GoTo3DView",
    };

    public void Evaluate(PreflightContext context)
    {
        var visited = new HashSet<int>();

        CheckAction(context, context.Catalog.Get(_openAction), visited);
        CheckAdditionalActions(context, context.Catalog.Get(_aa), visited);

        foreach (var page in context.EnumeratePages())
            CheckAdditionalActions(context, page.Get(_aa), visited);

        foreach (var annot in context.EnumerateAnnotations())
        {
            CheckAction(context, annot.Get(_a), visited);
            CheckAdditionalActions(context, annot.Get(_aa), visited);
        }
    }

    private void CheckAdditionalActions(PreflightContext context, PdfObject? aaObj, HashSet<int> visited)
    {
        if (context.Resolve(aaObj) is not PdfDictionary aa)
            return;
        foreach (var entry in aa.Entries)
            CheckAction(context, entry.Value, visited);
    }

    private void CheckAction(PreflightContext context, PdfObject? actionObj, HashSet<int> visited)
    {
        if (actionObj is PdfIndirectReference r && !visited.Add(r.ObjectNumber))
            return;
        if (context.Resolve(actionObj) is not PdfDictionary action)
            return;

        if (action.Get(_s) is PdfName s && _forbidden.Contains(s.Value))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                $"The action type /{s.Value} is not permitted in PDF/A.");
        }

        // /Next is either a single action or an array of actions executed afterwards.
        var next = context.Resolve(action.Get(_next));
        if (next is PdfDictionary)
        {
            CheckAction(context, action.Get(_next), visited);
        }
        else if (next is PdfArray array)
        {
            for (var i = 0; i < array.Count; i++)
                CheckAction(context, array[i], visited);
        }
    }
}
