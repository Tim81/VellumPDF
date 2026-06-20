// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Forms;

/// <summary>
/// ISO 19005-2 §6.4.2 (Interactive forms). The interactive form dictionary — the value of the
/// document catalog's <c>/AcroForm</c> key — shall not contain an <c>/XFA</c> entry; XFA forms are
/// not permitted in PDF/A-2.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.4.2. Clean-room: derived from the specification text, not from
/// any third-party validation profile.
/// </remarks>
internal sealed class XfaRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.4.2-xfa";

    public string Clause => "ISO 19005-2:2011, 6.4.2";

    private static readonly PdfName _acroForm = new("AcroForm");
    private static readonly PdfName _xfa = new("XFA");

    public void Evaluate(PreflightContext context)
    {
        if (context.Resolve(context.Catalog.Get(_acroForm)) is not PdfDictionary acroForm)
            return;

        if (acroForm.Get(_xfa) is not null)
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "The interactive form dictionary (/AcroForm) shall not contain an /XFA entry; "
                + "XFA forms are not permitted in PDF/A.");
        }
    }
}
