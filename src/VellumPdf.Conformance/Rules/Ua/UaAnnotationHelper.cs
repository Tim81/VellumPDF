// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// Shared helpers for the two PDF/UA-1 annotation exemptions that recur in §7.18 rules:
/// <list type="bullet">
///   <item><b>isOutsideCropBox</b>: the annotation's <c>/Rect</c> lies entirely outside the
///     page's <c>/CropBox</c> (or <c>/MediaBox</c> when no crop box is present, both
///     inheritable). Sourced from the veraPDF predicate: when the rect does not intersect the
///     box at all the annotation is invisible and therefore exempt from every §7.18 requirement.
///     If the rect or the box cannot be determined we treat the annotation as NOT exempt (the
///     conservative direction — it stays subject to the main predicate), which was confirmed against
///     veraPDF: the baseline's annotations pass because they have no out-of-bounds rect.</item>
///   <item><b>Hidden flag</b>: <c>(F &amp; 2) == 2</c>. When bit 2 (Hidden) is set the annotation
///     is invisible regardless of its position; the annotation is exempt from §7.18 requirements.
///     Default (absent <c>/F</c>) is 0, so no flag = not exempt.</item>
/// </list>
/// Annotations satisfying either exemption are skipped by every §7.18 rule that references them.
/// </summary>
internal static class UaAnnotationHelper
{
    private static readonly PdfName _f = new("F");
    private static readonly PdfName _rect = new("Rect");
    private static readonly PdfName _cropBox = new("CropBox");
    private static readonly PdfName _mediaBox = new("MediaBox");

    // Bit 2 in the annotation flags word (bit position 2, 1-indexed) — the Hidden flag.
    private const int HiddenFlag = 1 << 1; // 0-indexed bit 1 → value 2

    /// <summary>
    /// Returns <see langword="true"/> when the annotation is exempt from §7.18 requirements
    /// because it is Hidden (<c>F &amp; 2</c>) or its <c>/Rect</c> lies entirely outside the
    /// effective crop box of <paramref name="page"/>.
    /// </summary>
    public static bool IsExempt(PreflightContext context, PdfDictionary annot, PdfDictionary page)
        => IsHidden(context, annot) || IsOutsideCropBox(context, annot, page);

    // ─── Hidden flag ────────────────────────────────────────────────────────────────────────────

    private static bool IsHidden(PreflightContext context, PdfDictionary annot)
    {
        var flags = NumericValue(context.Resolve(annot.Get(_f))) ?? 0;
        return (flags & HiddenFlag) != 0;
    }

    // ─── Outside-CropBox ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the annotation's <c>/Rect</c> lies entirely outside the
    /// effective crop box of <paramref name="page"/> (the <c>/CropBox</c> if present and
    /// inheritable, else the <c>/MediaBox</c>). If any dimension cannot be resolved the method
    /// returns <see langword="false"/> (conservative: the annotation is NOT considered exempt).
    /// </summary>
    public static bool IsOutsideCropBox(PreflightContext context, PdfDictionary annot, PdfDictionary page)
    {
        // /Rect [x0 y0 x1 y1] — lower-left to upper-right, PDF coordinate space.
        if (!TryGetRect(context, annot.Get(_rect), out var ax0, out var ay0, out var ax1, out var ay1))
            return false;

        // The effective crop box: /CropBox if present (inheritable), else /MediaBox.
        var boxObj = context.ResolveInherited(page, _cropBox) ?? context.ResolveInherited(page, _mediaBox);
        if (!TryGetRect(context, boxObj, out var bx0, out var by0, out var bx1, out var by1))
            return false;

        // Normalise both rects (PDF allows reversed corners).
        var (ax0n, ax1n) = (Math.Min(ax0, ax1), Math.Max(ax0, ax1));
        var (ay0n, ay1n) = (Math.Min(ay0, ay1), Math.Max(ay0, ay1));
        var (bx0n, bx1n) = (Math.Min(bx0, bx1), Math.Max(bx0, bx1));
        var (by0n, by1n) = (Math.Min(by0, by1), Math.Max(by0, by1));

        // The annotation is outside when it does not overlap the box in at least one dimension.
        // "Not intersecting" means: right of box, left of box, above box, or below box.
        return ax1n <= bx0n  // annotation entirely to the left
            || ax0n >= bx1n  // annotation entirely to the right
            || ay1n <= by0n  // annotation entirely below
            || ay0n >= by1n; // annotation entirely above
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static bool TryGetRect(
        PreflightContext context, PdfObject? obj,
        out double x0, out double y0, out double x1, out double y1)
    {
        x0 = y0 = x1 = y1 = 0;
        if (context.Resolve(obj) is not PdfArray { Count: >= 4 } arr)
            return false;
        var v0 = DoubleValue(context.Resolve(arr[0]));
        var v1 = DoubleValue(context.Resolve(arr[1]));
        var v2 = DoubleValue(context.Resolve(arr[2]));
        var v3 = DoubleValue(context.Resolve(arr[3]));
        if (v0 is null || v1 is null || v2 is null || v3 is null)
            return false;
        x0 = v0.Value; y0 = v1.Value; x1 = v2.Value; y1 = v3.Value;
        return true;
    }

    private static double? DoubleValue(PdfObject? obj) => obj switch
    {
        PdfInteger i => (double)i.Value,
        PdfReal r => r.Value,
        _ => null,
    };

    private static long? NumericValue(PdfObject? obj) => obj switch
    {
        PdfInteger i => i.Value,
        PdfReal r => (long)r.Value,
        _ => null,
    };
}
