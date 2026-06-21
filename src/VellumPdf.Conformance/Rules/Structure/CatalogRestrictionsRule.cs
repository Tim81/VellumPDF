// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.10 (Use of alternate presentations and transitions) and §6.11 (Document
/// requirements). A conforming file shall not contain an <c>/AlternatePresentations</c> entry in the
/// document's name dictionary, a <c>/PresSteps</c> entry in any page dictionary (§6.10), or a
/// <c>/Requirements</c> entry in the document catalog (§6.11).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.10 and 6.11. Clean-room: derived from the specification text,
/// not from any third-party validation profile. These are pure object-graph presence checks: the
/// forbidden entries are reached directly from the catalog (the name dictionary's
/// <c>/AlternatePresentations</c> and the catalog's <c>/Requirements</c>) and from each page
/// dictionary (<c>/PresSteps</c>), so no content or byte-level parsing is involved.
/// </remarks>
internal sealed class CatalogRestrictionsRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.11-1-requirements";

    public string Clause => "ISO 19005-2:2011, 6.11";

    private static readonly PdfName _requirements = new("Requirements");
    private static readonly PdfName _names = new("Names");
    private static readonly PdfName _alternatePresentations = new("AlternatePresentations");
    private static readonly PdfName _presSteps = new("PresSteps");

    public void Evaluate(PreflightContext context)
    {
        // §6.11-1: the document catalog shall not contain the Requirements key.
        if (context.Catalog.Get(_requirements) is not null)
            context.Report(
                RuleId, Clause, PreflightSeverity.Error,
                "The document catalog contains a /Requirements entry, which is not permitted in PDF/A-2.");

        // §6.10-1: there shall be no AlternatePresentations entry in the document's name dictionary.
        if (context.Resolve(context.Catalog.Get(_names)) is PdfDictionary nameDictionary
            && nameDictionary.Get(_alternatePresentations) is not null)
            context.Report(
                "ISO19005-2:6.10-1-alternate-presentations", "ISO 19005-2:2011, 6.10", PreflightSeverity.Error,
                "The document's name dictionary contains an /AlternatePresentations entry, which is not "
                + "permitted in PDF/A-2.");

        // §6.10-2: there shall be no PresSteps entry in any page dictionary.
        foreach (var page in context.EnumeratePages())
            if (page.Get(_presSteps) is not null)
            {
                context.Report(
                    "ISO19005-2:6.10-2-pres-steps", "ISO 19005-2:2011, 6.10", PreflightSeverity.Error,
                    "A page dictionary contains a /PresSteps entry, which is not permitted in PDF/A-2.");
                break; // One report suffices; the verdict is unaffected by the count.
            }
    }
}
