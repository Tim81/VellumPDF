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
    /// Table 107: Th scales the ENTIRE horizontal bracket, including Tc and Tw, not only the
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
        CaseGen.Sample(c => AssertBothImplementationsAgree(c), iter: FuzzBudget.Iterations);
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
        }, iter: FuzzBudget.Iterations);

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

            var tolerance = 1e-6 + 1e-9 * Math.Max(
                Math.Max(Math.Abs(productionTrm.E), Math.Abs(productionTrm.F)),
                Math.Max(Math.Abs(referenceTrm3[2, 0]), Math.Abs(referenceTrm3[2, 1])));

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
