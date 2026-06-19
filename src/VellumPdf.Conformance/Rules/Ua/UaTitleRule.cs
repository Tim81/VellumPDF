// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Conformance.Rules.Metadata;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.1 (Document title). A PDF/UA-1 file shall carry a document title in its XMP
/// metadata (<c>dc:title</c>) and shall instruct viewers to display it by setting
/// <c>/ViewerPreferences /DisplayDocTitle true</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.1 and ISO 32000-1:2008, 12.2 (viewer preferences). Clean-room:
/// derived from the specification text, not from any third-party validation profile.
/// </remarks>
internal sealed class UaTitleRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.1-title";

    public string Clause => "ISO 14289-1:2014, 7.1";

    private static readonly PdfName _viewerPreferences = new("ViewerPreferences");
    private static readonly PdfName _displayDocTitle = new("DisplayDocTitle");
    private static readonly PdfName _metadata = new("Metadata");

    public void Evaluate(PreflightContext context)
    {
        var viewerPrefs = context.Resolve(context.Catalog.Get(_viewerPreferences)) as PdfDictionary;
        if (viewerPrefs?.Get(_displayDocTitle) is not PdfBoolean { Value: true })
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "A PDF/UA-1 file shall set /ViewerPreferences /DisplayDocTitle to true.");
        }

        var stream = context.ResolveStream(context.Catalog.Get(_metadata));
        var bytes = stream is null ? null : context.DecodeStream(stream);
        if (bytes is null || !XmpReader.Contains(Encoding.UTF8.GetString(bytes), "dc:title"))
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "A PDF/UA-1 file shall provide a document title via the XMP dc:title property.");
        }
    }
}
