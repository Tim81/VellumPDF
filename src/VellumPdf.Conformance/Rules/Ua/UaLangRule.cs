// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 (Text — natural language). A PDF/UA-1 file shall specify a default natural
/// language for its text via the document catalog's <c>/Lang</c> entry.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.2 and ISO 32000-1:2008, 14.9.2. Clean-room: derived from the
/// specification text, not from any third-party validation profile. Per-structure-element
/// language overrides are validated in a later slice.
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
