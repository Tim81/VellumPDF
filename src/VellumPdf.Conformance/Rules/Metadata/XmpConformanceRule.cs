// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Metadata;

/// <summary>
/// ISO 19005-2 §6.7.2 (Metadata). The document catalog shall contain an XMP <c>/Metadata</c>
/// stream, and that packet shall identify the PDF/A part and conformance level via the
/// <c>pdfaid:part</c> and <c>pdfaid:conformance</c> properties, matching the level being
/// validated (part 2, conformance B/U/A).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.7.2 and ISO 16684-1 (XMP). Clean-room: derived from the
/// specification text, not from any third-party validation profile. The pdfaid properties are
/// read tolerantly in either XML element (<c>&lt;pdfaid:part&gt;2&lt;/pdfaid:part&gt;</c>) or
/// attribute (<c>pdfaid:part="2"</c>) serialisation.
/// </remarks>
internal sealed class XmpConformanceRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.7.2-pdfaid";

    public string Clause => "ISO 19005-2:2011, 6.7.2";

    private static readonly PdfName _metadata = new("Metadata");

    public void Evaluate(PreflightContext context)
    {
        if (Expected(context.Conformance) is not { } expected)
            return;
        var (part, conformance) = expected;

        var stream = context.ResolveStream(context.Catalog.Get(_metadata));
        if (stream is null)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "The document catalog shall contain an XMP /Metadata stream.");
            return;
        }

        // Note: PDF/A-2 (ISO 19005-2) — unlike PDF/A-1 — does NOT require the document /Metadata
        // stream to be unfiltered; a FlateDecode metadata stream is permitted (and Acrobat and
        // Ghostscript routinely emit one). veraPDF does not flag it for part 2, so neither do we.
        var bytes = context.DecodeStream(stream);
        var xmp = bytes is null ? null : XmpReader.Parse(bytes);
        if (xmp is null)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "The XMP /Metadata stream could not be decoded as a well-formed XMP packet.");
            return;
        }

        var actualPart = XmpReader.Get(xmp, XmpReader.Pdfaid, "part");
        var actualConformance = XmpReader.Get(xmp, XmpReader.Pdfaid, "conformance");

        if (actualPart != part)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                $"The XMP pdfaid:part shall be {part} (found {Describe(actualPart)}).");
        }

        if (!string.Equals(actualConformance, conformance, StringComparison.Ordinal))
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                $"The XMP pdfaid:conformance shall be {conformance} (found {Describe(actualConformance)}).");
        }
    }

    private static (string Part, string Conformance)? Expected(PdfConformance conformance) => conformance switch
    {
        PdfConformance.PdfA2B => ("2", "B"),
        PdfConformance.PdfA2U => ("2", "U"),
        PdfConformance.PdfA2A => ("2", "A"),
        _ => null,
    };

    private static string Describe(string? value) => value is null ? "absent" : $"'{value}'";
}
