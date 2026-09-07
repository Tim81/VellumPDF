// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using VellumPdf.Reader.Content;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Text;

/// <summary>
/// Known-answer tests for ISO 32000-2 §9.4.4's own arithmetic, each worked out by hand from the
/// clause text quoted in this PR's brief, plus a differential property test against an
/// independently written reference (#98).
/// </summary>
public sealed class GlyphPositionerTests
{
    private const double Tolerance = 1e-9;

    private static void AssertClose(double expected, double actual, string what) =>
        Assert.True(
            Math.Abs(expected - actual) <= Tolerance,
            $"{what}: expected {expected}, got {actual}");

    private static void AssertMatrix(Matrix expected, Matrix actual)
    {
        AssertClose(expected.A, actual.A, "A");
        AssertClose(expected.B, actual.B, "B");
        AssertClose(expected.C, actual.C, "C");
        AssertClose(expected.D, actual.D, "D");
        AssertClose(expected.E, actual.E, "E");
        AssertClose(expected.F, actual.F, "F");
    }

    // ── Trm composition (§9.4.4) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the whole <c>Trm = parameters × Tm × CTM</c> composition if broken: parameters =
    /// [12 0 0 12 0 0] (Tfs=12, Th=100%, Trise=0), Tm = Identity, Ctm = [2 0 0 2 100 50].
    /// Hand-computed: parameters·Tm = parameters (Tm is identity); parameters·Ctm = [24 0 0 24 100
    /// 50].
    /// </summary>
    [Fact]
    public void ComputeTextRenderingMatrix_composesParametersTimesTmTimesCtm()
    {
        var trm = GlyphPositioner.ComputeTextRenderingMatrix(
            fontSize: 12, horizontalScaling: 100, rise: 0, textMatrix: Matrix.Identity,
            ctm: new Matrix(2, 0, 0, 2, 100, 50));

        AssertMatrix(new Matrix(24, 0, 0, 24, 100, 50), trm);
    }

    /// <summary>
    /// The same composition under a ROTATED (non-diagonal) CTM, [0 1 -1 0 5 5] (a 90° rotation plus
    /// translation) — deliberately non-symmetric so a transposed multiply, invisible against a
    /// diagonal CTM, produces a different, wrong answer here. Hand-computed: parameters = [10 0 0 10
    /// 0 0] (Tfs=10); parameters·Ctm: A=10×0+0×(-1)=0, B=10×1+0×0=10, C=0×0+10×(-1)=-10,
    /// D=0×1+10×0=0, E=0×0+0×(-1)+5=5, F=0×1+0×0+5=5.
    /// </summary>
    [Fact]
    public void ComputeTextRenderingMatrix_composesCorrectly_underARotatedCtm()
    {
        var trm = GlyphPositioner.ComputeTextRenderingMatrix(
            fontSize: 10, horizontalScaling: 100, rise: 0, textMatrix: Matrix.Identity,
            ctm: new Matrix(0, 1, -1, 0, 5, 5));

        AssertMatrix(new Matrix(0, 10, -10, 0, 5, 5), trm);
    }

    /// <summary>
    /// Rise (§9.3.7) enters Trm's own F component and nowhere else: with Tfs=10, Th=100%, Trise=3,
    /// Tm=CTM=Identity, Trm = [10 0 0 10 0 3]. The SAME call with rise forced to zero (what <see
    /// cref="PositionedGlyph.LineY"/> uses for line grouping) instead yields F=0: the two calls
    /// share every argument but rise, so a defect that let rise leak into the LINE key rather than
    /// only the painted Trm would show up as these two F values agreeing when they must not.
    /// </summary>
    [Fact]
    public void Rise_entersTrmFOnly_neverTheUnrisenLineKey()
    {
        var painted = GlyphPositioner.ComputeTextRenderingMatrix(
            fontSize: 10, horizontalScaling: 100, rise: 3, textMatrix: Matrix.Identity, ctm: Matrix.Identity);
        var lineKey = GlyphPositioner.ComputeTextRenderingMatrix(
            fontSize: 10, horizontalScaling: 100, rise: 0, textMatrix: Matrix.Identity, ctm: Matrix.Identity);

        AssertMatrix(new Matrix(10, 0, 0, 10, 0, 3), painted);
        AssertClose(0, lineKey.F, "unrisen line key");
        Assert.NotEqual(painted.F, lineKey.F);
    }

    /// <summary>
    /// The same composition with a NON-100% horizontal scaling, deliberately combined with a
    /// non-diagonal CTM so a defect that scaled the wrong pair of parameter-matrix components (or
    /// dropped Th from the parameter matrix entirely, applying it only to <see
    /// cref="GlyphPositioner.ComputeGlyphDisplacement"/>'s advance instead) cannot hide behind
    /// every other KAT and the differential test's generator both happening to exercise Th=100
    /// three times over (see <see cref="Generator_reachesItsStatedRanges"/>'s remarks).
    /// Hand-computed: parameters = [Tfs·Th 0 0 Tfs 0 Trise] = [5 0 0 10 0 0] (Tfs=10,
    /// Th=50%); parameters·Ctm with Ctm=[0 1 -1 0 5 5]: A=5×0+0×(-1)=0, B=5×1+0×0=5,
    /// C=0×0+10×(-1)=-10, D=0×1+10×0=0, E=0×0+0×(-1)+5=5, F=0×1+0×0+5=5. A defect using Tfs (10)
    /// in place of Tfs·Th (5) for A/B would instead give A=0, B=10 — different enough from A=0,
    /// B=5 that this KAT is discriminating.
    /// </summary>
    [Fact]
    public void ComputeTextRenderingMatrix_appliesTh_underARotatedCtm_withNonDefaultScaling()
    {
        var trm = GlyphPositioner.ComputeTextRenderingMatrix(
            fontSize: 10, horizontalScaling: 50, rise: 0, textMatrix: Matrix.Identity,
            ctm: new Matrix(0, 1, -1, 0, 5, 5));

        AssertMatrix(new Matrix(0, 5, -10, 0, 5, 5), trm);
    }

    // ── Line-grouping key (a heuristic of this reader's, not an ISO formula) ────────────────────

    /// <summary>
    /// Under an UNROTATED page (Ctm's B is 0), the key reduces to plain F exactly: the whole
    /// point of <see cref="GlyphPositioner.ComputeLineKey"/> is that it agrees with the simpler,
    /// pre-existing "just read F" approach in the common case and differs only once the page is
    /// rotated.
    /// </summary>
    [Fact]
    public void ComputeLineKey_reducesToPlainF_whenUnrotated()
    {
        var trm = GlyphPositioner.ComputeTextRenderingMatrix(
            fontSize: 12, horizontalScaling: 100, rise: 0, textMatrix: Matrix.Identity,
            ctm: new Matrix(2, 0, 0, 2, 100, 37));

        AssertClose(trm.F, GlyphPositioner.ComputeLineKey(trm), "unrotated line key");
    }

    /// <summary>
    /// The discriminating fixture for #417's rotated-text defect: under a 90° rotation (Ctm =
    /// [0 1 -1 0 0 0]), two glyphs at different points ALONG the baseline (simulated the way text
    /// extraction actually advances between glyphs: <c>Matrix.Translation(tx, 0).Concat(tm)</c>)
    /// must compute the SAME key, even though their <c>F</c> values differ (0 and 10 — reading
    /// plain <c>F</c>, the defect this method exists to fix, would put every glyph on its own
    /// line here). A third glyph moved PERPENDICULAR to the
    /// baseline instead (a genuine line break under this same rotation) must compute a DIFFERENT
    /// key from the first two. Hand-computed: parameters = [12 0 0 12 0 0] (Tfs=12); glyph 1's Tm =
    /// Identity gives Trm = [0 12 -12 0 0 0]; glyph 2's Tm = [1 0 0 1 10 0] (advanced by tx=10
    /// along the baseline) gives Trm = [0 12 -12 0 0 10] — different F (0 vs 10), same key (both
    /// 0, since A×F-B×E over |A,B|=12 is (0×F-12×0)/12 = 0 for glyph 1 and (0×10-12×0)/12 = 0 for
    /// glyph 2). Glyph 3's Tm = [1 0 0 1 0 5] (shifted perpendicular to the baseline in text
    /// space) gives Trm = [0 12 -12 0 -5 0]; key = (0×0-12×(-5))/12 = 5, different from 0.
    /// </summary>
    [Fact]
    public void ComputeLineKey_sameAlongBaseline_differsAcrossARealLineBreak_underRotation()
    {
        var ctm = new Matrix(0, 1, -1, 0, 0, 0);

        var trm1 = GlyphPositioner.ComputeTextRenderingMatrix(12, 100, 0, Matrix.Identity, ctm);
        var advancedTm = Matrix.Translation(10, 0).Concat(Matrix.Identity);
        var trm2 = GlyphPositioner.ComputeTextRenderingMatrix(12, 100, 0, advancedTm, ctm);
        var perpendicularTm = new Matrix(1, 0, 0, 1, 0, 5);
        var trm3 = GlyphPositioner.ComputeTextRenderingMatrix(12, 100, 0, perpendicularTm, ctm);

        AssertClose(0, trm1.F, "glyph 1 F");
        AssertClose(10, trm2.F, "glyph 2 F");
        Assert.NotEqual(trm1.F, trm2.F);

        var key1 = GlyphPositioner.ComputeLineKey(trm1);
        var key2 = GlyphPositioner.ComputeLineKey(trm2);
        var key3 = GlyphPositioner.ComputeLineKey(trm3);

        AssertClose(0, key1, "glyph 1 key");
        AssertClose(key1, key2, "glyph 2 key (same baseline)");
        AssertClose(5, key3, "glyph 3 key (real line break)");
        Assert.NotEqual(key1, key3);
    }

    /// <summary>
    /// MEDIUM 2 (#417 round 4): every other <c>ComputeLineKey</c> call in this file, and every one
    /// <c>TextExtractionVisitor</c> ever makes, has <c>A</c> or <c>B</c> exactly 0 (an unrotated
    /// page, or a rotation that is an exact multiple of 90°), so <see cref="GlyphPositioner"/>'s own
    /// <c>Hypot</c> always takes its <c>b == 0</c> shortcut and returns <c>a</c> unchanged — the
    /// scaling branch (<c>a * Math.Sqrt(1 + ratio * ratio)</c>) never runs under the whole suite.
    /// <c>return a;</c> in that branch's place still passes every other KAT here (a mutant this PR's
    /// own review caught surviving), because two glyphs on the SAME wrongly-scaled line still agree
    /// with EACH OTHER; only a fixture that pins the correct ABSOLUTE key, not merely equality
    /// between two glyphs, tells the mutant apart from the real formula. A 45° CTM
    /// <c>[√2/2 √2/2 −√2/2 √2/2 0 0]</c> makes <c>A = B</c> (so <c>ratio</c> is exactly 1, avoiding
    /// the <c>b == 0</c> shortcut entirely) with Tfs=12, Tm=<c>[1 0 0 1 100 700]</c>: hand-computed,
    /// <c>A = B = 6√2 ≈ 8.485281374</c>, <c>E = -300√2 ≈ -424.264069</c>, <c>F = 400√2 ≈
    /// 565.685425</c>, <c>Hypot(A, B) = 12</c> (since <c>6√2 · √2 = 12</c>), and
    /// <c>key = (A/12)·F − (B/12)·E = (√2/2)·400√2 − (√2/2)·(−300√2) = 400 − (−300) = 700</c>
    /// exactly. <c>return a;</c> instead yields <c>A = 6√2 ≈ 8.485…</c> as the "magnitude", giving a
    /// key of roughly 989.949 — clearly not 700, but STILL equal for both glyphs, which is why a
    /// same-line-only assertion would have let it through. A second glyph advanced 50 TEXT-SPACE
    /// units along the baseline (<c>Matrix.Translation(50, 0).Concat(tm)</c>, the same idiom
    /// <c>TextExtractionVisitor.ShowString</c> uses between glyphs) must compute the SAME 700: moving
    /// along the baseline is exactly the direction this projection is orthogonal to.
    /// </summary>
    [Fact]
    public void ComputeLineKey_pinsExactValue_atANonAxisAlignedAngle_notMerelyGlyphEquality()
    {
        var root2Over2 = Math.Sqrt(2) / 2;
        var ctm = new Matrix(root2Over2, root2Over2, -root2Over2, root2Over2, 0, 0);
        var tm = new Matrix(1, 0, 0, 1, 100, 700);

        var trm1 = GlyphPositioner.ComputeTextRenderingMatrix(12, 100, 0, tm, ctm);
        AssertClose(6 * Math.Sqrt(2), trm1.A, "glyph 1 A");
        AssertClose(6 * Math.Sqrt(2), trm1.B, "glyph 1 B");
        AssertClose(-300 * Math.Sqrt(2), trm1.E, "glyph 1 E");
        AssertClose(400 * Math.Sqrt(2), trm1.F, "glyph 1 F");

        var advancedTm = Matrix.Translation(50, 0).Concat(tm);
        var trm2 = GlyphPositioner.ComputeTextRenderingMatrix(12, 100, 0, advancedTm, ctm);

        var key1 = GlyphPositioner.ComputeLineKey(trm1);
        var key2 = GlyphPositioner.ComputeLineKey(trm2);

        AssertClose(700.0, key1, "glyph 1 key, pinned absolute value");
        AssertClose(700.0, key2, "glyph 2 key (advanced along the baseline), pinned absolute value");
    }

    /// <summary>
    /// A regression this PR's own round-2 review found empirically, not one a reviewer named: an
    /// individually-finite but extreme CTM (10^170-scale, reached the same way the overflow
    /// fixtures in <c>TextExtractionEndToEndTests</c> reach one) composes, through <see
    /// cref="ComputeTextRenderingMatrix"/>, to a Trm around 10^71–10^72 — still comfortably finite,
    /// and still the same magnitude for two genuinely DIFFERENT, ordinary-scale text lines under
    /// that CTM. The naive <c>(A·F − B·E)/√(A²+B²)</c> form of the projection squares or multiplies
    /// components that size directly, which overflows to <c>±Infinity</c> well before the division
    /// would bring the result back into range — so two lines 100 units apart both computed
    /// <c>NaN</c>, which <see cref="TextAssembler"/> reads as "same line", collapsing genuinely
    /// distinct lines together (caught by
    /// <see cref="TextExtractionEndToEndTests.NonFiniteLineY_doesNotStickPastTheExcursion_laterDistinctLinesStillSeparate"/>
    /// failing before this method's own division-before-multiplication fix). Pinned here directly,
    /// without going through content-stream parsing, so a future regression fails fast.
    /// </summary>
    [Fact]
    public void ComputeLineKey_doesNotOverflowToNaN_forLargeButFiniteBaselines()
    {
        var huge = double.Parse(
            "1" + new string('0', 170) + ".0", System.Globalization.CultureInfo.InvariantCulture);
        var ctm = new Matrix(huge, 0, 0, huge, huge, huge);

        var line1 = GlyphPositioner.ComputeTextRenderingMatrix(12, 100, 0, new Matrix(1, 0, 0, 1, 100, 700), ctm);
        var line2 = GlyphPositioner.ComputeTextRenderingMatrix(12, 100, 0, new Matrix(1, 0, 0, 1, 100, 600), ctm);

        var key1 = GlyphPositioner.ComputeLineKey(line1);
        var key2 = GlyphPositioner.ComputeLineKey(line2);

        Assert.True(double.IsFinite(key1), $"key1 was {key1}, expected finite");
        Assert.True(double.IsFinite(key2), $"key2 was {key2}, expected finite");
        Assert.NotEqual(key1, key2);
    }

    /// <summary>
    /// LOW 1 (#417 round 4): on an UNROTATED page (<c>B == 0</c>), an overflowing <c>E</c> (or
    /// <c>F</c>) used to poison the whole key through <c>0.0 / magnitude * E == NaN</c> — <c>B</c>'s
    /// own zero contribution should drop out of the formula entirely instead, since <c>0 × ∞</c> is
    /// <c>NaN</c> under IEEE 754 regardless of which factor overflowed. Reachable from the same
    /// all-digit-literal overflow family <see
    /// cref="ComputeLineKey_doesNotOverflowToNaN_forLargeButFiniteBaselines"/> above uses, except
    /// here only the TRANSLATION overflows (<paramref name="trm"/>'s <c>A</c>/<c>B</c> stay
    /// ordinary), so <c>Hypot</c> itself is not exercised at all and this is pinned directly against
    /// a synthesized <see cref="Matrix"/> instead.
    /// </summary>
    [Fact]
    public void ComputeLineKey_zeroTimesInfinity_doesNotPoisonTheKey_onAnUnrotatedPage()
    {
        var trm = new Matrix(12, 0, 0, 12, double.PositiveInfinity, 700);

        var key = GlyphPositioner.ComputeLineKey(trm);

        AssertClose(700.0, key, "unrotated key with an overflowing E");
    }

    /// <summary>
    /// #417 round 5: the mirror of the test above, for the <c>a == 0</c> short-circuit rather than
    /// <c>b == 0</c>. On a 90°-family rotation (<c>A == 0</c>), an overflowing <c>F</c> used to
    /// poison the whole key through <c>0.0 / magnitude * F == NaN</c> the same way an overflowing
    /// <c>E</c> did on an unrotated page; <c>A</c>'s own zero contribution must drop out instead.
    /// Deleting <c>a == 0 ? 0.0 :</c> alone (leaving the <c>b == 0</c> guard in place) leaves this
    /// failing: <c>term1</c> becomes <c>0 / 12 * (+Infinity) == NaN</c>, and the key becomes
    /// <c>NaN - 100 == NaN</c> instead of the correct <c>-100</c>.
    /// </summary>
    [Fact]
    public void ComputeLineKey_zeroTimesInfinity_doesNotPoisonTheKey_onARotatedPage()
    {
        var trm = new Matrix(0, 12, -12, 0, 100, double.PositiveInfinity);

        var key = GlyphPositioner.ComputeLineKey(trm);

        AssertClose(-100.0, key, "rotated key with an overflowing F");
    }

    /// <summary>
    /// LOW 2 (#417 round 4): a magnitude too large to represent as a finite <see cref="double"/> —
    /// reachable once <c>|A| = |B|</c> exceeds roughly <c>1.271×10^308</c> (<see
    /// cref="GlyphPositioner"/>'s own private <c>Hypot</c> then has its <c>a·√2</c> exceed
    /// <see cref="double.MaxValue"/>) — must not silently round every
    /// direction cosine below to 0 and report a deceptively FINITE key of exactly 0: two real,
    /// distinct lines that both overflow this way would then merge on that shared 0 instead of
    /// propagating the non-finite result <see cref="TextAssembler"/> already knows how to treat as
    /// "same line as before" without actually claiming a specific, wrong position.
    /// </summary>
    [Fact]
    public void ComputeLineKey_overflowingMagnitude_returnsNaN_notADeceptiveZero()
    {
        const double huge = 1.3e308;
        var trm1 = new Matrix(huge, huge, -huge, huge, 100, 700);
        var trm2 = new Matrix(huge, huge, -huge, huge, 100, 600);

        var key1 = GlyphPositioner.ComputeLineKey(trm1);
        var key2 = GlyphPositioner.ComputeLineKey(trm2);

        Assert.True(double.IsNaN(key1), $"key1 was {key1}, expected NaN");
        Assert.True(double.IsNaN(key2), $"key2 was {key2}, expected NaN");
    }

    /// <summary>
    /// #417 round 5: a zero-magnitude baseline direction (<c>A == B == 0</c>, from a zero font size
    /// or horizontal scaling) must return <c>NaN</c>, not a deceptively FINITE 0. Deleting the
    /// explicit <c>magnitude == 0</c> check alone (leaving <c>!double.IsFinite(magnitude)</c> in
    /// place) leaves this failing: with both <c>A</c> and <c>B</c> exactly 0, the <c>a == 0</c> and
    /// <c>b == 0</c> short-circuits skip both terms of the subtraction and return exactly 0 for
    /// <paramref name="trm"/>'s own <c>F</c>/<c>E</c> of 700/100 alike — the same collapse two
    /// genuinely different degenerate lines would share.
    /// </summary>
    [Fact]
    public void ComputeLineKey_zeroMagnitude_returnsNaN_notADeceptiveZero()
    {
        var trm = new Matrix(0, 0, 0, 0, 100, 700);

        var key = GlyphPositioner.ComputeLineKey(trm);

        Assert.True(double.IsNaN(key), $"key was {key}, expected NaN");
    }

    /// <summary>
    /// LOW 3 (#417 round 4): a negative <c>Tfs</c> flips <c>A</c> and <c>B</c> end to end through
    /// the parameters matrix's own <c>Tfs·Th</c> term, without touching <c>E</c> or <c>F</c> at all
    /// (those trace only to Tm's and the CTM's own translation) — so two glyphs on the exact SAME
    /// physical baseline that differ only in the sign of <c>Tfs</c> must still key identically.
    /// Concretely, <c>BT /F1 12 Tf 100 700 Td (AB) Tj -12 Tf (CD) Tj ET</c>: before this fix, "AB"
    /// keyed 700 and "CD" keyed -700, splitting one physical line into two.
    /// </summary>
    [Fact]
    public void ComputeLineKey_signCanonicalized_negativeFontSizeMidLine_doesNotSplitTheLine()
    {
        var tm = new Matrix(1, 0, 0, 1, 100, 700);
        var positiveTfsTrm = GlyphPositioner.ComputeTextRenderingMatrix(12, 100, 0, tm, Matrix.Identity);
        var negativeTfsTrm = GlyphPositioner.ComputeTextRenderingMatrix(-12, 100, 0, tm, Matrix.Identity);

        var positiveKey = GlyphPositioner.ComputeLineKey(positiveTfsTrm);
        var negativeKey = GlyphPositioner.ComputeLineKey(negativeTfsTrm);

        AssertClose(700.0, positiveKey, "positive Tfs key");
        AssertClose(700.0, negativeKey, "negative Tfs key, canonicalized to match");
    }

    // ── Per-glyph displacement (§9.4.4, §9.3.3) ─────────────────────────────────────────────────

    /// <summary>
    /// §9.3.3: word spacing applies "to every occurrence of the single-byte character code 32 …
    /// It shall not apply to occurrences of the byte value 32 in multiple-byte codes." Three glyphs,
    /// same Width/Tfs/Tc/Tw/Th, differing only in Code/CodeLength/IsSpaceCode: a single-byte code 32
    /// gets Tw added; a MULTI-byte code 32 (CodeLength 2 — untestable through any simple font today,
    /// but load-bearing once a composite font's own multi-byte code 32 lands) does not; an ordinary
    /// non-32 code does not either. Hand-computed with w0=250/1000=0.25, Tfs=10, Tc=1, Tw=5, Th=100%:
    /// tx = (0.25×10 + 1 + 5)×1.0 = 8.5 with Tw, (0.25×10 + 1)×1.0 = 3.5 without.
    /// </summary>
    [Fact]
    public void ComputeGlyphDisplacement_wordSpacing_onlySingleByteCode32()
    {
        var singleByteSpace = new DecodedGlyph(Code: 32, CodeLength: 1, Width: 250, Unicode: " ", IsSpaceCode: true);
        var multiByteSpace = new DecodedGlyph(Code: 32, CodeLength: 2, Width: 250, Unicode: " ", IsSpaceCode: true);
        var ordinaryGlyph = new DecodedGlyph(Code: 65, CodeLength: 1, Width: 250, Unicode: "A", IsSpaceCode: false);

        AssertClose(8.5, GlyphPositioner.ComputeGlyphDisplacement(singleByteSpace, 10, 1, 5, 100), "single-byte code 32");
        AssertClose(3.5, GlyphPositioner.ComputeGlyphDisplacement(multiByteSpace, 10, 1, 5, 100), "multi-byte code 32");
        AssertClose(3.5, GlyphPositioner.ComputeGlyphDisplacement(ordinaryGlyph, 10, 1, 5, 100), "non-32 code");
    }

    /// <summary>
    /// §9.3.4: Th scales the ENTIRE horizontal bracket, including Tc and Tw, not only the
    /// glyph's own width. With w0=0.2 (Width 200), Tfs=10, Tc=2, Tw=3, Th=50%: tx = (0.2×10 + 2 +
    /// 3) × 0.5 = 3.5. A defect that let Th scale only the width term would instead give
    /// (0.2×10×0.5) + 2 + 3 = 6.0 — different enough from 3.5 that this KAT is discriminating.
    /// </summary>
    [Fact]
    public void ComputeGlyphDisplacement_thScales_theWholeBracket_notOnlyWidth()
    {
        var glyph = new DecodedGlyph(Code: 32, CodeLength: 1, Width: 200, Unicode: " ", IsSpaceCode: true);

        var tx = GlyphPositioner.ComputeGlyphDisplacement(glyph, fontSize: 10, charSpacing: 2, wordSpacing: 3, horizontalScaling: 50);

        AssertClose(3.5, tx, "Th-scaled displacement");
    }

    // ── TJ numeric adjustment (Table 107) ───────────────────────────────────────────────────────

    /// <summary>
    /// A positive <c>TJ</c> number moves the next glyph LEFT (Table 107: "subtracted from the
    /// current horizontal … coordinate"), and Th scales it too. tj=200, Tfs=10, Th=100%: tx =
    /// -(200/1000)×10×1.0 = -2.0. The same tj at Th=50%: tx = -(0.2)×10×0.5 = -1.0. Getting the sign
    /// backwards (a common defect this PR's brief calls out explicitly) would produce +2.0 instead.
    /// </summary>
    [Fact]
    public void ComputeNumericAdjustment_positiveTjMovesLeft_andThScalesIt()
    {
        AssertClose(-2.0, GlyphPositioner.ComputeNumericAdjustment(tj: 200, fontSize: 10, horizontalScaling: 100), "Th=100%");
        AssertClose(-1.0, GlyphPositioner.ComputeNumericAdjustment(tj: 200, fontSize: 10, horizontalScaling: 50), "Th=50%");
    }

    [Fact]
    public void ComputeNumericAdjustment_negativeTjMovesRight()
    {
        AssertClose(2.0, GlyphPositioner.ComputeNumericAdjustment(tj: -200, fontSize: 10, horizontalScaling: 100), "negative tj");
    }

    // ── Differential property test ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Independently reimplements §9.4.4's arithmetic with explicit <c>double[3,3]</c> row-vector
    /// matrices (not <see cref="Matrix.Concat"/>) and the clause's own algebraic fold of a trailing
    /// <c>TJ</c> number into the glyph it follows (not the production's own standalone-translation
    /// implementation of Table 107's prose), then checks both sides paint every glyph at the same
    /// origin to a stated tolerance. Differing in both mechanics (raw arrays vs. the record-struct
    /// composer) and formulation (one combined tx per glyph vs. two separate translations) is what
    /// lets this catch a transposed compose or a misplaced Th that hand-picked KAT vectors could
    /// both let through unnoticed.
    /// <para>
    /// A trailing <c>TJ</c> number folds cleanly into the glyph BEFORE it algebraically (proven by
    /// hand: two consecutive translations commute, so translating by the glyph's own tx and then by
    /// the adjustment's tx is the same net displacement as one combined
    /// <c>((w0 - tj/1000)×Tfs + Tc + Tw)×Th</c> translation) precisely because production applies
    /// the SAME adjustment as a standalone move immediately after that glyph and before the next one
    /// paints — so this generator only ever places an adjustment between two glyphs, never after the
    /// last one in a run, which is Table 107's own "next glyph does not exist" case this fold cannot
    /// represent (covered separately by an end-to-end KAT with a trailing numeric <c>TJ</c>
    /// element instead).
    /// </para>
    /// <para>
    /// Measured generator reach (asserted by <see cref="Generator_reachesItsStatedRanges"/>): Tfs
    /// spans roughly 1–144 (typical to large point sizes); Tz (horizontal scaling) spans roughly
    /// 10%–400%; Ts (rise) spans roughly -50 to 50; Tc spans roughly -5 to 20; Tw spans roughly -2
    /// to 15; a TJ adjustment spans roughly -2000 to 2000; a run holds 1 to 15 glyphs, each glyph's
    /// own width spans 0 to 1500; Tm and Ctm each vary their linear part (A, B, C, D) across roughly
    /// -2 to 2 independently (so the generated Ctm is not restricted to pure rotations) and their
    /// translation (E, F) across roughly -1000 to 1000.
    /// </para>
    /// </summary>
    [Fact]
    public void ProductionAndReferenceImplementation_agreeOnEveryGlyphOrigin()
    {
        // threads: 1: matches FontFuzzTests' own convention (see its comments) for a suite this
        // repository already found losing updates under the default multi-threaded sampler — see
        // Generator_reachesItsStatedRanges below, which shares this generator and hit that exact
        // failure mode with its own plain-double accumulators.
        CaseGen.Sample(c => AssertBothImplementationsAgree(c), iter: FuzzBudget.Iterations, threads: 1);
    }

    /// <summary>Measures the generator's own reach directly, rather than trusting the range
    /// comments above to stay accurate: a recent review round in this repository found a fuzz
    /// invariant vacuous by a factor of 2310 because nobody measured the corpus (see this PR's own
    /// brief).</summary>
    [Fact]
    public void Generator_reachesItsStatedRanges()
    {
        double minTfs = double.MaxValue, maxTfs = double.MinValue;
        double minTz = double.MaxValue, maxTz = double.MinValue;
        double minTs = double.MaxValue, maxTs = double.MinValue;
        double minTc = double.MaxValue, maxTc = double.MinValue;
        double minTw = double.MaxValue, maxTw = double.MinValue;
        var minGlyphs = int.MaxValue;
        var maxGlyphs = int.MinValue;
        var sawAdjustment = false;
        var sawNoAdjustment = false;
        var sawSpaceApplies = false;
        var sawSpaceSuppressed = false;

        CaseGen.Sample(c =>
        {
            minTfs = Math.Min(minTfs, c.Tfs); maxTfs = Math.Max(maxTfs, c.Tfs);
            minTz = Math.Min(minTz, c.Th); maxTz = Math.Max(maxTz, c.Th);
            minTs = Math.Min(minTs, c.Rise); maxTs = Math.Max(maxTs, c.Rise);
            minTc = Math.Min(minTc, c.Tc); maxTc = Math.Max(maxTc, c.Tc);
            minTw = Math.Min(minTw, c.Tw); maxTw = Math.Max(maxTw, c.Tw);
            minGlyphs = Math.Min(minGlyphs, c.Glyphs.Count);
            maxGlyphs = Math.Max(maxGlyphs, c.Glyphs.Count);
            foreach (var g in c.Glyphs)
            {
                if (g.TrailingTj is not null) sawAdjustment = true; else sawNoAdjustment = true;
                if (g.IsSpaceCode && g.CodeLength == 1) sawSpaceApplies = true;
                if (!(g.IsSpaceCode && g.CodeLength == 1)) sawSpaceSuppressed = true;
            }
            // threads: 1 (LOW 8, #417 round 4): every accumulator above is a plain double or bool,
            // not an atomic type, and CsCheck's default sampler runs the callback from multiple
            // worker threads — an instrumented run measured 2,959 of 3,000 cases actually landing
            // at the default versus exactly 3,000 with threads: 1, i.e. updates were being
            // silently lost, not merely reordered. FontFuzzTests hits the same class of hazard for
            // a different reason (a shared, non-concurrent PdfDocumentReader/FontCache) and uses
            // the same fix.
        }, iter: FuzzBudget.Iterations, threads: 1);

        Assert.True(minTfs <= 2 && maxTfs >= 140, $"Tfs reached [{minTfs}, {maxTfs}]");
        Assert.True(minTz <= 15 && maxTz >= 380, $"Tz reached [{minTz}, {maxTz}]");
        Assert.True(minTs <= -40 && maxTs >= 40, $"Ts reached [{minTs}, {maxTs}]");
        Assert.True(minTc <= -3 && maxTc >= 15, $"Tc reached [{minTc}, {maxTc}]");
        Assert.True(minTw <= -1 && maxTw >= 10, $"Tw reached [{minTw}, {maxTw}]");
        Assert.True(minGlyphs == 1, $"minimum run length reached was {minGlyphs}, expected 1");
        Assert.True(maxGlyphs >= 14, $"maximum run length reached was {maxGlyphs}, expected >= 14");
        Assert.True(sawAdjustment, "generator never produced a trailing TJ adjustment");
        Assert.True(sawNoAdjustment, "generator always produced a trailing TJ adjustment");
        Assert.True(sawSpaceApplies, "generator never produced a glyph where word spacing applies");
        Assert.True(sawSpaceSuppressed, "generator never produced a glyph where word spacing is suppressed");
    }

    private static void AssertBothImplementationsAgree(Case c)
    {
        var productionTm = new Matrix(c.TmA, c.TmB, c.TmC, c.TmD, c.TmE, c.TmF);
        var ctm = new Matrix(c.CtmA, c.CtmB, c.CtmC, c.CtmD, c.CtmE, c.CtmF);
        var referenceTm = ToMatrix3(c.TmA, c.TmB, c.TmC, c.TmD, c.TmE, c.TmF);
        var ctm3 = ToMatrix3(c.CtmA, c.CtmB, c.CtmC, c.CtmD, c.CtmE, c.CtmF);

        for (var i = 0; i < c.Glyphs.Count; i++)
        {
            var g = c.Glyphs[i];
            var decoded = new DecodedGlyph(g.Code, g.CodeLength, g.Width, Unicode: null, g.IsSpaceCode);

            // Production: paint at the current Tm, then advance by the glyph's own displacement
            // (never including a Tj term), then, if a trailing adjustment follows this glyph,
            // apply it as ITS OWN standalone translation (Table 107's prose form).
            var productionTrm = GlyphPositioner.ComputeTextRenderingMatrix(
                c.Tfs, c.Th, c.Rise, productionTm, ctm);
            var productionTx = GlyphPositioner.ComputeGlyphDisplacement(decoded, c.Tfs, c.Tc, c.Tw, c.Th);
            productionTm = Matrix.Translation(productionTx, 0).Concat(productionTm);
            if (g.TrailingTj is { } tj)
            {
                var adjustmentTx = GlyphPositioner.ComputeNumericAdjustment(tj, c.Tfs, c.Th);
                productionTm = Matrix.Translation(adjustmentTx, 0).Concat(productionTm);
            }

            // Reference: raw 3x3 arrays, and ONE combined tx per glyph folding any trailing
            // adjustment directly into §9.4.4's own literal formula.
            var paramsMatrix3 = ToMatrix3(c.Tfs * (c.Th / 100.0), 0, 0, c.Tfs, 0, c.Rise);
            var referenceTrm3 = Multiply(paramsMatrix3, Multiply(referenceTm, ctm3));
            var appliesWordSpacing = g.IsSpaceCode && g.CodeLength == 1;
            var w0 = g.Width / 1000.0;
            var th = c.Th / 100.0;
            var tjOrZero = g.TrailingTj ?? 0;
            var combinedTx = ((w0 - (tjOrZero / 1000.0)) * c.Tfs + c.Tc + (appliesWordSpacing ? c.Tw : 0)) * th;
            referenceTm = Multiply(Translate3(combinedTx), referenceTm);

            var magnitude = Math.Max(
                Math.Max(
                    Math.Max(Math.Abs(productionTrm.A), Math.Abs(productionTrm.B)),
                    Math.Max(Math.Abs(productionTrm.C), Math.Abs(productionTrm.D))),
                Math.Max(Math.Abs(productionTrm.E), Math.Abs(productionTrm.F)));
            var tolerance = 1e-6 + 1e-9 * magnitude;

            // The linear part (A/B/C/D), not only the translation (E/F): a misplaced Th (scaling
            // the wrong pair of components, or a transposed compose that swaps B and C) never
            // moves a glyph's ORIGIN, only its painted ORIENTATION and SIZE. Comparing E/F alone
            // let both mutations through (#417 round 2: this test's PR body claims it catches "a
            // transposed compose or a misplaced Th", which required this).
            Assert.True(
                Math.Abs(productionTrm.A - referenceTrm3[0, 0]) <= tolerance,
                $"glyph {i}: A differs: production {productionTrm.A}, reference {referenceTrm3[0, 0]} (case: {c})");
            Assert.True(
                Math.Abs(productionTrm.B - referenceTrm3[0, 1]) <= tolerance,
                $"glyph {i}: B differs: production {productionTrm.B}, reference {referenceTrm3[0, 1]} (case: {c})");
            Assert.True(
                Math.Abs(productionTrm.C - referenceTrm3[1, 0]) <= tolerance,
                $"glyph {i}: C differs: production {productionTrm.C}, reference {referenceTrm3[1, 0]} (case: {c})");
            Assert.True(
                Math.Abs(productionTrm.D - referenceTrm3[1, 1]) <= tolerance,
                $"glyph {i}: D differs: production {productionTrm.D}, reference {referenceTrm3[1, 1]} (case: {c})");
            Assert.True(
                Math.Abs(productionTrm.E - referenceTrm3[2, 0]) <= tolerance,
                $"glyph {i}: X differs: production {productionTrm.E}, reference {referenceTrm3[2, 0]} (case: {c})");
            Assert.True(
                Math.Abs(productionTrm.F - referenceTrm3[2, 1]) <= tolerance,
                $"glyph {i}: Y differs: production {productionTrm.F}, reference {referenceTrm3[2, 1]} (case: {c})");
        }
    }

    private static double[,] ToMatrix3(double a, double b, double c, double d, double e, double f) =>
        new double[3, 3] { { a, b, 0 }, { c, d, 0 }, { e, f, 1 } };

    private static double[,] Translate3(double tx) => ToMatrix3(1, 0, 0, 1, tx, 0);

    // Standard 3x3 matrix multiplication: point*(x·y) == (point*x)*y for a row vector on the left,
    // so Multiply(x, y) means "apply x first, then y" — the same convention Matrix.Concat uses,
    // reimplemented here with no shared code between the two.
    private static double[,] Multiply(double[,] x, double[,] y)
    {
        var result = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                double sum = 0;
                for (var k = 0; k < 3; k++)
                    sum += x[i, k] * y[k, j];
                result[i, j] = sum;
            }
        return result;
    }

    private readonly record struct GlyphCase(double Width, bool IsSpaceCode, int CodeLength, double? TrailingTj)
    {
        internal int Code => IsSpaceCode ? 32 : 65;
    }

    private readonly record struct Case(
        double Tfs, double Th, double Rise, double Tc, double Tw,
        double TmA, double TmB, double TmC, double TmD, double TmE, double TmF,
        double CtmA, double CtmB, double CtmC, double CtmD, double CtmE, double CtmF,
        IReadOnlyList<GlyphCase> Glyphs);

    // The raw per-slot values, generated independently of the OTHER slots in the same run: whether
    // this slot ends up carrying its own TrailingTj is decided afterward, in CaseGen's own combiner
    // below, since only an INTERNAL slot (not the run's last one) may keep the adjustment this
    // generator drew for it.
    private static readonly Gen<(double Width, bool IsSpaceCode, int CodeLength, double? Tj)> GlyphSlotGen =
        Gen.Select(
            Gen.Double[0.0, 1500.0], Gen.OneOfConst(true, false), Gen.OneOfConst(1, 2),
            Gen.OneOf(
                Gen.Const((double?)null),
                Gen.Double[-2000.0, 2000.0].Select(v => (double?)v)),
            (width, isSpace, codeLength, tj) => (width, isSpace, codeLength, tj));

    // CsCheck's Gen.Select tops out at 8 generators; this case needs Tfs/Th/Rise/Tc/Tw (5), Tm's
    // six components, Ctm's six components, and the glyph array — 18 values in all — so they are
    // grouped into three sub-tuples first (text parameters, Tm, Ctm) and combined with the glyph
    // array in one final 4-argument Select, rather than one flat call that would not compile.
    private static readonly Gen<(double Tfs, double Th, double Rise, double Tc, double Tw)> TextParamsGen =
        Gen.Select(
            Gen.Double[1.0, 144.0], Gen.Double[10.0, 400.0], Gen.Double[-50.0, 50.0], Gen.Double[-5.0, 20.0],
            Gen.Double[-2.0, 15.0],
            (tfs, th, rise, tc, tw) => (tfs, th, rise, tc, tw));

    private static readonly Gen<(double A, double B, double C, double D, double E, double F)> MatrixGen =
        Gen.Select(
            Gen.Double[-2.0, 2.0], Gen.Double[-2.0, 2.0], Gen.Double[-2.0, 2.0], Gen.Double[-2.0, 2.0],
            Gen.Double[-1000.0, 1000.0], Gen.Double[-1000.0, 1000.0],
            (a, b, c, d, e, f) => (a, b, c, d, e, f));

    private static readonly Gen<Case> CaseGen = Gen.Select(
        TextParamsGen, MatrixGen, MatrixGen, GlyphSlotGen.Array[1, 15],
        (textParams, tm, ctm, slots) =>
        {
            var glyphs = new List<GlyphCase>(slots.Length);
            for (var i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                // Only an INTERNAL glyph (not the last one in the run) may carry a trailing
                // adjustment: see this test's own class doc for why the fold cannot represent one
                // trailing the very last glyph.
                var tj = i < slots.Length - 1 ? s.Tj : null;
                glyphs.Add(new GlyphCase(s.Width, s.IsSpaceCode, s.CodeLength, tj));
            }
            return new Case(
                textParams.Tfs, textParams.Th, textParams.Rise, textParams.Tc, textParams.Tw,
                tm.A, tm.B, tm.C, tm.D, tm.E, tm.F, ctm.A, ctm.B, ctm.C, ctm.D, ctm.E, ctm.F,
                glyphs);
        });

    private static class FuzzBudget
    {
        private const long DefaultIterations = 3_000;

        internal static long Iterations
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("VELLUMPDF_FUZZ_ITER");
                return long.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultIterations;
            }
        }
    }
}
