// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Annotations;

/// <summary>
/// ISO 19005-2 §6.3 (Annotations). An annotation's flags shall have the <c>Print</c> bit set
/// and the <c>Invisible</c>, <c>Hidden</c>, <c>NoView</c>, and <c>ToggleNoView</c> bits clear
/// (§6.3.2-2); when an annotation has an <c>/AP</c>, that appearance dictionary shall contain only
/// the <c>/N</c> entry (§6.3.3-2); and — except for <c>/Popup</c> and <c>/Link</c> annotations — the
/// annotation shall provide a normal appearance stream (<c>/AP</c> with an <c>/N</c> entry). The flag
/// and appearance-dictionary requirements still apply to <c>/Link</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.3 (forbidden subtypes §6.3.1, flags §6.3.2, appearance
/// §6.3.3) and ISO 32000-1:2008, 12.5.3 (Table 165 annotation flags). Clean-room: derived from the
/// specification text, not from any third-party validation profile.
/// </remarks>
internal sealed class AnnotationRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.3-annotation";

    public string Clause => "ISO 19005-2:2011, 6.3";

    private static readonly PdfName _f = new("F");
    private static readonly PdfName _ap = new("AP");
    private static readonly PdfName _n = new("N");
    private static readonly PdfName _ft = new("FT");
    private static readonly PdfName _parent = new("Parent");
    private static readonly PdfName _rect = new("Rect");

    private const int MaxFieldDepth = 64;

    // Annotation flag bit values (ISO 32000-1 Table 165).
    private const int Invisible = 1 << 0;     // bit 1
    private const int Hidden = 1 << 1;        // bit 2
    private const int Print = 1 << 2;         // bit 3
    private const int NoView = 1 << 5;        // bit 6
    private const int ToggleNoView = 1 << 8;  // bit 9

    // Multimedia / dynamic annotation subtypes prohibited by PDF/A-2 (ISO 19005-2 §6.3.1).
    // This is intentionally a deny-list of the unambiguously forbidden subtypes rather than an
    // allow-list of permitted ones: the permitted set is large, so a deny-list avoids
    // false-positives on valid-but-uncommon subtypes. Rejecting unknown subtypes outright (the
    // stricter allow-list reading of §6.3.1) is a deliberate follow-up.
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

            var label = subtype is null ? "An annotation" : $"A /{subtype} annotation";

            // §6.3.2: a /Popup annotation is driven by its parent and is exempt from the annotation
            // flag requirements (it need not even carry an /F). Every other annotation's flags are
            // constrained. The appearance-dictionary requirements below still apply to Popup.
            if (subtype != "Popup")
            {
                var flags = (int)(NumericValue(context.Resolve(annot.Get(_f))) ?? 0);
                if ((flags & Print) == 0)
                    context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall have the Print flag set.");
                if ((flags & Hidden) != 0)
                    context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall not have the Hidden flag set.");
                if ((flags & Invisible) != 0)
                    context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall not have the Invisible flag set.");
                if ((flags & NoView) != 0)
                    context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall not have the NoView flag set.");
                // §6.3.2-2: the ToggleNoView flag shall also be clear.
                if ((flags & ToggleNoView) != 0)
                    context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall not have the ToggleNoView flag set.");
            }

            // §6.3.3-2: when an annotation has an /AP, the appearance dictionary shall contain only the
            // /N (normal) entry — no /D (down) or /R (rollover). This applies to every annotation that
            // has an /AP, including /Link.
            var ap = context.Resolve(annot.Get(_ap)) as PdfDictionary;
            var apHasOnlyN = ap is not null;
            if (ap is not null)
                foreach (var entry in ap.Entries)
                    if (entry.Key.Value != "N")
                    {
                        apHasOnlyN = false;
                        context.Report(RuleId, Clause, PreflightSeverity.Error,
                            $"{label}'s appearance dictionary (/AP) shall contain only the /N entry (found /{entry.Key.Value}).");
                        break;
                    }

            // §6.3.3-3 / §6.3.3-4: when the appearance dictionary holds only /N, the kind of that /N
            // depends on the annotation. A Widget whose field type (its own /FT or one inherited
            // through the /Parent field chain) is /Btn shall have an appearance SUB-DICTIONARY (keyed
            // by appearance state); every other annotation shall have an appearance STREAM. Note that
            // resolving a stream object yields its dictionary, so the kind is told apart by whether the
            // value resolves as a stream — not by "is it a dictionary".
            if (apHasOnlyN)
            {
                var n = ap!.Get(_n);
                var isStream = context.ResolveStream(n) is not null;
                var isSubDictionary = !isStream && context.Resolve(n) is PdfDictionary;
                if (isStream || isSubDictionary)
                {
                    var isButtonWidget = subtype == "Widget" && FieldType(context, annot) == "Btn";
                    if (isButtonWidget && !isSubDictionary)
                        context.Report(RuleId, Clause, PreflightSeverity.Error,
                            $"{label} (a Widget button field) shall have an appearance sub-dictionary as its /AP /N.");
                    else if (!isButtonWidget && !isStream)
                        context.Report(RuleId, Clause, PreflightSeverity.Error,
                            $"{label} shall have an appearance stream as its /AP /N.");
                }
            }

            // A /Link (and a /Popup) annotation has no visible appearance of its own and is exempt from
            // the appearance-PRESENCE requirement (§6.3.3-1) — but NOT from the /AP-kind checks above
            // (a /Popup with an /AP /N sub-dictionary is non-compliant), nor, for /Link, from the flag
            // requirements (the pdfa2b-link oracle fixture exercises a conformant Link with /F Print).
            if (subtype == "Link" || subtype == "Popup")
                continue;

            // §6.3.3-1: an annotation whose /Rect is degenerate (zero width or zero height) has no
            // visible extent and is exempt from the appearance-presence requirement. veraPDF 1.30.2
            // accepts such annotations (including invisible signature widgets with /Rect [0 0 0 0])
            // without flagging the missing /AP. The flag and /AP-kind checks above still apply.
            if (HasDegenerateRect(context, annot))
                continue;

            // The normal appearance /N is either an appearance stream or a sub-dictionary keyed by
            // appearance state; a missing/non-stream-non-dictionary value does not satisfy it.
            var apN = ap?.Get(_n);
            if (context.ResolveStream(apN) is null && context.Resolve(apN) is not PdfDictionary)
                context.Report(RuleId, Clause, PreflightSeverity.Error, $"{label} shall have a normal appearance (/AP /N).");
        }
    }

    // Returns true when the annotation's /Rect is degenerate — i.e. has zero width (x0 == x2) or
    // zero height (y1 == y3). A degenerate rect means the annotation has no visible area, so it
    // carries no renderable content and is exempt from the appearance-stream presence requirement
    // (§6.3.3-1). veraPDF 1.30.2 confirms: it accepts annotations with /Rect [0 0 0 0] (and any
    // other zero-area rectangle) without flagging a missing /AP.
    private static bool HasDegenerateRect(PreflightContext context, PdfDictionary annot)
    {
        if (context.Resolve(annot.Get(_rect)) is not PdfArray { Count: 4 } rect)
            return false;
        var x0 = NumericValue(context.Resolve(rect[0]));
        var y1 = NumericValue(context.Resolve(rect[1]));
        var x2 = NumericValue(context.Resolve(rect[2]));
        var y3 = NumericValue(context.Resolve(rect[3]));
        if (x0 is null || y1 is null || x2 is null || y3 is null)
            return false;
        return x0 == x2 || y1 == y3; // zero width or zero height
    }

    // The field type (/FT) of a widget annotation: its own, or — for a widget that is a child of a
    // field — the one inherited through the /Parent field chain (ISO 32000-1 §12.7.3.1).
    private static string? FieldType(PreflightContext context, PdfDictionary annot)
    {
        var current = annot;
        for (var depth = 0; depth < MaxFieldDepth && current is not null; depth++)
        {
            if (context.Resolve(current.Get(_ft)) is PdfName ft)
                return ft.Value;
            current = context.Resolve(current.Get(_parent)) as PdfDictionary;
        }
        return null;
    }

    private static long? NumericValue(PdfObject? value) => value switch
    {
        PdfInteger i => i.Value,
        PdfReal r => (long)r.Value,
        _ => null,
    };
}
