// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 (Text — natural language). This rule requires a PDF/UA-1 file to specify a
/// default natural language through the document catalog's <c>/Lang</c> entry. 7.2 does not itself
/// require that entry: it allows the language to be declared as in ISO 32000-1 14.9.2 and/or 7.9.2.
/// </summary>
/// <remarks>
/// Cites ISO 14289-1:2014, 7.2 and ISO 32000-1:2008, 14.9.2. It was written from veraPDF's PDF/UA-1
/// profile and checked against veraPDF, before ISO 14289-1 was held. The 2026-11-21 audit against
/// the clause text (#428) found it broader: it fires whenever the catalog has no <c>/Lang</c>, even
/// when structure elements declare the language, which 7.2 allows. The language that individual
/// structure elements declare is read by <see cref="UaNaturalLanguageRule"/> and <see
/// cref="UaMarkedContentLangRule"/>, not here.
/// </remarks>
internal sealed class UaLangRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-lang";

    public string Clause => "ISO 14289-1:2014, 7.2";

    private static readonly PdfName _lang = new("Lang");

    public void Evaluate(PreflightContext context)
    {
        var lang = context.Resolve(context.Catalog.Get(_lang));
        var specified = lang switch
        {
            PdfLiteralString s => HasNonSpace(s.Bytes.Span),
            PdfHexString h => HasNonSpace(h.Bytes.Span),
            _ => false,
        };

        if (!specified)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "A PDF/UA-1 file shall specify a default natural language in the document catalog /Lang entry.");
        }
    }

    private static bool HasNonSpace(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b is not (32 or 9 or 10 or 13))
                return true;
        return false;
    }
}
