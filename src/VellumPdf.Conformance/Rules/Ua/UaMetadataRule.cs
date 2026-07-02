// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Metadata;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §5 (Metadata). A PDF/UA-1 file shall contain an XMP <c>/Metadata</c> stream that
/// declares the PDF/UA identification schema with <c>pdfuaid:part</c> equal to 1. Also checks
/// that the properties <c>part</c> (§5-3), <c>amd</c> (§5-4), and <c>corr</c> (§5-5) in the
/// PDF/UA identification namespace are bound to the prefix <c>pdfuaid</c> or the default (null)
/// prefix — a non-<c>pdfuaid</c>, non-null prefix is a violation.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 5 and ISO 16684-1 (XMP). Clean-room: derived from the
/// specification text, not from any third-party validation profile.
/// <para>
/// Prefix checks (§5-3/5-4/5-5): fire only when the property IS present bound to the PDF/UA
/// identification namespace URI (<c>http://www.aiim.org/pdfua/ns/id/</c>) via a prefix that is
/// neither <c>pdfuaid</c> nor the null/default prefix. Absent property ⇒ no fire. The check
/// is URI-based (XmpReader.Get already resolves by namespace URI), so a file that uses the correct
/// prefix but binds the URI to a wrong local name is already caught by the §5-1/5-2 <c>part==1</c>
/// check. This check targets the orthogonal case: the URI is bound to a NON-standard prefix.
/// </para>
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
        var xmp = bytes is null ? null : XmpReader.Parse(bytes);
        if (xmp is null)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "The XMP /Metadata stream could not be decoded as a well-formed XMP packet.");
            return;
        }

        var part = XmpReader.Get(xmp, XmpReader.Pdfuaid, "part");
        if (part != "1")
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                $"The XMP pdfuaid:part shall be 1 (found {(part is null ? "absent" : $"'{part}'")}).");
        }

        // §5-3/5-4/5-5: the PDF/UA identification schema properties must be bound with the
        // pdfuaid prefix (or the default/null prefix). Fire only when the property is present
        // AND the prefix bound to the PDF/UA-id namespace URI is neither "pdfuaid" nor null/empty.
        var actualPrefix = XmpReader.GetPrefixOfNamespace(xmp, XmpReader.Pdfuaid);
        if (actualPrefix is not null
            && !string.Equals(actualPrefix, "pdfuaid", StringComparison.Ordinal)
            && actualPrefix.Length > 0)
        {
            // A non-pdfuaid, non-null prefix is bound. Fire only for properties that ARE present
            // (XmpReader.Get looks up by namespace URI, so it finds them regardless of prefix).
            if (XmpReader.Get(xmp, XmpReader.Pdfuaid, "part") is not null)
                context.Report("ISO14289-1:5-3", "ISO 14289-1:2014, 5", PreflightSeverity.Error,
                    $"The XMP PDF/UA identification namespace is bound to prefix '{actualPrefix}' instead of 'pdfuaid'. "
                    + "ISO 14289-1:2014 §5 requires the pdfuaid:part property to use the 'pdfuaid' prefix "
                    + "(ISO 14289-1:2014, 5, testNumber 3).");

            if (XmpReader.Get(xmp, XmpReader.Pdfuaid, "amd") is not null)
                context.Report("ISO14289-1:5-4", "ISO 14289-1:2014, 5", PreflightSeverity.Error,
                    $"The XMP PDF/UA identification namespace is bound to prefix '{actualPrefix}' instead of 'pdfuaid'. "
                    + "ISO 14289-1:2014 §5 requires the pdfuaid:amd property to use the 'pdfuaid' prefix "
                    + "(ISO 14289-1:2014, 5, testNumber 4).");

            if (XmpReader.Get(xmp, XmpReader.Pdfuaid, "corr") is not null)
                context.Report("ISO14289-1:5-5", "ISO 14289-1:2014, 5", PreflightSeverity.Error,
                    $"The XMP PDF/UA identification namespace is bound to prefix '{actualPrefix}' instead of 'pdfuaid'. "
                    + "ISO 14289-1:2014 §5 requires the pdfuaid:corr property to use the 'pdfuaid' prefix "
                    + "(ISO 14289-1:2014, 5, testNumber 5).");
        }
    }
}
