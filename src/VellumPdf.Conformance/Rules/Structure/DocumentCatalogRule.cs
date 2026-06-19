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
/// Authored from ISO 32000-2:2020, 7.7.2 ("Document catalog"). Clean-room: derived from the
/// specification text, not from any third-party validation profile.
/// </remarks>
internal sealed class DocumentCatalogRule : IConformanceRule
{
    public string RuleId => "ISO32000-2:7.7.2-catalog-type";

    public string Clause => "ISO 32000-2:2020, 7.7.2";

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
