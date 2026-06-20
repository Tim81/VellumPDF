// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Annotations;

/// <summary>
/// ISO 19005-2 §6.5.3 (Annotations). An annotation's flags shall have the <c>Print</c> bit set
/// and the <c>Hidden</c> and <c>NoView</c> bits clear, and — except for <c>/Popup</c> and
/// <c>/Link</c> annotations — the annotation shall provide a normal appearance stream
/// (<c>/AP</c> with an <c>/N</c> entry). The flag requirements still apply to <c>/Link</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.5.3 and ISO 32000-1:2008, 12.5.3 (Table 165 annotation
/// flags). Clean-room: derived from the specification text, not from any third-party validation
/// profile.
/// </remarks>
internal sealed class AnnotationRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.5.3-annotation";

    public string Clause => "ISO 19005-2:2011, 6.5.3";

    private static readonly PdfName _f = new("F");
    private static readonly PdfName _ap = new("AP");
    private static readonly PdfName _n = new("N");

    // Annotation flag bit values (ISO 32000-1 Table 165).
    private const int Hidden = 1 << 1;  // bit 2
    private const int Print = 1 << 2;   // bit 3
    private const int NoView = 1 << 5;  // bit 6

    // Multimedia / dynamic annotation subtypes prohibited by PDF/A-2 (ISO 19005-2 §6.5.3).
    // This is intentionally a deny-list of the unambiguously forbidden subtypes rather than an
    // allow-list of permitted ones: the permitted set is large, so a deny-list avoids
    // false-positives on valid-but-uncommon subtypes. Rejecting unknown subtypes outright (the
    // stricter allow-list reading of §6.5.3) is a deliberate follow-up.
    private static readonly HashSet<string> _forbiddenSubtypes = new(StringComparer.Ordinal)
    {
        "Sound", "Movie", "Screen", "3D", "RichMedia",
    };

    public void Evaluate(PreflightContext context)
    {
        foreach (var annot in context.EnumerateAnnotations())
        {
            var subtype = (context.Resolve(annot.Get(PdfName.Subtype)) as PdfName)?.Value;

            if (subtype is not null && _forbiddenSubtypes.Contains(subtype))
            {
                context.Report(RuleId, Clause, PreflightSeverity.Error,
                    $"Annotations of type /{subtype} are not permitted in PDF/A.");
                continue;
            }

            // Popup annotations are driven by their parent and are exempt from these requirements.
            if (subtype == "Popup")
                continue;

            var flags = (int)(NumericValue(context.Resolve(annot.Get(_f))) ?? 0);
            var label = subtype is null ? "An annotation" : $"A /{subtype} annotation";

            if ((flags & Print) == 0)
                context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall have the Print flag set.");
            if ((flags & Hidden) != 0)
                context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall not have the Hidden flag set.");
            if ((flags & NoView) != 0)
                context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall not have the NoView flag set.");

            // A /Link annotation has no visible appearance of its own and is exempt from the
            // appearance-stream requirement — but NOT from the flag requirements above: veraPDF
            // requires the Print flag on Link annotations too (confirmed by the pdfa2b-link-no-print
            // oracle fixture, where veraPDF reports a Link with no /F as non-compliant).
            if (subtype == "Link")
                continue;

            // The normal appearance /N is either an appearance stream or a sub-dictionary keyed by
            // appearance state; a missing/non-stream-non-dictionary value does not satisfy it.
            var apN = (context.Resolve(annot.Get(_ap)) as PdfDictionary)?.Get(_n);
            if (context.ResolveStream(apN) is null && context.Resolve(apN) is not PdfDictionary)
                context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall have a normal appearance (/AP /N).");
        }
    }

    private static long? NumericValue(PdfObject? value) => value switch
    {
        PdfInteger i => i.Value,
        PdfReal r => (long)r.Value,
        _ => null,
    };
}
