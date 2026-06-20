// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Actions;

/// <summary>
/// ISO 19005-2 §6.5.1 (Actions). A PDF/A file shall not contain certain action types:
/// <c>Launch</c>, <c>Sound</c>, <c>Movie</c>, <c>ResetForm</c>, <c>ImportData</c>,
/// <c>JavaScript</c>, <c>SetState</c>, <c>NoOp</c>, <c>SetOCGState</c>, <c>Rendition</c>,
/// <c>Trans</c>, and <c>GoTo3DView</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.5.1 and ISO 32000-1:2008, 12.6. Clean-room: derived from the
/// specification text, not from any third-party validation profile. Inspects the document catalog's
/// <c>/OpenAction</c>, each annotation's <c>/A</c>, and the additional-action (<c>/AA</c>)
/// dictionaries on the catalog, pages, and annotations, following any <c>/Next</c> chain. Form-field
/// <c>/AA</c> reached only through the AcroForm field tree (not via a widget annotation) is deferred.
/// </remarks>
internal sealed class ActionRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.5.1-action";

    public string Clause => "ISO 19005-2:2011, 6.5.1";

    private const int MaxDepth = 64;

    private static readonly PdfName _s = new("S");
    private static readonly PdfName _n = new("N");
    private static readonly PdfName _next = new("Next");
    private static readonly PdfName _openAction = new("OpenAction");
    private static readonly PdfName _a = new("A");
    private static readonly PdfName _aa = new("AA");
    private static readonly PdfName _names = new("Names");
    private static readonly PdfName _javaScript = new("JavaScript");

    private static readonly HashSet<string> _forbidden = new(StringComparer.Ordinal)
    {
        "Launch", "Sound", "Movie", "ResetForm", "ImportData",
        "JavaScript", "SetState", "NoOp", "SetOCGState", "Rendition", "Trans", "GoTo3DView",
    };

    // The only named actions PDF/A-2 permits (§6.5.1, ISO 32000-1 §12.6.4.11, Table 215).
    private static readonly HashSet<string> _permittedNamedActions = new(StringComparer.Ordinal)
    {
        "NextPage", "PrevPage", "FirstPage", "LastPage",
    };

    public void Evaluate(PreflightContext context)
    {
        var visited = new HashSet<int>();

        CheckAction(context, context.Catalog.Get(_openAction), visited, 0);

        // §6.5.2-1: the document catalog shall not contain an /AA additional-actions dictionary.
        if (context.Catalog.Get(_aa) is not null)
            context.Report("ISO19005-2:6.5.2-catalog-aa", "ISO 19005-2:2011, 6.5.2", PreflightSeverity.Error,
                "The document catalog contains an /AA additional-actions dictionary, which is not permitted in PDF/A-2.");
        CheckAdditionalActions(context, context.Catalog.Get(_aa), visited);

        foreach (var page in context.EnumeratePages())
        {
            // §6.5.2-2: a page dictionary shall not contain an /AA additional-actions dictionary.
            if (page.Get(_aa) is not null)
                context.Report("ISO19005-2:6.5.2-page-aa", "ISO 19005-2:2011, 6.5.2", PreflightSeverity.Error,
                    "A page dictionary contains an /AA additional-actions dictionary, which is not permitted in PDF/A-2.");
            CheckAdditionalActions(context, page.Get(_aa), visited);
        }

        foreach (var annot in context.EnumerateAnnotations())
        {
            CheckAction(context, annot.Get(_a), visited, 0);
            CheckAdditionalActions(context, annot.Get(_aa), visited);
        }

        // Document-level JavaScript lives in the catalog /Names /JavaScript name tree.
        if (context.Resolve(context.Catalog.Get(_names)) is PdfDictionary names)
            CheckNameTreeActions(context, names.Get(_javaScript), new HashSet<int>(), visited, 0);
    }

    private void CheckAdditionalActions(PreflightContext context, PdfObject? aaObj, HashSet<int> visited)
    {
        if (context.Resolve(aaObj) is not PdfDictionary aa)
            return;
        foreach (var entry in aa.Entries)
            CheckAction(context, entry.Value, visited, 0);
    }

    private void CheckNameTreeActions(
        PreflightContext context, PdfObject? nodeObj, HashSet<int> seenNodes, HashSet<int> visited, int depth)
    {
        if (depth > MaxDepth)
            return;
        if (nodeObj is PdfIndirectReference r && !seenNodes.Add(r.ObjectNumber))
            return;
        if (context.Resolve(nodeObj) is not PdfDictionary node)
            return;

        // Leaf entries: /Names is [key value key value …] where each value is an action.
        if (context.Resolve(node.Get(_names)) is PdfArray pairs)
            for (var i = 1; i < pairs.Count; i += 2)
                CheckAction(context, pairs[i], visited, 0);

        // Intermediate nodes: recurse into /Kids.
        if (context.Resolve(node.Get(PdfName.Kids)) is PdfArray kids)
            for (var i = 0; i < kids.Count; i++)
                CheckNameTreeActions(context, kids[i], seenNodes, visited, depth + 1);
    }

    private void CheckAction(PreflightContext context, PdfObject? actionObj, HashSet<int> visited, int depth)
    {
        if (depth > MaxDepth)
            return;
        if (actionObj is PdfIndirectReference r && !visited.Add(r.ObjectNumber))
            return;
        if (context.Resolve(actionObj) is not PdfDictionary action)
            return;

        if (context.Resolve(action.Get(_s)) is PdfName s)
        {
            if (_forbidden.Contains(s.Value))
            {
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    $"The action type /{s.Value} is not permitted in PDF/A.");
            }
            else if (s.Value == "Named"
                && context.Resolve(action.Get(_n)) is PdfName named
                && !_permittedNamedActions.Contains(named.Value))
            {
                // §6.5.1-2: only NextPage/PrevPage/FirstPage/LastPage named actions are permitted.
                context.Report(
                    "ISO19005-2:6.5.1-named-action",
                    "ISO 19005-2:2011, 6.5.1",
                    PreflightSeverity.Error,
                    $"The named action /{named.Value} is not permitted in PDF/A "
                    + "(only NextPage, PrevPage, FirstPage, and LastPage are allowed).");
            }
        }

        // /Next is either a single action or an array of actions executed afterwards.
        var next = context.Resolve(action.Get(_next));
        if (next is PdfDictionary)
        {
            CheckAction(context, action.Get(_next), visited, depth + 1);
        }
        else if (next is PdfArray array)
        {
            for (var i = 0; i < array.Count; i++)
                CheckAction(context, array[i], visited, depth + 1);
        }
    }
}
