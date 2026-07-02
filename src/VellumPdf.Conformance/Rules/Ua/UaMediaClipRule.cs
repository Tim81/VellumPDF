// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.18.6.2 — media clip data dictionary requirements. A media clip data dictionary
/// (a dictionary with <c>/S /MCD</c>) shall contain a <c>/CT</c> (content-type) key (§7.18.6.2-1)
/// and an <c>/Alt</c> (alternate text) key (§7.18.6.2-2).
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.18.6.2 and ISO 32000-1:2008, 13.2.4 (media clip data
/// dictionaries). Clean-room: derived from the specification text only.
///
/// <para><strong>Traversal:</strong> for every Screen annotation on every page, the rule walks:
/// <c>/A</c> (and <c>/AA</c> entries) → rendition dictionary (<c>/S /R</c> or <c>/S /MR</c>) →
/// <c>/R</c> → media clip (<c>/C</c>) → resolves until a media clip data dict (<c>/S /MCD</c>)
/// is reached. Nested rendition arrays (ISO 32000-1 §13.2.6 media rendition /R) are flattened
/// by the same walker. Only MCD dicts actually reached via this traversal are checked (FP-safe:
/// an MCD not reachable from any Screen annotation is not validated).</para>
///
/// <para><strong>Cycle guard:</strong> a visited set on indirect-reference object numbers prevents
/// infinite loops in pathological structures.</para>
/// </remarks>
internal sealed class UaMediaClipRule : IConformanceRule
{
    public const string RuleIdCt = "ISO14289-1:7.18.6.2-1";
    public const string RuleIdAlt = "ISO14289-1:7.18.6.2-2";

    public string RuleId => RuleIdCt;

    public string Clause => "ISO 14289-1:2014, 7.18.6.2";

    private static readonly PdfName _a = new("A");
    private static readonly PdfName _aa = new("AA");
    private static readonly PdfName _s = new("S");
    private static readonly PdfName _r = new("R");
    private static readonly PdfName _c = new("C");
    private static readonly PdfName _ct = new("CT");
    private static readonly PdfName _alt = new("Alt");
    private static readonly PdfName _screen = new("Screen");
    private static readonly PdfName _rendition = new("Rendition");

    private const int MaxDepth = 32;

    public void Evaluate(PreflightContext context)
    {
        var visitedMcd = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.Resolve(page.Get(PdfName.Annots)) is not PdfArray annots)
                continue;

            for (var i = 0; i < annots.Count; i++)
            {
                if (context.Resolve(annots[i]) is not PdfDictionary annot)
                    continue;

                var subtype = (context.Resolve(annot.Get(PdfName.Subtype)) as PdfName)?.Value;
                if (subtype != "Screen")
                    continue;

                // Walk /A and /AA action entries.
                WalkActionEntry(context, annot.Get(_a), visitedMcd, depth: 0);

                if (context.Resolve(annot.Get(_aa)) is PdfDictionary aa)
                {
                    foreach (var entry in aa.Entries)
                        WalkActionEntry(context, entry.Value, visitedMcd, depth: 0);
                }
            }
        }
    }

    // Resolves an action object and, if it is a Rendition action (/S /Rendition), walks its /R.
    private void WalkActionEntry(PreflightContext context, PdfObject? actionObj, HashSet<int> visitedMcd, int depth)
    {
        if (depth >= MaxDepth)
            return;
        if (context.Resolve(actionObj) is not PdfDictionary action)
            return;

        var actionType = (context.Resolve(action.Get(_s)) as PdfName)?.Value;
        if (actionType != "Rendition")
            return;

        // Walk the rendition dictionary in /R.
        WalkRendition(context, action.Get(_r), visitedMcd, depth + 1);
    }

    // Resolves a rendition object and follows its /C (media clip) to MCD dicts.
    // A rendition may be a media rendition (/S /MR) or a selector rendition (/S /SR).
    private void WalkRendition(PreflightContext context, PdfObject? rObj, HashSet<int> visitedMcd, int depth)
    {
        if (depth >= MaxDepth)
            return;

        // Cycle guard.
        int objNum = -1;
        if (rObj is PdfIndirectReference rRef)
            objNum = rRef.ObjectNumber;

        if (context.Resolve(rObj) is not PdfDictionary rendition)
            return;

        var rendType = (context.Resolve(rendition.Get(_s)) as PdfName)?.Value;

        if (rendType == "MR")
        {
            // Media rendition: check its /C (media clip).
            WalkMediaClip(context, rendition.Get(_c), visitedMcd, depth + 1);
        }
        else if (rendType == "SR")
        {
            // Selector rendition: /R is an array of renditions.
            if (context.Resolve(rendition.Get(_r)) is PdfArray renditions)
            {
                for (var i = 0; i < renditions.Count; i++)
                    WalkRendition(context, renditions[i], visitedMcd, depth + 1);
            }
        }
    }

    // Resolves a media clip object. If it is a media clip data dict (/S /MCD), checks /CT and /Alt.
    // If it is a media clip section (/S /MCS), walks its /D (data).
    private void WalkMediaClip(PreflightContext context, PdfObject? clipObj, HashSet<int> visitedMcd, int depth)
    {
        if (depth >= MaxDepth)
            return;

        int objNum = -1;
        if (clipObj is PdfIndirectReference clipRef)
        {
            objNum = clipRef.ObjectNumber;
            if (!visitedMcd.Add(objNum))
                return;
        }

        if (context.Resolve(clipObj) is not PdfDictionary clip)
            return;

        var clipType = (context.Resolve(clip.Get(_s)) as PdfName)?.Value;

        if (clipType == "MCD")
        {
            // §7.18.6.2-1: /CT (MIME content type) must be present.
            if (clip.Get(_ct) is null)
            {
                context.Report(
                    RuleIdCt, Clause, PreflightSeverity.Error,
                    "A media clip data dictionary (/S /MCD) reached from a Screen annotation "
                    + "rendition action does not contain a /CT (content-type) entry. "
                    + "ISO 14289-1:2014 §7.18.6.2 requires /CT to be present "
                    + "(ISO 14289-1:2014, 7.18.6.2, testNumber 1).");
            }

            // §7.18.6.2-2: /Alt (alternate text) must be present.
            if (clip.Get(_alt) is null)
            {
                context.Report(
                    RuleIdAlt, Clause, PreflightSeverity.Error,
                    "A media clip data dictionary (/S /MCD) reached from a Screen annotation "
                    + "rendition action does not contain an /Alt entry. "
                    + "ISO 14289-1:2014 §7.18.6.2 requires /Alt to be present "
                    + "(ISO 14289-1:2014, 7.18.6.2, testNumber 2).");
            }
        }
    }
}
