// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Annotations;

/// <summary>
/// ISO 19005-2 §6.5.3 (Annotations). An annotation's flags shall have the <c>Print</c> bit set
/// and the <c>Hidden</c> and <c>NoView</c> bits clear, and — except for <c>/Popup</c> and
/// <c>/Link</c> annotations — the annotation shall provide a normal appearance stream
/// (<c>/AP</c> with an <c>/N</c> entry).
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

    public void Evaluate(PreflightContext context)
    {
        foreach (var annot in context.EnumerateAnnotations())
        {
            var subtype = (annot.Get(PdfName.Subtype) as PdfName)?.Value;

            // Popup annotations are driven by their parent and are exempt from these requirements.
            if (subtype == "Popup")
                continue;

            var flags = (int)((context.Resolve(annot.Get(_f)) as PdfInteger)?.Value ?? 0);
            var label = subtype is null ? "An annotation" : $"A /{subtype} annotation";

            if ((flags & Print) == 0)
                context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall have the Print flag set.");
            if ((flags & Hidden) != 0)
                context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall not have the Hidden flag set.");
            if ((flags & NoView) != 0)
                context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall not have the NoView flag set.");

            // A /Link annotation has no visible appearance of its own and is exempt.
            if (subtype == "Link")
                continue;

            var ap = context.Resolve(annot.Get(_ap)) as PdfDictionary;
            if (ap is null || ap.Get(_n) is null)
                context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall have a normal appearance (/AP /N).");
        }
    }
}
