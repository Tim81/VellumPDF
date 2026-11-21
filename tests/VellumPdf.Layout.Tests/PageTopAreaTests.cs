// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #549: the area offered at the top of a page. On A4 with a 72pt bottom, left and right margin,
/// the top margin picks the geometry class. The height from the top of the content area to its
/// bottom, <c>(Y + H) - Y</c>, is one rounding step below the content area's own height H at a
/// top margin of 440.16 (short), one step above it at 440.04 (long) and equal to it at 100
/// (equal). With a 16pt footer the same three margins fall in the same three classes.
/// </summary>
public sealed class PageTopAreaTests
{
    private const double PageW = 595.28;
    private const double PageH = 841.89;
    private static readonly PdfRectangle A4 = new(0, 0, PageW, PageH);
    private static readonly TextStyle Helv = new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    public static TheoryData<string, double, bool> Geometries => new()
    {
        { "short", 440.16, false },
        { "long", 440.04, false },
        { "equal", 100, false },
        { "short", 440.16, true },
        { "long", 440.04, true },
        { "equal", 100, true },
    };

    private static double FooterHeight(bool footer) => footer ? 16 : 0;

    // The expressions DocumentRenderer.ContentArea evaluates, so the box equals it field by field.
    private static LayoutBox Content(double top, bool footer) =>
        new(72, top, PageW - 144, PageH - (top + 72) - FooterHeight(footer));

    private static DocumentRenderer Renderer(double top, bool footer, PdfDocument? pdf = null) =>
        new(pdf ?? NewPdf(), A4, new EdgeInsets(top, 72, 72, 72))
        {
            Footer = footer ? new RunningBand("p {page}/{pages}", Helv) { Height = 16 } : null,
        };

    private static byte[] Save(DocumentRenderer renderer) => Render(renderer);

    private static string Streams(byte[] pdf) => PdfTestUtil.DecompressAllFlatStreams(pdf);

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    /// <summary>Records each area and fresh-page area it is offered; places a fixed height.</summary>
    private sealed class Recording(double need) : IRenderer
    {
        internal List<(LayoutBox Area, LayoutBox? Fresh)> Calls { get; } = [];

        public LayoutResult Layout(LayoutContext context)
        {
            Calls.Add((context.Area, context.FreshPageArea));
            if (context.Area.Height < need)
                return LayoutResult.Nothing();
            return LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, context.Area.Width, need));
        }

        public void Draw(DrawContext context) { }
    }

    // Returns Nothing for an area at least `limit` tall and fits a shorter one: not a supported renderer.
    private sealed class FitsSmallerNotLarger(double limit) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            context.Area.Height >= limit
                ? LayoutResult.Nothing()
                : LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, context.Area.Width, 10));

        public void Draw(DrawContext context) { }
    }

    // ── The fixture: the three classes are what the names say ──

    [Theory]
    [MemberData(nameof(Geometries))]
    public void Geometry_isTheClassItsNameSays(string kind, double top, bool footer)
    {
        var content = Content(top, footer);
        var left = content.Bottom - content.Y;
        switch (kind)
        {
            case "short": Assert.True(left < content.Height); break;
            case "long": Assert.True(left > content.Height); break;
            default: Assert.True(left == content.Height); break;
        }
    }

    // ── An element exactly as tall as the content area ──

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ExactContentHeight_savesOnePage_withOneCallPerPass(string kind, double top, bool footer)
    {
        _ = kind;
        var content = Content(top, footer);
        var element = new Recording(content.Height);
        var renderer = Renderer(top, footer);
        renderer.Add(element);

        var pdf = Save(renderer);

        Assert.Equal(1, PageCount(pdf));
        // A footer adds the page-counting pass, which lays the element out once more.
        Assert.Equal(footer ? 2 : 1, element.Calls.Count);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void PageTopArea_isTheLargerOfTheContentHeightAndTheHeightLeft(string kind, double top, bool footer)
    {
        var content = Content(top, footer);
        var element = new Recording(1);
        var renderer = Renderer(top, footer);
        renderer.Add(element);
        Save(renderer);

        var expected = Math.Max(content.Height, content.Bottom - content.Y);
        foreach (var (area, _) in element.Calls)
        {
            Assert.True(SameBits(top, area.Y), kind);
            Assert.True(SameBits(expected, area.Height), $"{kind}: offered {area.Height:R}, expected {expected:R}");
        }
    }

    [Fact]
    public void ShortGeometry_pageTopBox_equalsTheContentAreaInEveryField()
    {
        var content = Content(440.16, false);
        var element = new Recording(1);
        var renderer = Renderer(440.16, false);
        renderer.Add(element);
        Save(renderer);

        var area = Assert.Single(element.Calls).Area;
        Assert.True(SameBits(content.X, area.X));
        Assert.True(SameBits(content.Y, area.Y));
        Assert.True(SameBits(content.Width, area.Width));
        Assert.True(SameBits(content.Height, area.Height));
        Assert.True(SameBits(content.Bottom, area.Bottom));
    }

    [Fact]
    public void ExactContentHeight_afterAParagraph_isPlacedOnTheNextPage_inTheShortGeometry()
    {
        const double top = 440.16;
        var content = Content(top, false);
        var element = new Recording(content.Height);
        var renderer = Renderer(top, false);
        renderer.Add(new ParagraphRenderer(new Paragraph("before", Helv)));
        renderer.Add(element);

        var pdf = Save(renderer);

        Assert.Equal(2, PageCount(pdf));
        // Mid-page, then the retry, then the page-top call that places it.
        Assert.Equal(3, element.Calls.Count);
        var (midPage, _) = element.Calls[0];
        Assert.True(midPage.Y > top);
        Assert.True(SameBits(content.Bottom, midPage.Bottom));
        Assert.Equal(content, element.Calls[1].Area);
        Assert.Equal(content, element.Calls[2].Area);
    }

    [Fact]
    public void LongGeometry_anElementNeedingTheHeightLeft_isRefusedAfterContent_asBefore()
    {
        const double top = 440.04;
        var content = Content(top, false);
        var element = new Recording(content.Bottom - content.Y);
        var renderer = Renderer(top, false);
        renderer.Add(new ParagraphRenderer(new Paragraph("before", Helv)));
        renderer.Add(element);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(renderer));

        Assert.StartsWith("An element is too tall to fit on a single page", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, element.Calls.Count);
    }

    [Fact]
    public void LongGeometry_anElementNeedingTheHeightLeft_isPlacedAtThePageTop_asBefore()
    {
        const double top = 440.04;
        var content = Content(top, false);
        var element = new Recording(content.Bottom - content.Y);
        var renderer = Renderer(top, false);
        renderer.Add(element);

        var pdf = Save(renderer);

        Assert.Equal(1, PageCount(pdf));
        Assert.Single(element.Calls);
    }

    // ── Elements the built-in renderers measure ──

    private static byte[] TwoByTwoBmp()
    {
        const int size = 54 + 8 * 2;
        var b = new byte[size];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(b, 2);
        BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(2).CopyTo(b, 18);
        BitConverter.GetBytes(2).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);
        BitConverter.GetBytes((short)24).CopyTo(b, 28);
        BitConverter.GetBytes(16).CopyTo(b, 34);
        return b;
    }

    [Theory]
    [InlineData(440.16, false, "10 0 0 329.73 72 72")]
    [InlineData(440.16, true, "10 0 0 313.73 72 88")]
    [InlineData(440.04, false, "10 0 0 329.85 72 72")]
    [InlineData(100, false, "10 0 0 669.89 72 72")]
    public void ImageAsTallAsTheContentArea_isDrawnOnPageOne(double top, bool footer, string cm)
    {
        var content = Content(top, footer);
        var image = new LayoutImage(BmpImageLoader.Load(TwoByTwoBmp())) { Width = 10, Height = content.Height };
        var renderer = Renderer(top, footer);
        renderer.Add(new LayoutImageRenderer(image));

        var pdf = Save(renderer);

        Assert.Equal(1, PageCount(pdf));
        var drawn = Regex.Matches(Streams(pdf), @"(\S+ \S+ \S+ \S+ \S+ \S+) cm");
        Assert.Contains(drawn, m => m.Groups[1].Value == cm);
    }

    [Theory]
    [InlineData(440.16, false, 10, 1)]
    [InlineData(440.04, false, 10, 1)]
    [InlineData(100, false, 10, 1)]
    [InlineData(440.16, true, 11, 1)]
    public void ParagraphWhoseLinesFillTheContentArea_staysOnOnePage(double top, bool footer, int shown, int pages)
    {
        var content = Content(top, footer);
        var style = new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10, Leading = content.Height / 10 };
        var text = string.Join("\n", Enumerable.Range(0, 10).Select(i => "line" + i));
        var renderer = Renderer(top, footer);
        renderer.Add(new ParagraphRenderer(new Paragraph(text, style)));

        var pdf = Save(renderer);

        Assert.Equal(pages, PageCount(pdf));
        // Ten lines, plus the footer's own line when there is one.
        Assert.Equal(shown, Regex.Matches(Streams(pdf), @"\)\s*Tj").Count);
    }

    // ── {pages} agrees with the pages drawn ──

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PagesToken_equalsTheDrawnPages_onTheShortGeometryWithAnExactFillElement(int elements)
    {
        const double top = 440.16;
        var content = Content(top, true);
        var renderer = Renderer(top, true);
        for (var i = 0; i < elements; i++)
            renderer.Add(new Recording(content.Height));

        var pdf = Save(renderer);

        Assert.Equal(elements, PageCount(pdf));
        var shown = Regex.Matches(Streams(pdf), @"\(p (\d+)/(\d+)\)\s*Tj").Select(m => m.Groups[1].Value + "/" + m.Groups[2].Value).ToList();
        Assert.Equal(Enumerable.Range(1, elements).Select(p => p.ToString(CultureInfo.InvariantCulture) + "/" + elements.ToString(CultureInfo.InvariantCulture)), shown);
    }

    // ── What FreshPageArea reports ──

    [Theory]
    [MemberData(nameof(Geometries))]
    public void FreshPageArea_isTheContentArea_onEveryCallOfBothPasses(string kind, double top, bool footer)
    {
        _ = kind;
        var content = Content(top, footer);
        // Mid-page, then a Nothing and its retry, then the page-top call that places it.
        var element = new Recording(content.Height);
        var renderer = Renderer(top, footer);
        renderer.Add(new ParagraphRenderer(new Paragraph("before", Helv)));
        renderer.Add(element);
        Save(renderer);

        Assert.True(element.Calls.Count >= 3);
        foreach (var (_, fresh) in element.Calls)
        {
            var box = Assert.IsType<LayoutBox>(fresh);
            Assert.True(SameBits(content.X, box.X));
            Assert.True(SameBits(content.Y, box.Y));
            Assert.True(SameBits(content.Width, box.Width));
            Assert.True(SameBits(content.Height, box.Height));
        }
    }

    [Fact]
    public void FreshPageArea_isSetInTheCountingPass_whichTheFooterAdds()
    {
        var content = Content(440.16, true);
        var element = new Recording(1);
        var renderer = Renderer(440.16, true);
        renderer.Add(element);
        Save(renderer);

        // One call per pass; both carry it.
        Assert.Equal(2, element.Calls.Count);
        Assert.All(element.Calls, c => Assert.Equal(content, c.Fresh));
    }

    // ── The boundary the change accepts ──

    [Fact]
    public void ARendererThatFitsASmallerAreaButNotALarger_isRefusedOnTheShortGeometry()
    {
        const double top = 440.16;
        var content = Content(top, false);
        var renderer = Renderer(top, false);
        renderer.Add(new FitsSmallerNotLarger(content.Height));

        var ex = Assert.Throws<InvalidOperationException>(() => Save(renderer));

        Assert.StartsWith("An element is too tall to fit on a single page", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARendererThatFitsASmallerAreaButNotALarger_isRefusedOnTheEqualGeometry_asBefore()
    {
        // Equal geometry already offered the full height at a page top, so this refusal is not new.
        const double top = 100;
        var content = Content(top, false);
        var renderer = Renderer(top, false);
        renderer.Add(new FitsSmallerNotLarger(content.Height));

        var ex = Assert.Throws<InvalidOperationException>(() => Save(renderer));

        Assert.StartsWith("An element is too tall to fit on a single page", ex.Message, StringComparison.Ordinal);
    }
}
