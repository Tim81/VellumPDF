// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Graphics;

/// <summary>
/// ISO 19005-2 §6.2.2 test 2. A content stream that references resources shall have an
/// explicitly associated <c>/Resources</c> dictionary on the page object itself — the page
/// may not rely on a <c>/Resources</c> dictionary inherited from an ancestor <c>Pages</c> node
/// to satisfy the resource reference (ISO 32000-1:2008, 7.8.3).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.2 and ISO 32000-1:2008, 7.8.3. Empirical veraPDF-oracle
/// probing confirms the precise trigger: the rule fires when the page dictionary itself has no
/// <c>/Resources</c> key (making resource lookup only possible via the parent-chain inheritance
/// mechanism) and the page content stream uses at least one named resource. A page that has its
/// own <c>/Resources</c> entry — even an empty one — satisfies the "explicitly associated"
/// requirement in veraPDF's interpretation and does not trigger this rule. Clean-room: derived
/// from specification text and empirical veraPDF-oracle probing, not from any third-party profile.
/// <para>
/// <strong>Categories detected:</strong> Font (<c>Tf</c>), XObject (<c>Do</c>), ExtGState
/// (<c>gs</c>), ColorSpace (<c>cs</c>/<c>CS</c>), and Shading (<c>sh</c>). Pattern names
/// (<c>scn</c>/<c>SCN</c> in Pattern color space) and Properties names (<c>BDC</c>/<c>DP</c>
/// with a name operand) are not detected because reliable identification requires stateful
/// color-space and marked-content tracking respectively; omitting them keeps false positives
/// impossible at the cost of under-detecting those two categories.
/// </para>
/// <para>
/// <strong>Scope:</strong> page content streams only. Form XObject, Type 3 CharProc, and
/// annotation appearance streams are not walked — the same caveat that applies to
/// <c>6.2.2-1</c> and <c>6.1.10-1</c>.
/// </para>
/// <para>
/// <strong>Defensive operation:</strong> on any decode failure or lexer error the scan stops
/// and retains findings already collected; no spurious finding is emitted for malformed content.
/// </para>
/// </remarks>
internal sealed class InheritedResourceRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.2-2";

    public string Clause => "ISO 19005-2:2011, 6.2.2";

    public void Evaluate(PreflightContext context)
    {
        foreach (var page in context.EnumeratePages())
            EvaluatePage(context, page);
    }

    private void EvaluatePage(PreflightContext context, PdfDictionary page)
    {
        // Check whether the page has its OWN /Resources key — not inherited from a parent Pages
        // node. page.Get() looks only at the entries of this dictionary object itself; it does
        // not follow /Parent references. A non-null return means the page is explicitly associated
        // with a Resources dictionary and the rule is satisfied regardless of what is in it.
        var ownResources = page.Get(PdfName.Resources);
        if (ownResources is not null)
            return; // page has an explicit /Resources entry — rule satisfied

        // The page has no own /Resources. Scan its content stream for any named-resource usage.
        // Any used name is an "inheritedResourceName" in veraPDF's terminology.
        HashSet<string> usedFonts, drawnXObjects, appliedExtGStates, selectedColorSpaces, paintedShadings;
        try
        {
            var u = ContentStreamUsage.Analyze(context, page);
            usedFonts = u.UsedFonts;
            drawnXObjects = u.DrawnXObjects;
            appliedExtGStates = u.AppliedExtGStates;
            selectedColorSpaces = u.SelectedColorSpaces;
            paintedShadings = u.PaintedShadings;
        }
        catch
        {
            // Undecodable content — skip this page; do not emit a finding.
            return;
        }

        // Collect all used resource names across the detected categories. Report each name at
        // most once per page even if it appears in multiple categories (e.g. a name used both
        // as a font and as a color space — unusual but defensive).
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in usedFonts)
            ReportIfNew(context, name, reported);
        foreach (var name in drawnXObjects)
            ReportIfNew(context, name, reported);
        foreach (var name in appliedExtGStates)
            ReportIfNew(context, name, reported);
        foreach (var name in selectedColorSpaces)
            ReportIfNew(context, name, reported);
        foreach (var name in paintedShadings)
            ReportIfNew(context, name, reported);
    }

    private void ReportIfNew(PreflightContext context, string resourceName, HashSet<string> reported)
    {
        if (reported.Add(resourceName))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                $"A content stream refers to resource(s) {resourceName} not defined in an explicitly associated Resources dictionary");
        }
    }
}
