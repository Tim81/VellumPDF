// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A measured extent positioned content without checking that it fit, so a centre or right
/// alignment could place a coordinate outside its box. Four sites shared that mechanism: an image
/// or chart wider than the content box, a list marker wider than the indent, and an unbreakable
/// glyph wider than the box. This class pins each one, plus two known-answer cases for the exact
/// curve-extent walk every image and chart assertion here depends on.
///
/// Sectioned like <see cref="RunningBandFitTests"/>: (a) image, (b) chart, (c) list marker,
/// (d) paragraph glyph, (e) the reader's own curve extent.
/// </summary>
public sealed class OffPagePlacementTests
{
    private static readonly PdfImageXObject FixtureImage =
        PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng());

    private static readonly IReadOnlyList<PieSlice> ChartSlices =
        [new PieSlice(3, ColorRgb.Black), new PieSlice(1, new ColorRgb(1, 0, 0))];

    // ── (a) Image, Centre ─────────────────────────────────────────────────────

    /// <summary>
    /// Page 400x500, margin 50, content box [50, 350], explicit Height 20 (image margins default
    /// to zero). Before the fix, Width 300.0001 gave x 49.99995 and Width 320 gave x 40 — both
    /// left of the 50pt margin's mirror image on the right, i.e. the box. The clamp in
    /// LayoutImageRenderer.Layout fits Width to the 300pt content box, so both now match the exact
    /// Width-300 case: x 50, right edge 350.
    /// </summary>
    [Theory]
    [InlineData(300.0)]
    [InlineData(300.0001)]
    [InlineData(320.0)]
    public void Image_centreWiderThanTheBox_fitsTheContentBoxExactly(double width)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 500),
            Margins = new EdgeInsets(50),
        };
        doc.Add(new LayoutImage(FixtureImage)
        {
            Width = width,
            Height = 20,
            Alignment = HorizontalAlignment.Center,
        });

        var extent = Extent(doc);
        Assert.Equal(50.0, extent.MinX, 0.001);
        Assert.Equal(350.0, extent.MaxX, 0.001);
    }

    // ── (b) Chart, Centre ─────────────────────────────────────────────────────

    /// <summary>
    /// Page 400x900, margin 50, content box [50, 350], default start angle, slice values 3 and 1.
    /// With the chart's own <c>Margins</c> set to zero, before the fix a Diameter of 320 gave an
    /// operand extent of [40, 360]; the clamp fits it to the 300pt content box instead.
    /// </summary>
    [Fact]
    public void Chart_zeroMarginsWiderThanTheBox_fitsTheContentBoxExactly()
    {
        var extent = ChartExtent(diameter: 320, margins: EdgeInsets.Zero);
        Assert.Equal(50.0, extent.MinX, 0.001);
        Assert.Equal(350.0, extent.MaxX, 0.001);
    }

    /// <summary>
    /// The row that decides the fix. With the chart's default 6pt margins, a Diameter of 300 spans
    /// exactly the 300pt content box today — [50, 350] — because the clamp measures against
    /// <c>ctx.Area.Width</c> before the chart's own margins deflate it. Clamping against the
    /// deflated 288pt width instead (revision 1's approach) would move this to [56, 344], which is
    /// what this test refuses: it is a correct document, and no byte in it should move.
    /// </summary>
    [Fact]
    public void Chart_defaultMargins_diameterEqualToTheAreaWidth_isUnmoved()
    {
        var extent = ChartExtent(diameter: 300, margins: null);
        Assert.Equal(50.0, extent.MinX, 0.001);
        Assert.Equal(350.0, extent.MaxX, 0.001);
    }

    /// <summary>
    /// Same page and default margins, but a diameter that already fits the deflated 288pt width.
    /// Unaffected either way — included because it is the second half of the row that decides the
    /// fix, and a clamp that quietly widened every diameter would move this one too.
    /// </summary>
    [Fact]
    public void Chart_defaultMargins_diameterSmallerThanTheAreaWidth_isUnaffected()
    {
        var extent = ChartExtent(diameter: 288, margins: null);
        Assert.Equal(56.0, extent.MinX, 0.001);
        Assert.Equal(344.0, extent.MaxX, 0.001);
    }

    private static ContentStreamReadback.Extent ChartExtent(double diameter, EdgeInsets? margins)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 900),
            Margins = new EdgeInsets(50),
        };
        var chart = margins is { } m
            ? new PieChart { Diameter = diameter, Margins = m, Slices = ChartSlices, Alignment = HorizontalAlignment.Center }
            : new PieChart { Diameter = diameter, Slices = ChartSlices, Alignment = HorizontalAlignment.Center };
        doc.Add(chart);
        return Extent(doc);
    }

    private static ContentStreamReadback.Extent Extent(Document doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        return ContentStreamReadback.GeometryExtent(stream)!.Value;
    }

    // ── (c) List marker ───────────────────────────────────────────────────────

    /// <summary>
    /// OrderedRoman, Helvetica 10pt, Indent 20. Item 24's marker, "xxiv.", is exactly 20.00pt and
    /// abuts the content without widening the gutter. Item 27's marker, "xxvii.", is 22.22pt — the
    /// first to overprint before the fix — and item 38's, "xxxviii.", is 29.44pt. Both strings are
    /// present in the stream either way, overprinted before the fix, so this asserts the content
    /// paragraph's own Tm x rather than which text exists.
    /// </summary>
    [Fact]
    public void ListMarker_widerThanTheIndent_widensItsOwnItemsGutter()
    {
        const double indent = 20;
        var style = new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 700),
            Margins = EdgeInsets.Zero,
        };
        var list = new ListElement(ListStyle.OrderedRoman) { DefaultStyle = style, Indent = indent };
        for (var i = 1; i <= 38; i++)
            list.Add(new ListItem($"c{i}", style));
        doc.Add(list);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        // Item 1's marker, "i.", is 5.0pt — far under the indent — a second sanity check that an
        // easily-fitting marker keeps today's exact gutter alongside item 24's exact-fit boundary.
        Assert.Equal(20.0, ContentX(placements, "c1"), 0.001);
        Assert.Equal(20.0, ContentX(placements, "c24"), 0.001);
        Assert.Equal(22.22, ContentX(placements, "c27"), 0.001);
        Assert.Equal(29.44, ContentX(placements, "c38"), 0.001);
    }

    /// <summary>
    /// The nested arm has the same defect at indent * 2, and the fix is not
    /// <c>Math.Max(indent * 2, markerWidth)</c>: that would never widen, since indent * 2 already
    /// exceeds any marker that would have widened the top-level gutter. It widens past
    /// indent * 2 only by what the marker needs beyond its own indent-wide gutter. Nested ordered
    /// markers restart at 1 per parent, so this needs a parent with 27 or more children to reach
    /// the same "xxvii." boundary as the top-level case.
    /// </summary>
    [Fact]
    public void ListMarker_nestedGutter_widensPastIndentTimesTwo()
    {
        const double indent = 20;
        var style = new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 900),
            Margins = EdgeInsets.Zero,
        };
        var list = new ListElement(ListStyle.OrderedRoman) { DefaultStyle = style, Indent = indent };
        var parent = new ListItem("parent", style);
        for (var i = 1; i <= 27; i++)
            parent.AddChild(new ListItem($"n{i}", style));
        list.Add(parent);
        doc.Add(list);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        Assert.Equal(42.22, ContentX(placements, "n27"), 0.001);
    }

    private static string RenderAndDecompress(Document doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    private static double ContentX(List<ContentStreamReadback.TextPlacement> placements, string text) =>
        placements.Single(p => p.Text == text).X;

    // ── (d) Paragraph glyph ───────────────────────────────────────────────────

    /// <summary>
    /// Page 200x600, margin 50, content box [50, 150]. At size 106 a single "W" measures 100.064pt
    /// against the 100pt box: HardBreakWord always emits the first rune of a word that cannot be
    /// broken further, so before the fix Centre gave x 49.968 and Right would give the same
    /// negative-offset formula its own answer. Floored at zero, both start exactly at the box's
    /// left edge.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    public void ParagraphGlyph_widerThanTheBox_startsAtTheBoxLeftEdge(HorizontalAlignment alignment)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, 600),
            Margins = new EdgeInsets(50),
        };
        var style = new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 106 };
        doc.Add(new Paragraph("W", style) { Alignment = alignment });

        var placement = Assert.Single(ContentStreamReadback.TextPlacements(RenderAndDecompress(doc)));

        Assert.Equal(50.0, placement.X, 0.001);
    }

    // ── (e) The reader's own curve extent ─────────────────────────────────────

    /// <summary>
    /// The same construction PieChartRenderer's single-slice branch uses: MoveTo the arc start,
    /// one full-circle AppendArc, ClosePath. At this start angle the earlier control-hull reader
    /// measured up to 1.13216 times the diameter — 19.825pt of operand past the nominal edge on
    /// each side, at a diameter of 300 — while the drawn curve itself only bulges past the nominal
    /// circle by up to 0.0408pt at that diameter. The tolerance here sits above that bulge and
    /// nowhere near the hull's overshoot, so this discriminates a walk that bounds the drawn curve
    /// from one that still bounds its control points.
    /// </summary>
    [Fact]
    public void GeometryExtent_fullCircleControlHullOvershoots_readsTheDrawnCurveInstead()
    {
        const double cx = 200, cy = 200, r = 150, startAngle = 1.2;

        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 400, 400), EdgeInsets.Zero);
        renderer.Add(new ArcProbeRenderer(cx, cy, r, startAngle));

        var ms = new MemoryStream();
        renderer.Render(ms);
        var extent = ContentStreamReadback.GeometryExtent(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()))!.Value;

        const double tolerance = 0.1;
        Assert.Equal(cx - r, extent.MinX, tolerance);
        Assert.Equal(cx + r, extent.MaxX, tolerance);
        Assert.Equal(cy - r, extent.MinY, tolerance);
        Assert.Equal(cy + r, extent.MaxY, tolerance);
    }

    /// <summary>
    /// A curve's start has to come from the current point the preceding operator left behind, not
    /// from the origin: p0 = (500, 500), and the six numbers on the c line alone are (500, 600,
    /// 600, 600, 600, 500) — the two control points and the end point. A walk that started every
    /// curve at (0, 0) instead would report an extent reaching down to x = 0, y = 0; this one
    /// stays entirely inside [500, 600] x [500, 575], which is exact rather than approximate:
    /// worked by hand, x is monotonic across the curve (its derivative is 600t(1 - t), non-negative
    /// on [0, 1]) so the endpoints alone bound it, and y's one interior extremum falls at t = 0.5,
    /// where evaluating the curve gives 575.
    /// </summary>
    [Fact]
    public void GeometryExtent_curveExtent_dependsOnThePrecedingMoveto()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 700, 700), EdgeInsets.Zero);
        renderer.Add(new CurveProbeRenderer());

        var ms = new MemoryStream();
        renderer.Render(ms);
        var extent = ContentStreamReadback.GeometryExtent(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()))!.Value;

        Assert.Equal(500.0, extent.MinX, 1e-9);
        Assert.Equal(600.0, extent.MaxX, 1e-9);
        Assert.Equal(500.0, extent.MinY, 1e-9);
        Assert.Equal(575.0, extent.MaxY, 1e-9);
    }

    /// <summary>Draws MoveTo(the arc start) then one full-circle AppendArc, then ClosePath.</summary>
    private sealed class ArcProbeRenderer(double cx, double cy, double r, double startAngle) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context) =>
            context.Canvas
                .MoveTo(cx + (r * Math.Cos(startAngle)), cy + (r * Math.Sin(startAngle)))
                .AppendArc(cx, cy, r, startAngle, startAngle + (2 * Math.PI))
                .ClosePath();
    }

    /// <summary>Draws a single MoveTo followed by one CurveTo, with hand-computed extrema.</summary>
    private sealed class CurveProbeRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context) =>
            context.Canvas.MoveTo(500, 500).CurveTo(500, 600, 600, 600, 600, 500);
    }
}
