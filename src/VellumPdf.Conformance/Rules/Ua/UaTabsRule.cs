// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.18.3 (Tab order). Every page that contains annotations shall set its
/// <c>/Tabs</c> entry to <c>/S</c>, so that interactive elements are navigated in logical
/// structure order.
/// </summary>
/// <remarks>
/// Cites ISO 14289-1:2014, 7.18.3 and ISO 32000-1:2008, 7.7.3.3. It was written from veraPDF's
/// PDF/UA-1 profile and checked against veraPDF, before ISO 14289-1 was held. The 2026-11-21 audit
/// against the clause text (#428) found it broader: 7.18.1 exempts annotations whose hidden flag is
/// set and Popup annotations, and this rule fires on any annotation without consulting those
/// exemptions.
/// </remarks>
internal sealed class UaTabsRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.18.3-tabs";

    public string Clause => "ISO 14289-1:2014, 7.18.3";

    private static readonly PdfName _tabs = new("Tabs");

    public void Evaluate(PreflightContext context)
    {
        foreach (var page in context.EnumeratePages())
        {
            if (context.Resolve(page.Get(PdfName.Annots)) is not PdfArray annots || annots.Count == 0)
                continue;

            // /Tabs is NOT an inheritable page attribute — ISO 32000-1 §7.7.3.4 / Table 31 lists only
            // Resources, MediaBox, CropBox and Rotate as inheritable. It must therefore be read from
            // the page object itself; a value set on an ancestor /Pages node does not satisfy it.
            if ((context.Resolve(page.Get(_tabs)) as PdfName)?.Value != "S")
            {
                context.Report(RuleId, Clause, PreflightSeverity.Error,
                    "A page containing annotations shall set its /Tabs entry to /S (structure order).");
            }
        }
    }
}
