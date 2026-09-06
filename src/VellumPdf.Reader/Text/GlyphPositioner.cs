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
    /// own prose, not this clause's algebraic fold into the next glyph — see that method's own
    /// remarks for why).
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
