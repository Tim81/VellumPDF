// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.1 (General — tagging). A PDF/UA-1 file shall be a tagged PDF: the document
/// catalog's <c>/MarkInfo</c> dictionary shall set <c>/Marked true</c>, and the catalog shall
/// reference a document structure tree (<c>/StructTreeRoot</c>).
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.1 and ISO 32000-1:2008, 14.7. Clean-room: derived from the
/// specification text, not from any third-party validation profile.
/// </remarks>
internal sealed class UaTaggingRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.1-tagged";

    public string Clause => "ISO 14289-1:2014, 7.1";

    private static readonly PdfName _markInfo = new("MarkInfo");
    private static readonly PdfName _marked = new("Marked");
    private static readonly PdfName _structTreeRoot = new("StructTreeRoot");

    public void Evaluate(PreflightContext context)
    {
        var markInfo = context.Resolve(context.Catalog.Get(_markInfo)) as PdfDictionary;
        if (markInfo?.Get(_marked) is not PdfBoolean { Value: true })
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "A PDF/UA-1 file shall set the document catalog /MarkInfo /Marked entry to true.");
        }

        if (context.Resolve(context.Catalog.Get(_structTreeRoot)) is not PdfDictionary)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "A PDF/UA-1 file shall contain a document structure tree (/StructTreeRoot).");
        }
    }
}
