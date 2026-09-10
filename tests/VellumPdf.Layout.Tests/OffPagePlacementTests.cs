// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
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
/// A fifth site shares the same mechanism but belongs to the table pull request rather than this
/// one: a table cell wider than its column, filed as #473 and measured off the left edge of a
/// 400pt page at x -83.2 under Centre and -222.4 under Right.
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
    /// to zero). Before the fix, Width 320 gave x 40 — left of the 50pt margin's mirror image on
    /// the right, i.e. the box. The clamp in LayoutImageRenderer.Layout fits Width to the 300pt
    /// content box, so it matches the exact Width-300 case: x 50, right edge 350.
    /// </summary>
    [Theory]
    [InlineData(300.0)]
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

    /// <summary>
    /// The row above does not discriminate the clamp at a Width just past the box: reverting
    /// LayoutImageRenderer.Layout's clamp (measured) gives x 49.99995 for a Width of 300.0001, and
    /// 0.001 tolerance — |50 - 49.99995| = 5e-5 — passes it either way. The tolerance here is 1e-5,
    /// below that 5e-5 defect, so this row fails when the clamp is reverted and passes when it is
    /// not; the extra 4 digits of headroom below 1e-5 down to the 5e-5 gap are for the reader, not
    /// for arithmetic noise, since the clamped value is exactly 50.0 with no floating-point residue
    /// at this input (ctx.Area.Width is 400 - 50 - 50 = 300.0 exactly, and 300.0001 - 300.0 needs
    /// no cancellation to detect).
    /// </summary>
    [Fact]
    public void Image_centreWidthFractionOverTheBox_discriminatesTheLayoutClamp()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 500),
            Margins = new EdgeInsets(50),
        };
        doc.Add(new LayoutImage(FixtureImage)
        {
            Width = 300.0001,
            Height = 20,
            Alignment = HorizontalAlignment.Center,
        });

        var extent = Extent(doc);
        Assert.Equal(50.0, extent.MinX, 1e-5);
        Assert.Equal(350.0, extent.MaxX, 1e-5);
    }

    /// <summary>
    /// Page 400x900, margin 50, content box [50, 350]. A Width of 290 with the image's own Margins
    /// set to EdgeInsets(6) fits inside the box — [56, 346] — but is wider than the 288pt width
    /// left after those margins deflate it. LayoutImageRenderer.Layout clamps Width against
    /// ctx.Area.Width (300, the box), not against that deflated 288, so nothing about this correct
    /// document moves. Clamping against 288 instead would shrink the image and, with a null
    /// Height, shrink the vertical reservation along with it — see the page-count case below for
    /// what that does.
    /// </summary>
    [Fact]
    public void Image_marginsNarrowerThanTheWidth_fitsInsideTheBox_isUnmoved()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 900),
            Margins = new EdgeInsets(50),
        };
        doc.Add(new LayoutImage(FixtureImage)
        {
            Width = 290,
            Margins = new EdgeInsets(6),
        });

        var extent = Extent(doc);
        Assert.Equal(56.0, extent.MinX, 0.001);
        Assert.Equal(346.0, extent.MaxX, 0.001);
    }

    /// <summary>
    /// Every row above picks a Width the Layout clamp fits exactly to the 300pt content box, so
    /// Left, Centre and Right all land on offset zero: replacing the Centre and Right arms of the
    /// alignment switch in LayoutImageRenderer.Draw with 0 leaves every one of them green. A Width
    /// of 100, page 400x500 margin 50, is strictly smaller than the box, so the three alignments
    /// land on three different extents instead — confirmed by making that exact replacement,
    /// rerunning this class, and seeing the Centre and Right rows below fail (both then report
    /// [50, 150], the Left row's own extent) before restoring the switch.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Left, 50.0, 150.0)]
    [InlineData(HorizontalAlignment.Center, 150.0, 250.0)]
    [InlineData(HorizontalAlignment.Right, 250.0, 350.0)]
    public void Image_smallerThanTheBox_discriminatesEveryAlignment(
        HorizontalAlignment alignment, double expectedMin, double expectedMax)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 500),
            Margins = new EdgeInsets(50),
        };
        doc.Add(new LayoutImage(FixtureImage)
        {
            Width = 100,
            Height = 20,
            Alignment = alignment,
        });

        var extent = Extent(doc);
        Assert.Equal(expectedMin, extent.MinX, 0.001);
        Assert.Equal(expectedMax, extent.MaxX, 0.001);
    }

    /// <summary>
    /// Page 400x413, margin 50 — content height 313. A Width of 290 with Margins EdgeInsets(6) and
    /// no explicit Height follows the 2x2 fixture image's 1:1 aspect ratio to a 290pt drawn height,
    /// plus the image's own 12pt of vertical margin: 302pt reserved. A one-line paragraph ahead of
    /// it adds enough height to overflow the 313pt content box, so the document paginates to two
    /// pages (measured via the page tree's own /Count entry). Before the fix, clamping Width
    /// against the deflated 288pt width shrank the null Height's aspect-ratio computation along
    /// with it, and the smaller reservation fit everything on one page instead.
    /// </summary>
    [Fact]
    public void Image_marginsNarrowerThanTheWidth_reservesTheUnclampedHeight_paginatesToTwoPages()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 413),
            Margins = new EdgeInsets(50),
        };
        var style = new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 12 };
        doc.Add(new Paragraph("Short.", style));
        doc.Add(new LayoutImage(FixtureImage) { Width = 290, Margins = new EdgeInsets(6) });

        var ms = new MemoryStream();
        doc.Save(ms);
        var pdfText = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        var countMatch = Regex.Match(pdfText, @"/Count (\d+)");

        Assert.True(countMatch.Success);
        Assert.Equal(2, int.Parse(countMatch.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// LayoutImageRenderer.Layout's own comment documents that the explicit-Width clamp is guarded
    /// on ctx.Area.Width being positive, because ordinary margins wider than the content box reach
    /// a non-positive deflated area too — and that guard only covers the explicit-Width branch, so
    /// a null Width (the default) reaches `_w = area.Width` directly regardless of it. Margins
    /// EdgeInsets(200) on a 400x1400 page's 300pt content box deflates the image's own area to
    /// -100, so _w becomes -100 with Width left null. Draw emits that as a Concat with a negative
    /// x-scale — a horizontally mirrored image — left exactly as it renders rather than absorbed,
    /// which this pins as the literal cm operands rather than merely "Draw did not throw".
    /// </summary>
    [Fact]
    public void Image_negativeDeflatedWidth_emitsTheMirroredMatrixUnclamped()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 1400),
            Margins = new EdgeInsets(50),
        };
        doc.Add(new LayoutImage(FixtureImage) { Margins = new EdgeInsets(200), Height = 50 });

        var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        var m = Regex.Match(
            stream, @"(?m)^(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm$");

        Assert.True(m.Success);
        double G(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        Assert.Equal(-100.0, G(1), 1e-9);
        Assert.Equal(0.0, G(2), 1e-9);
        Assert.Equal(0.0, G(3), 1e-9);
        Assert.Equal(50.0, G(4), 1e-9);
        Assert.Equal(250.0, G(5), 1e-9);
        Assert.Equal(1100.0, G(6), 1e-9);
    }

    // ── (b) Chart ─────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Clamping the diameter is not enough on its own, and only Centre hides that. Left and Right
    /// take their offset from the area the chart's own margins deflate, so a circle clamped to the
    /// content box is then shifted by a margin and leaves the box on one side: measured before this
    /// fix, on this page and the default 6pt margins, Left gave [56, 356] and Right [44, 344]
    /// against a content box of [50, 350]. On a page with a small document margin that is off the
    /// page rather than merely out of the box: a 452pt content box inside a 454.4pt page put Left
    /// 4.8pt past the right edge and Right 4.8pt past the left. Draw now aligns within the content
    /// box once the circle no longer fits between the margins, so all three land on the box.
    ///
    /// Centre is absent from the cases below because the test above already pins that same
    /// document, where it carries the stronger claim: not merely inside the box but unmoved.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Left)]
    [InlineData(HorizontalAlignment.Right)]
    public void Chart_defaultMargins_clampedDiameter_fitsTheContentBoxUnderEveryAlignment(
        HorizontalAlignment alignment)
    {
        var extent = ChartExtent(diameter: 300, margins: null, alignment);
        Assert.Equal(50.0, extent.MinX, 0.001);
        Assert.Equal(350.0, extent.MaxX, 0.001);
    }

    /// <summary>
    /// The other half of that rule: while the circle still fits between its own margins, the
    /// margins are honoured and nothing moves. A 288pt diameter fits the deflated 288pt width
    /// exactly, so Left sits against the left margin and Right against the right one.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Left, 56.0, 344.0)]
    [InlineData(HorizontalAlignment.Right, 56.0, 344.0)]
    public void Chart_defaultMargins_diameterFittingTheMargins_keepsThem(
        HorizontalAlignment alignment, double expectedMin, double expectedMax)
    {
        var extent = ChartExtent(diameter: 288, margins: null, alignment);
        Assert.Equal(expectedMin, extent.MinX, 0.001);
        Assert.Equal(expectedMax, extent.MaxX, 0.001);
    }

    /// <summary>
    /// The row that decides the position-clamp fix, extended to all three alignments — the case a
    /// basis switch would have broken and the position clamp preserves. Diameter 290 with the
    /// chart's default 6pt margins sits inside the 300pt content box under every alignment — Left
    /// [56, 346], Centre [55, 345], Right [54, 344] — but is wider than the 288pt width left after
    /// those margins deflate it, so aligning within the deflated area (the earlier basis-switch
    /// approach) and clamping position into the box (this fix) disagree here: measured, switching
    /// the basis instead moves Left to [50, 340] (PieChartRenderer.Draw's own comment cites this
    /// exact figure) while Centre coincidentally lands on 55 either way. This is the most valuable
    /// case in this file for exactly that reason.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Left, 56.0, 346.0)]
    [InlineData(HorizontalAlignment.Center, 55.0, 345.0)]
    [InlineData(HorizontalAlignment.Right, 54.0, 344.0)]
    public void Chart_defaultMargins_diameterFitsTheBoxButNotTheDeflatedWidth_isUnmoved(
        HorizontalAlignment alignment, double expectedMin, double expectedMax)
    {
        var extent = ChartExtent(diameter: 290, margins: null, alignment);
        Assert.Equal(expectedMin, extent.MinX, 0.001);
        Assert.Equal(expectedMax, extent.MaxX, 0.001);
    }

    /// <summary>
    /// Same shape under asymmetric margins, EdgeInsets(6, 20, 6, 10) (top, right, bottom, left): a
    /// Diameter of 275 fits the 300pt content box under every alignment — [60, 335], [57.5,
    /// 332.5], [55, 330] — but is wider than the 270pt width those margins leave (10 left + 20
    /// right deflated from 300). Asymmetric margins move the deflated area's own centre off the
    /// content box's centre, so this also pins that the clamp measures against the box's actual
    /// bounds rather than a formula that assumes the two centres coincide.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Left, 60.0, 335.0)]
    [InlineData(HorizontalAlignment.Center, 57.5, 332.5)]
    [InlineData(HorizontalAlignment.Right, 55.0, 330.0)]
    public void Chart_asymmetricMargins_diameterFitsTheBoxButNotTheDeflatedWidth_isUnmoved(
        HorizontalAlignment alignment, double expectedMin, double expectedMax)
    {
        var extent = ChartExtent(diameter: 275, margins: new EdgeInsets(6, 20, 6, 10), alignment);
        Assert.Equal(expectedMin, extent.MinX, 0.001);
        Assert.Equal(expectedMax, extent.MaxX, 0.001);
    }

    /// <summary>
    /// Every chart case above picks a Diameter the clamp fits exactly to the box, so Left, Centre
    /// and Right all land on the same offset and replacing the Centre and Right arms of the
    /// alignment switch in PieChartRenderer.Draw with 0 leaves every one of them green. A Diameter
    /// of 100 in the 300pt box, with the chart's default 6pt margins, is strictly smaller, so the
    /// three alignments land on three different extents — confirmed by making that exact
    /// replacement, rerunning this class, and seeing the Centre and Right rows below fail (both
    /// then report [56, 156], the Left row's own extent) before restoring the switch.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Left, 56.0, 156.0)]
    [InlineData(HorizontalAlignment.Center, 150.0, 250.0)]
    [InlineData(HorizontalAlignment.Right, 244.0, 344.0)]
    public void Chart_smallerThanTheBox_discriminatesEveryAlignment(
        HorizontalAlignment alignment, double expectedMin, double expectedMax)
    {
        var extent = ChartExtent(diameter: 100, margins: null, alignment);
        Assert.Equal(expectedMin, extent.MinX, 0.001);
        Assert.Equal(expectedMax, extent.MaxX, 0.001);
    }

    private static ContentStreamReadback.Extent ChartExtent(
        double diameter,
        EdgeInsets? margins,
        HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 900),
            Margins = new EdgeInsets(50),
        };
        var chart = margins is { } m
            ? new PieChart { Diameter = diameter, Margins = m, Slices = ChartSlices, Alignment = alignment }
            : new PieChart { Diameter = diameter, Slices = ChartSlices, Alignment = alignment };
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
    /// <c>Math.Max(indent * 2, markerWidth)</c>. A nested marker starts at <c>indent</c> rather
    /// than at zero, so its right edge is <c>indent + markerWidth</c>, and that is the quantity
    /// that has to clear the content's own left edge; comparing a bare marker width against
    /// <c>indent * 2</c> measures a width against a position. MEASURED: at indent 20 and Helvetica
    /// 30pt, "xxvii." is 66.66pt, so <c>Math.Max(indent * 2, markerWidth)</c> would widen there,
    /// even though it never does at the 10pt size this class otherwise uses. Nested ordered
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

    /// <summary>
    /// Page 60x4000, zero margins, OrderedRoman, Helvetica 20pt, Indent 20, 38 items whose text is
    /// "ab". Item 38's marker, "xxxviii.", is 58.88pt, which would leave 60 - 58.88 = 1.12pt of
    /// content width — under "ab"'s own 22.24pt widest-word measurement, so ListRenderer.WidestWord
    /// bounds it: the gutter keeps today's 20pt indent rather than widening into that, even though
    /// Math.Max(indent, markerWidth) alone would pick 58.88pt. Item 7's marker, "vii.", is 24.44pt,
    /// leaving 60 - 24.44 = 35.56pt — comfortably over 22.24pt — so the bound accepts the widen
    /// there. Both are asserted from the same document so a change to the bound's threshold, in
    /// either direction, is visible on whichever side it moves.
    /// </summary>
    [Fact]
    public void ListMarker_gutterBound_keepsTheIndentWhereWideningWouldShredTheWord()
    {
        const double indent = 20;
        var style = new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 20 };

        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 60, 4000),
            Margins = EdgeInsets.Zero,
        };
        var list = new ListElement(ListStyle.OrderedRoman) { DefaultStyle = style, Indent = indent };
        for (var i = 1; i <= 38; i++)
            list.Add(new ListItem("ab", style));
        doc.Add(list);
        doc.Add(new Paragraph("trailer", style));

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));
        var content = placements.Where(p => p.Text == "ab").ToList();

        Assert.Equal(38, content.Count);
        Assert.Equal(24.44, content[6].X, 0.001);  // item 7: bound accepts, widens the gutter
        Assert.Equal(20.0, content[37].X, 0.001);  // item 38: bound rejects, keeps the indent
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
    /// measured 1.13216 times the diameter — 19.825pt of operand past the nominal edge on
    /// each side, at a diameter of 300 — while the drawn curve itself only bulges past the nominal
    /// circle by up to 0.0409pt at that diameter. The tolerance here sits above that bulge and
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

    /// <summary>
    /// re is defined as m l l l h (ISO 32000-2, 8.5.2.1), so the current point after it is the
    /// rectangle's own (x, y) corner — the corner the construction starts and closes on — and a c
    /// immediately after one starts from there. Rectangle(100, 100, 50, 30) then the same curve
    /// shape as <see cref="GeometryExtent_curveExtent_dependsOnThePrecedingMoveto"/>, translated to
    /// start at (100, 100) instead of (500, 500): c1 = (100, 200), c2 = (200, 200), end =
    /// (200, 100). Bézier evaluation is affine-invariant under translation, so the same
    /// hand-worked proof applies verbatim, shifted: x is monotonic (endpoints alone bound it) and y
    /// peaks at t = 0.5, 75pt above the start — [100, 200] x [100, 175], confirmed by running this
    /// case rather than only citing the earlier proof. Mutating the re arm to hand the curve the
    /// rectangle's opposite corner (x + w, y + h) instead moves this extent measurably, which was
    /// used to confirm this case discriminates that arm before restoring it.
    /// </summary>
    [Fact]
    public void GeometryExtent_curveAfterRectangle_startsFromTheRectanglesOwnCorner()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 400, 400), EdgeInsets.Zero);
        renderer.Add(new RectangleThenCurveProbeRenderer());

        var ms = new MemoryStream();
        renderer.Render(ms);
        var extent = ContentStreamReadback.GeometryExtent(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()))!.Value;

        Assert.Equal(100.0, extent.MinX, 1e-9);
        Assert.Equal(200.0, extent.MaxX, 1e-9);
        Assert.Equal(100.0, extent.MinY, 1e-9);
        Assert.Equal(175.0, extent.MaxY, 1e-9);
    }

    /// <summary>
    /// MoveTo(0, 0), then LineTo(50, 10), then the same curve shape again, translated to start at
    /// (50, 10): c1 = (50, 110), c2 = (150, 110), end = (150, 10). This is the l arm's own
    /// current-point update under test, not merely that l is noted: if it fed the curve the m
    /// point instead of its own, the curve's x range would be [0, 100] rather than [50, 150], and
    /// the combined extent — m and l are noted directly too — would report MaxX 100 instead of
    /// 150. The m point anchors MinX and MinY at 0 either way, which is why this needs m, l and c
    /// together rather than l and c alone: a walk that ignored l entirely, current point stuck at
    /// (0, 0), would still pass a test that only checked the minimums.
    /// </summary>
    [Fact]
    public void GeometryExtent_curveAfterLineTo_startsFromTheLinesOwnEndpoint()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 400, 400), EdgeInsets.Zero);
        renderer.Add(new LineThenCurveProbeRenderer());

        var ms = new MemoryStream();
        renderer.Render(ms);
        var extent = ContentStreamReadback.GeometryExtent(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()))!.Value;

        Assert.Equal(0.0, extent.MinX, 1e-9);
        Assert.Equal(150.0, extent.MaxX, 1e-9);
        Assert.Equal(0.0, extent.MinY, 1e-9);
        Assert.Equal(85.0, extent.MaxY, 1e-9);
    }

    /// <summary>
    /// MoveTo(0, 0), LineTo(50, 10), ClosePath, then the same curve shape a third time — this time
    /// starting from (0, 0) again, because h returns the current point to the subpath's own start
    /// rather than establishing a new one: c1 = (0, 100), c2 = (100, 100), end = (100, 0). Without
    /// h's reset the curve would start from l's endpoint (50, 10) instead, as in the case above,
    /// and report a different extent; with it, the curve's own contribution collapses back onto
    /// the m/l points' range instead of extending past it, which is what discriminates the h arm
    /// specifically rather than merely re-covering the l arm's own test.
    /// </summary>
    [Fact]
    public void GeometryExtent_curveAfterClosePath_startsFromTheSubpathsOwnStart()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 400, 400), EdgeInsets.Zero);
        renderer.Add(new LineThenClosePathThenCurveProbeRenderer());

        var ms = new MemoryStream();
        renderer.Render(ms);
        var extent = ContentStreamReadback.GeometryExtent(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()))!.Value;

        Assert.Equal(0.0, extent.MinX, 1e-9);
        Assert.Equal(100.0, extent.MaxX, 1e-9);
        Assert.Equal(0.0, extent.MinY, 1e-9);
        Assert.Equal(75.0, extent.MaxY, 1e-9);
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

    /// <summary>Draws a Rectangle (re), then one CurveTo starting from its own corner.</summary>
    private sealed class RectangleThenCurveProbeRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context) =>
            context.Canvas.Rectangle(100, 100, 50, 30).CurveTo(100, 200, 200, 200, 200, 100);
    }

    /// <summary>Draws MoveTo, LineTo, then one CurveTo starting from the LineTo's own endpoint.</summary>
    private sealed class LineThenCurveProbeRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context) =>
            context.Canvas.MoveTo(0, 0).LineTo(50, 10).CurveTo(50, 110, 150, 110, 150, 10);
    }

    /// <summary>Draws MoveTo, LineTo, ClosePath, then one CurveTo starting from the subpath's own start.</summary>
    private sealed class LineThenClosePathThenCurveProbeRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context) =>
            context.Canvas.MoveTo(0, 0).LineTo(50, 10).ClosePath().CurveTo(0, 100, 100, 100, 100, 0);
    }
}
