// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Property-based invariants for the layout engine. Two cover the render itself: it never escapes
/// its declared exception contract, and it never places content outside the page box. Both are
/// stated here over the corner of the input space that holds today
/// (<see cref="LayoutGen.ValidDoc"/>). Widening either to a corner where a defect lives belongs in
/// the pull request that fixes it, where the property has to fail before the fix and pass after;
/// that is the only arrangement that shows it discriminates. #468 is the worked example: the
/// generator's cell word is held at two characters because generating it turns that defect into a
/// failing property.
///
/// The rest close a coverage gap rather than a defect. <c>LayoutBox</c> is public and shipped and
/// is named by no test in the repository at all; no test reads an <c>EdgeInsets</c> member, so
/// <c>Horizontal</c> and <c>Vertical</c>, the two the renderer's geometry check depends on, were
/// unasserted. <c>ColorRgb</c> is the weaker case: a Barcodes test already pins <c>FromHex</c> and
/// the kernel conversion together through the emitted <c>1 0 1 rg</c>, so what is new here is
/// per-channel coverage rather than first coverage.
/// </summary>
public sealed class PropertyTests
{
    // Coordinates reach the content stream through PdfCanvas's "0.#####" format, so the quantum is
    // 1e-5pt and the worst rounding error is half of that. This sits 200 times above it, which is
    // slack rather than a derived figure: 0.001pt is 0.00035mm, far below anything a viewer could
    // show, and it keeps the assertion about the layout rather than the formatter's last digit.
    private const double Tolerance = 0.001;

    // Enough to absorb the last bits of a differently-ordered sum at these magnitudes, and far
    // too small to hide a geometric error.
    private const double FloatSlack = 1e-9;

    // ── (a) Render invariants ────────────────────────────────────────────────

    /// <summary>
    /// A document built from valid input renders without throwing and produces a well-formed
    /// PDF. This is the exception-contract half: for this corner of the space the contract is
    /// that there is no exception at all.
    /// </summary>
    [Fact]
    public void ValidDocument_rendersWithoutThrowing()
    {
        LayoutGen.ValidDoc.Sample(spec =>
        {
            var bytes = LayoutGen.Render(spec);

            Assert.True(bytes.Length > 100, $"PDF too short ({bytes.Length} bytes) for {Describe(spec)}");
            Assert.Equal((byte)'%', bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
            Assert.Equal((byte)'D', bytes[2]);
            Assert.Equal((byte)'F', bytes[3]);
        }, iter: FuzzBudget.Iterations);
    }

    /// <summary>
    /// Nothing a document positions lies outside its own page box.
    ///
    /// This is containment, not placement, and the difference is worth stating because the name
    /// invites the stronger reading. Any misplacement that stays inside the page passes. Two
    /// measured examples: dropping the halving from the band's centre-alignment arm turns a centred
    /// band into a right-aligned one, and dropping the header height from the renderer's content
    /// area drops content on top of the header band. Both leave every property green. What the
    /// invariant does catch is a coordinate that leaves the page, which is the defect class the
    /// pull requests after this one are about.
    ///
    /// The floor is the generated margin rather than <see cref="Tolerance"/>, because a table fills
    /// the content box exactly, so its right edge sits a margin's width inside the page. On a
    /// uniform margin that would be a loose floor. Measured with an atomic counter, because
    /// CsCheck samples in parallel and a plain increment loses updates, it drives the margin
    /// generator hard toward its lower bound: roughly a quarter of 410 samples land below 0.01pt
    /// and one to three hit exact zero. So the zero-margin edge is reached often, and a 0.01pt
    /// cell shift was caught in three runs out of three.
    /// </summary>
    [Fact]
    public void ValidDocument_placesNothingOutsideThePage()
    {
        LayoutGen.ValidDoc.Sample(spec =>
        {
            var stream = PdfTestUtil.DecompressAllFlatStreams(LayoutGen.Render(spec));

            var extent = ContentStreamReadback.GeometryExtent(stream);
            Assert.NotNull(extent);
            var e = extent!.Value;
            Assert.True(e.MinX >= -Tolerance, $"x {e.MinX} is left of the page for {Describe(spec)}");
            Assert.True(e.MinY >= -Tolerance, $"y {e.MinY} is below the page for {Describe(spec)}");
            Assert.True(e.MaxX <= spec.PageWidth + Tolerance,
                $"x {e.MaxX} is right of the {spec.PageWidth}pt page for {Describe(spec)}");
            Assert.True(e.MaxY <= spec.PageHeight + Tolerance,
                $"y {e.MaxY} is above the {spec.PageHeight}pt page for {Describe(spec)}");

            // A baseline origin alone does not bound text: a left-aligned run that starts inside
            // the box and is wider than the box escapes to the right with its origin still legal.
            // Every document this suite builds uses Helvetica, so its own metrics give the exact
            // right edge rather than an estimate.
            // A loop over an empty list asserts nothing, so the placements have to be counted
            // before they are bounded. Asserting merely that some text was shown does not
            // discriminate: measured, stubbing ParagraphRenderer.Draw to return immediately left
            // all fourteen properties green, because the table cells and the bands still emit
            // literals. So the counts are pinned per element instead, which they can be because
            // the shapes are known: the paragraph shows one literal, and every list item and every
            // table cell shows the cell word once. A renderer that draws nothing now fails here
            // rather than passing quietly.
            var placements = ContentStreamReadback.TextPlacements(stream);
            Assert.Single(placements, p => p.Text == LayoutGen.ParagraphText(spec));
            Assert.Equal(
                spec.ItemCount + spec.ColumnCount,
                placements.Count(p => p.Text == spec.Word));

            foreach (var t in placements)
            {
                var right = t.X + Standard14Metrics.MeasureString(Standard14.Helvetica, t.Text, t.FontSize);
                Assert.True(right <= spec.PageWidth + Tolerance,
                    $"text \"{t.Text}\" ends at {right}, right of the {spec.PageWidth}pt page "
                    + $"for {Describe(spec)}");
            }
        }, iter: FuzzBudget.Iterations);
    }

    // ── (b) EdgeInsets ───────────────────────────────────────────────────────

    [Fact]
    public void EdgeInsets_horizontalAndVertical_areTheEdgeSums()
    {
        LayoutGen.Insets.Sample(i =>
        {
            Assert.Equal(i.Left + i.Right, i.Horizontal);
            Assert.Equal(i.Top + i.Bottom, i.Vertical);
        }, iter: FuzzBudget.Iterations);
    }

    [Fact]
    public void EdgeInsets_uniformConstructor_setsEveryEdge()
    {
        LayoutGen.SignedPoints.Sample(v =>
        {
            var i = new EdgeInsets(v);
            Assert.Equal(v, i.Top);
            Assert.Equal(v, i.Right);
            Assert.Equal(v, i.Bottom);
            Assert.Equal(v, i.Left);
        }, iter: FuzzBudget.Iterations);
    }

    [Fact]
    public void EdgeInsets_twoValueConstructor_pairsOppositeEdges()
    {
        Gen.Select(LayoutGen.SignedPoints, LayoutGen.SignedPoints).Sample(pair =>
        {
            var (topBottom, leftRight) = pair;
            var i = new EdgeInsets(topBottom, leftRight);
            Assert.Equal(topBottom, i.Top);
            Assert.Equal(topBottom, i.Bottom);
            Assert.Equal(leftRight, i.Left);
            Assert.Equal(leftRight, i.Right);
        }, iter: FuzzBudget.Iterations);
    }

    // ── (c) LayoutBox ────────────────────────────────────────────────────────

    [Fact]
    public void LayoutBox_rightAndBottom_areOriginPlusExtent()
    {
        LayoutGen.Box.Sample(b =>
        {
            Assert.Equal(b.X + b.Width, b.Right);
            Assert.Equal(b.Y + b.Height, b.Bottom);
        }, iter: FuzzBudget.Iterations);
    }

    [Fact]
    public void LayoutBox_deflate_movesTheOriginInAndShrinksBothExtents()
    {
        Gen.Select(LayoutGen.Box, LayoutGen.Insets).Sample(pair =>
        {
            var (box, insets) = pair;
            var d = box.Deflate(insets);

            // The origin moves by exactly one addition, so this is exact.
            Assert.Equal(box.X + insets.Left, d.X);
            Assert.Equal(box.Y + insets.Top, d.Y);

            // The extents are not. Deflate subtracts the two edges in sequence while Horizontal
            // and Vertical add them first, and floating-point addition is not associative: at a
            // width of 509.04 with insets of 514 and 445.71, the two orders differ by one unit in
            // the last place, 5.68e-14. The tolerance is what makes this a statement about the geometry rather
            // than about the order of two subtractions.
            Assert.Equal(box.Width - insets.Horizontal, d.Width, FloatSlack);
            Assert.Equal(box.Height - insets.Vertical, d.Height, FloatSlack);

            // Deflating by non-negative insets can never widen the box.
            Assert.True(d.Width <= box.Width);
            Assert.True(d.Height <= box.Height);
        }, iter: FuzzBudget.Iterations);
    }

    [Fact]
    public void LayoutBox_withHeightAndWithY_changeOnlyWhatTheyName()
    {
        Gen.Select(LayoutGen.Box, LayoutGen.SignedPoints).Sample(pair =>
        {
            var (box, v) = pair;

            var h = box.WithHeight(v);
            Assert.Equal(box.X, h.X);
            Assert.Equal(box.Y, h.Y);
            Assert.Equal(box.Width, h.Width);
            Assert.Equal(v, h.Height);

            var y = box.WithY(v);
            Assert.Equal(box.X, y.X);
            Assert.Equal(v, y.Y);
            Assert.Equal(box.Width, y.Width);
            Assert.Equal(box.Height, y.Height);
        }, iter: FuzzBudget.Iterations);
    }

    [Fact]
    public void LayoutBox_isEmpty_iffAnExtentIsNotPositive()
    {
        LayoutGen.Box.Sample(b =>
            Assert.Equal(b.Width <= 0 || b.Height <= 0, b.IsEmpty),
            iter: FuzzBudget.Iterations);
    }

    // ── (d) ColorRgb ─────────────────────────────────────────────────────────

    [Fact]
    public void ColorRgb_roundTripsThroughTheKernelColour()
    {
        LayoutGen.Rgb.Sample(c =>
        {
            VellumPdf.Graphics.KernelColor kernel = c;
            ColorRgb back = kernel;
            Assert.Equal(c, back);

            // The round trip alone is an involution: swap two channels in both operators and it
            // still holds. Pinning the channels on the way out is what makes a one-sided or
            // symmetric ordering error visible.
            Assert.Equal(c.R, kernel.R);
            Assert.Equal(c.G, kernel.G);
            Assert.Equal(c.B, kernel.B);
        }, iter: FuzzBudget.Iterations);
    }

    [Fact]
    public void ColorRgb_fromHex_recoversEveryByteChannel()
    {
        Gen.Select(Gen.Byte, Gen.Byte, Gen.Byte).Sample(rgb =>
        {
            var (r, g, b) = rgb;
            var packed = ((uint)r << 16) | ((uint)g << 8) | b;
            var c = ColorRgb.FromHex(packed);

            Assert.Equal(r, (int)Math.Round(c.R * 255));
            Assert.Equal(g, (int)Math.Round(c.G * 255));
            Assert.Equal(b, (int)Math.Round(c.B * 255));
        }, iter: FuzzBudget.Iterations);
    }

    // ── (e) Measurement and markers ──────────────────────────────────────────

    /// <summary>
    /// Pins the width a style reports to a value derived from the shipped metrics table, at every
    /// generated font size.
    ///
    /// This replaced a non-negativity and monotonic-append property, which was worth nothing here
    /// for two reasons. Kernel's suite already states the same two predicates over Helvetica and
    /// random strings, and separately proves per-glyph non-negativity exhaustively across every
    /// Standard 14 face and the whole 16-bit char space. And both predicates hold under every
    /// plausible mutation of the code they name: advances are non-negative, so dropping the
    /// point-size scale, dividing by 100 instead of 1000, or substituting another face's table all
    /// keep a sum non-negative and keep appending a glyph from shrinking it. Measured: deleting
    /// <c>* pointSize</c> left all 14 properties green, and all three of those mutations now fail.
    ///
    /// It also matters that the page-box invariant computes its right edge with the same function
    /// the renderer used to place the text, so a metrics error cancels there. This is the one place
    /// the metrics are held against an independent number.
    /// </summary>
    [Fact]
    public void MeasureString_matchesTheShippedAdvanceWidths()
    {
        // Helvetica advances in thousandths of an em, read out of Standard14Metrics' own table:
        // W is 944, g is 556, space is 278.
        const double emW = 944, emG = 556, emSpace = 278;

        LayoutGen.FontSize.Sample(size =>
        {
            var style = new TextStyle { FontSize = size };

            Assert.Equal(emW / 1000.0 * size, style.MeasureString("W"), FloatSlack);
            Assert.Equal((emW + emG) / 1000.0 * size, style.MeasureString("Wg"), FloatSlack);
            Assert.Equal((3 * (emW + emG) + 2 * emSpace) / 1000.0 * size,
                style.MeasureString("Wg Wg Wg"), FloatSlack);
            Assert.Equal(0.0, style.MeasureString(""));
        }, iter: FuzzBudget.Iterations);
    }

    [Fact]
    public void FormatMarker_isNeverEmpty_forAnyStyleAtAOneBasedIndex()
    {
        Gen.Select(LayoutGen.Bullets, Gen.Int[1, 5000]).Sample(pair =>
        {
            var (style, index) = pair;
            var marker = new ListElement(style).FormatMarker(index);
            Assert.False(string.IsNullOrEmpty(marker), $"{style} produced no marker at index {index}");
        }, iter: FuzzBudget.Iterations);
    }

    [Fact]
    public void FormatMarker_orderedStyles_areDistinctAcrossConsecutiveIndices()
    {
        Gen.Select(
            Gen.OneOfConst(ListStyle.OrderedDecimal, ListStyle.OrderedAlpha, ListStyle.OrderedRoman),
            Gen.Int[1, 2000]).Sample(pair =>
        {
            var (style, index) = pair;
            var list = new ListElement(style);
            Assert.NotEqual(list.FormatMarker(index), list.FormatMarker(index + 1));
        }, iter: FuzzBudget.Iterations);
    }

    private static string Describe(LayoutGen.DocSpec s) =>
        $"page {s.PageWidth:F1}x{s.PageHeight:F1}, margin {s.Margins.Left:F1}, {s.FontSize:F1}pt, " +
        $"{s.Alignment}, {s.ListStyle}, {s.ItemCount} items, header={s.Header ?? "none"}, " +
        $"footer={s.Footer ?? "none"}";

    private static class FuzzBudget
    {
        private const long DefaultIterations = 500;

        /// <summary>
        /// Iterations per property run, overridable via <c>VELLUMPDF_FUZZ_ITER</c>. Its own copy
        /// for the reason the Reader suite's copies already give: lifting it into a shared file
        /// would touch <c>VellumPdf.TestSupport</c>, which several test projects build against.
        /// The default is lower than the Reader suite's 3,000 because each iteration here renders
        /// and reparses a whole document rather than decoding one buffer.
        /// </summary>
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
