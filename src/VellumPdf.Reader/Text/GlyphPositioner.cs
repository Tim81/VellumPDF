// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader.Content;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader;

/// <summary>
/// ISO 32000-2 §9.4.4's own arithmetic ("Text space details"), isolated from
/// <see cref="TextExtractionVisitor"/> so it is testable with a synthesized <see
/// cref="DecodedGlyph"/> and no PDF at all. Every method here is a pure function of its own
/// arguments; none of them read or write graphics or text state.
/// </summary>
internal static class GlyphPositioner
{
    /// <summary>
    /// The text rendering matrix, §9.4.4's own Trm:
    /// <c>Trm = [Tfs·Th 0 0; 0 Tfs 0; 0 Trise 1] × Tm × CTM</c>. <paramref name="horizontalScaling"/>
    /// is §9.3.4's percentage (100 unscaled), converted to Th here rather than by the caller, so
    /// every caller applies the same <c>/100.0</c> once. <paramref name="rise"/> enters this matrix
    /// only, never <paramref name="textMatrix"/> itself (§9.3.7): a glyph shown with a non-zero
    /// <c>Ts</c> is painted offset, but the pen position <c>Tj</c>/<c>TJ</c> advance from is not.
    /// </summary>
    internal static Matrix ComputeTextRenderingMatrix(
        double fontSize, double horizontalScaling, double rise, Matrix textMatrix, Matrix ctm)
    {
        var th = horizontalScaling / 100.0;
        var parameters = new Matrix(fontSize * th, 0, 0, fontSize, 0, rise);
        return parameters.Concat(textMatrix).Concat(ctm);
    }

    /// <summary>
    /// The displacement §9.4.4 applies to the text matrix after one glyph is shown:
    /// <c>tx = ((w0 - Tj/1000) × Tfs + Tc + Tw) × Th</c>, restricted to the horizontal-only case
    /// this PR covers (§9.6.1: every simple font is horizontal writing mode, so <c>ty</c> is always
    /// 0 and not computed here) and with no <c>Tj</c> term: a numbered <c>TJ</c> array element is a
    /// standalone adjustment handled by <see cref="ComputeNumericAdjustment"/> instead (Table 107's
    /// prose, not this clause's algebraic fold into the next glyph — see that method's remarks for
    /// why).
    /// </summary>
    /// <param name="glyph">
    /// The decoded glyph; <see cref="DecodedGlyph.Width"/> is <c>w0 × 1000</c> (§9.2.4 puts glyph
    /// space at one-thousandth of text space for every font type but Type 3), so it is divided down
    /// here before use. <see cref="DecodedGlyph.IsSpaceCode"/> and
    /// <see cref="DecodedGlyph.CodeLength"/> together decide whether <paramref name="wordSpacing"/>
    /// applies: §9.3.3 states word spacing "shall be applied to every occurrence of the single-byte
    /// character code 32 … It shall not apply to occurrences of the byte value 32 in multiple-byte
    /// codes", so both conditions are required even though every simple-font code today has
    /// <c>CodeLength == 1</c>; a composite font's multi-byte code 32 is what makes the second
    /// conjunct load-bearing once #98's later PRs land Type0 fonts.
    /// </param>
    /// <param name="fontSize">Tfs, from the graphics state's own <c>Tf</c>.</param>
    /// <param name="charSpacing">Tc (§9.3.2): added for every glyph, code 32 included.</param>
    /// <param name="wordSpacing">Tw (§9.3.3): added only when this glyph is a single-byte code 32.</param>
    /// <param name="horizontalScaling">Th as <c>Tz</c> sets it, a percentage of normal glyph width
    /// (§9.3.4); converted here. §9.3.4 scopes Th to the whole horizontal bracket — "it shall also
    /// affect the spacing parameters Tc and Tw, as well as any positioning adjustments performed by
    /// the TJ operator" — so it multiplies <paramref name="charSpacing"/> and <paramref
    /// name="wordSpacing"/> too, not only the glyph's own width.</param>
    internal static double ComputeGlyphDisplacement(
        DecodedGlyph glyph, double fontSize, double charSpacing, double wordSpacing,
        double horizontalScaling)
    {
        var th = horizontalScaling / 100.0;
        var w0 = glyph.Width / 1000.0;
        var appliesWordSpacing = glyph.IsSpaceCode && glyph.CodeLength == 1;
        return (w0 * fontSize + charSpacing + (appliesWordSpacing ? wordSpacing : 0)) * th;
    }

    /// <summary>
    /// <see cref="TextAssembler"/>'s line-grouping key: the signed perpendicular distance from the
    /// origin to <paramref name="trm"/>'s translation (<see cref="Matrix.E"/>, <see
    /// cref="Matrix.F"/>), projected onto the direction normal to the baseline <paramref
    /// name="trm"/>'s linear part carries. Not simply <see cref="Matrix.F"/>: that IS the
    /// perpendicular-to-baseline coordinate only when the baseline runs parallel to the device
    /// x-axis (an unrotated page, the common case, where this reduces to plain <c>F</c> exactly,
    /// since <c>B</c> is 0 there); under a rotation, or any CTM/Tm with a non-zero <c>B</c>, <c>F</c>
    /// is instead the coordinate ALONG the advancing baseline, so every glyph on one rotated line
    /// computed a distinct key and opened its own one-glyph run (#417 round 2: "Hello" under a 90°
    /// rotation extracted as "H\ne\nl\nl\no").
    /// <para>
    /// Showing a glyph advances the text matrix by a multiple of <c>(A, B)</c> — the device-space
    /// direction text-space <c>(1, 0)</c> maps to — so <paramref name="trm"/>'s <c>(E, F)</c> moves
    /// along <c>(A, B)</c> as later glyphs on the same line paint, which is exactly the direction
    /// this projection is orthogonal to: the key stays constant along one line whatever direction
    /// the baseline runs in device space, and changes only when the pen moves perpendicular to it,
    /// which is what a real line break does. This is a heuristic of this reader's, not an ISO
    /// 32000-2 formula: the specification does not define "the same line" for extracted text at
    /// all.
    /// </para>
    /// <paramref name="trm"/> should already have rise forced to zero, the same way <see
    /// cref="ComputeTextRenderingMatrix"/>'s <c>rise</c> parameter allows: rise can reach either
    /// <c>E</c> or <c>F</c> through a non-zero <c>C</c> in the Tm/CTM (not only <c>F</c>, once the
    /// page is rotated), and must not affect line grouping any more than it does when the page is
    /// not rotated. Degenerate (a zero-magnitude baseline direction, from a zero font size or
    /// horizontal scaling) or already non-finite input naturally propagates to a non-finite result,
    /// which <see cref="TextAssembler"/>'s line comparison already treats as "same line as before".
    /// <para>
    /// Computed as <c>(A/|A,B|)·F − (B/|A,B|)·E</c> — the direction cosines of the baseline times
    /// <paramref name="trm"/>'s translation — rather than the algebraically equivalent
    /// <c>(A·F − B·E)/|A,B|</c> the remarks above describe: dividing BEFORE multiplying keeps every
    /// intermediate value bounded by <paramref name="trm"/>'s own components, whereas forming
    /// <c>A·F</c>, <c>B·E</c>, or <c>A²+B²</c> directly first can overflow to <c>±Infinity</c> even
    /// when <paramref name="trm"/> itself, and the correct key, are both comfortably finite (an
    /// individually-finite but extreme CTM/Tm compounding through <see
    /// cref="ComputeTextRenderingMatrix"/> reaches components around 10^71–10^72 before this method
    /// ever sees them; squaring or multiplying two such values overflows IEEE 754 double's own
    /// ~1.8×10^308 range, though neither the inputs nor the true result do). #417 round 2's own
    /// regression: two genuinely different, ordinary-scale lines under such a CTM both computed
    /// <c>NaN</c> the naive way, which <see cref="TextAssembler"/> then read as "same line",
    /// collapsing them together.
    /// </para>
    /// </summary>
    internal static double ComputeLineKey(Matrix trm)
    {
        var magnitude = Hypot(trm.A, trm.B);
        return trm.A / magnitude * trm.F - trm.B / magnitude * trm.E;
    }

    // A numerically stable sqrt(a^2 + b^2) (the classic scale-by-the-larger-term technique):
    // never squares either argument directly, so it stays finite whenever the larger of the two
    // itself is, even where a naive Math.Sqrt(a*a + b*b) would overflow first (see
    // ComputeLineKey's own remarks). Returns 0 when both arguments are exactly 0, and propagates
    // NaN/Infinity unchanged when either argument already is one.
    private static double Hypot(double a, double b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        if (b > a)
            (a, b) = (b, a);
        if (a == 0)
            return 0;

        var ratio = b / a;
        return a * Math.Sqrt(1 + ratio * ratio);
    }

    /// <summary>
    /// A <c>TJ</c> array's own numeric element, per Table 107's prose rather than §9.4.4's
    /// algebraic fold into the next glyph's own displacement: "shall be subtracted from the current
    /// horizontal or vertical coordinate", a standalone translation
    /// <c>tx = -(tj / 1000) × Tfs × Th</c> with neither <c>Tc</c> nor <c>Tw</c> added, since those
    /// are per-glyph, not per-adjustment. Table 107's prose is total — defined even for a number at
    /// the end of the array, or two numbers in a row, where "the next glyph" §9.4.4's own fold
    /// would need does not exist — which is why this reader implements the operator this way rather
    /// than the clause's algebraically equivalent one. A positive <paramref name="tj"/> moves the
    /// next glyph LEFT (subtracted, per Table 107), not right.
    /// </summary>
    internal static double ComputeNumericAdjustment(double tj, double fontSize, double horizontalScaling)
    {
        var th = horizontalScaling / 100.0;
        return -(tj / 1000.0) * fontSize * th;
    }
}
