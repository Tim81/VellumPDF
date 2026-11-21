// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// The document catalog dictionary shall be identified by a <c>/Type</c> entry whose value
/// is <c>/Catalog</c>. This is a baseline structural requirement inherited by every
/// PDF/A and PDF/UA level from ISO 32000.
/// </summary>
/// <remarks>
/// Authored from ISO 32000-1:2008, 7.7.2 ("Document catalog"). Neither the #418 nor the #428
/// audit covered this rule, so it has not been compared with veraPDF's profiles or with a held
/// copy of either PDF/A or PDF/UA.
/// </remarks>
internal sealed class DocumentCatalogRule : IConformanceRule
{
    public string RuleId => "ISO32000-1:7.7.2-catalog-type";

    public string Clause => "ISO 32000-1:2008, 7.7.2";

    public void Evaluate(PreflightContext context)
    {
        var type = context.Resolve(context.Catalog.Get(PdfName.Type));
        if (type is not PdfName name || name.Value != "Catalog")
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "The document catalog dictionary shall have a /Type entry with the value /Catalog.");
        }
    }
}
