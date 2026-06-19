// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Conformance.Rules.Metadata;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §5 (Metadata). A PDF/UA-1 file shall contain an XMP <c>/Metadata</c> stream that
/// declares the PDF/UA identification schema with <c>pdfuaid:part</c> equal to 1.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 5 and ISO 16684-1 (XMP). Clean-room: derived from the
/// specification text, not from any third-party validation profile.
/// </remarks>
internal sealed class UaMetadataRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:5-pdfuaid";

    public string Clause => "ISO 14289-1:2014, 5";

    private static readonly PdfName _metadata = new("Metadata");

    public void Evaluate(PreflightContext context)
    {
        var stream = context.ResolveStream(context.Catalog.Get(_metadata));
        if (stream is null)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "A PDF/UA-1 file shall contain an XMP /Metadata stream.");
            return;
        }

        var bytes = context.DecodeStream(stream);
        if (bytes is null)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "The XMP /Metadata stream could not be decoded.");
            return;
        }

        var part = XmpReader.GetProperty(Encoding.UTF8.GetString(bytes), "pdfuaid:part");
        if (part != "1")
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                $"The XMP pdfuaid:part shall be 1 (found {(part is null ? "absent" : $"'{part}'")}).");
        }
    }
}
