// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
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

        var bytes = context.DecodeStream(stream);
        if (bytes is null)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "The XMP /Metadata stream could not be decoded.");
            return;
        }

        var xmp = Encoding.UTF8.GetString(bytes);
        var actualPart = ExtractValue(xmp, "pdfaid:part");
        var actualConformance = ExtractValue(xmp, "pdfaid:conformance");

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

    /// <summary>
    /// Returns the value associated with <paramref name="token"/> in <paramref name="xmp"/>,
    /// reading either the element form (<c>&gt;value&lt;</c>) or the attribute form
    /// (<c>="value"</c>). Returns <see langword="null"/> when the token is absent.
    /// </summary>
    private static string? ExtractValue(string xmp, string token)
    {
        var idx = xmp.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var i = idx + token.Length;
        while (i < xmp.Length && char.IsWhiteSpace(xmp[i]))
            i++;
        if (i >= xmp.Length)
            return null;

        char terminator;
        if (xmp[i] == '>')
        {
            i++;
            terminator = '<';
        }
        else if (xmp[i] == '=')
        {
            i++;
            while (i < xmp.Length && char.IsWhiteSpace(xmp[i]))
                i++;
            if (i >= xmp.Length || (xmp[i] != '"' && xmp[i] != '\''))
                return null;
            terminator = xmp[i];
            i++;
        }
        else
        {
            return null;
        }

        var start = i;
        while (i < xmp.Length && xmp[i] != terminator)
            i++;
        return xmp[start..i].Trim();
    }
}
