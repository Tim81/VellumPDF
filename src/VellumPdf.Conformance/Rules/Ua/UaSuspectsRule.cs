// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.1 (General — tagged PDF). The document catalog's <c>/MarkInfo</c> dictionary
/// shall not contain a <c>/Suspects</c> entry whose value is the boolean <see langword="true"/>.
/// The default (absent) and the explicit value <see langword="false"/> are both acceptable.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.1 (CosDocument predicate: <c>Suspects != true</c>) and
/// ISO 32000-1:2008, 14.7.1. Clean-room: derived from the specification text and empirically
/// validated against veraPDF 1.30.2 (test id 7.1-4), not from any third-party validation profile.
/// </remarks>
internal sealed class UaSuspectsRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.1-4";

    public string Clause => "ISO 14289-1:2014, 7.1";

    private static readonly PdfName _markInfo = new("MarkInfo");
    private static readonly PdfName _suspects = new("Suspects");

    public void Evaluate(PreflightContext context)
    {
        var markInfo = context.Resolve(context.Catalog.Get(_markInfo)) as PdfDictionary;
        if (markInfo is null)
            return; // Absent /MarkInfo — /Suspects defaults to false; no violation.

        var suspects = context.Resolve(markInfo.Get(_suspects));
        if (suspects is PdfBoolean { Value: true })
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "The document catalog /MarkInfo /Suspects entry shall not be true in a PDF/UA-1 file.");
        }
    }
}
