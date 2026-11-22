// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #471: <see cref="LayoutImage.FitToPage"/>. Every operand is read back from the decoded content
/// stream. The page is 900 by 400 with no margins unless a test says otherwise, so the content
/// box is 900 by 400 and a square image with no width is 900 pt tall before it is fitted.
/// </summary>
public sealed class ImageFitToPageTests
{
    private const string TooTall =
        "An element is too tall to fit on a single page and cannot be rendered. " +
        "Reduce the element's content or increase the page size.";

    private static readonly TextStyle Helv = new() { Font = VellumPdf.Fonts.Standard14.Helvetica, FontSize = 10 };

    // A 24-bit BMP of the given size. The pixels are never read back, so one byte pattern serves.
    internal static byte[] Bmp(int width, int height)
    {
        var stride = (width * 3 + 3) / 4 * 4;
        var size = 54 + stride * height;
        var b = new byte[size];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(b, 2);
        BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(width).CopyTo(b, 18);
        BitConverter.GetBytes(height).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);
        BitConverter.GetBytes((short)24).CopyTo(b, 28);
        BitConverter.GetBytes(stride * height).CopyTo(b, 34);
        return b;
    }

    internal static LayoutImage Image(int w, int h, double? width = null, double? height = null, bool fit = false,
        EdgeInsets? margins = null, HorizontalAlignment align = HorizontalAlignment.Left, bool keep = false,
        string? anchor = null) =>
        new(BmpImageLoader.Load(Bmp(w, h)))
        {
            Width = width,
            Height = height,
            FitToPage = fit,
            Margins = margins ?? EdgeInsets.Zero,
            Alignment = align,
            KeepWithNext = keep,
            Anchor = anchor,
        };

    internal static Document NewDocument(double w = 900, double h = 400, EdgeInsets? margins = null, bool footer = false,
        bool mirror = false) =>
        new()
        {
            PageSize = new PdfRectangle(0, 0, w, h),
            Margins = margins ?? EdgeInsets.Zero,
            MirrorMargins = mirror,
            Footer = footer ? new RunningBand("p {page}/{pages}", Helv) { Height = 12 } : null,
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };

    internal static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string[] Cms(string stream) =>
        [.. Regex.Matches(stream, @"(?m)^(\S+ \S+ \S+ \S+ \S+ \S+) cm$").Select(m => m.Groups[1].Value)];

    // The decoded content stream of every page, in page-tree order.
    internal static List<string> PageStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pages = new List<string>();
        foreach (Match kid in Regex.Matches(kids, @"(\d+) 0 R"))
        {
            var page = Regex.Match(text, $@"(?m)^{kid.Groups[1].Value} 0 obj\n<<(?<d>.*?)>>\nendobj", RegexOptions.Singleline);
            Assert.True(page.Success);
            var contents = Regex.Match(page.Groups["d"].Value, @"/Contents (\d+) 0 R");
            Assert.True(contents.Success);
            var head = Regex.Match(text, $@"(?m)^{contents.Groups[1].Value} 0 obj\n(?<d><<.*?>>)\nstream\n", RegexOptions.Singleline);
            Assert.True(head.Success);
            var length = int.Parse(Regex.Match(head.Groups["d"].Value, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            using var input = new MemoryStream(pdf, head.Index + head.Length, length);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            pages.Add(Encoding.Latin1.GetString(output.ToArray()));
        }

        return pages;
    }

    // ── The fitted sizes ─────────────────────────────────────────────────────

    public static TheoryData<string, int, int, double?, double?, double, double, double, string> Fitted => new()
    {
        // name, source w, source h, Width, Height, page w, page h, page margin (all sides), cm
        { "square, no width", 2, 2, null, null, 900, 400, 0, "400 0 0 400 0 0" },
        { "square, width 401", 2, 2, 401, null, 900, 400, 0, "400 0 0 400 0 0" },
        { "square, width 900", 2, 2, 900, null, 900, 400, 0, "400 0 0 400 0 0" },
        { "4x2 source", 4, 2, null, null, 900, 400, 0, "800 0 0 400 0 0" },
        { "2x4 source", 2, 4, null, null, 900, 400, 0, "200 0 0 400 0 0" },
        { "2x4 source on A4", 2, 4, null, null, 595.28, 841.89, 72, "348.945 0 0 697.89 72 72" },
        { "explicit height 401", 2, 2, null, 401, 900, 400, 0, "897.75561 0 0 400 0 0" },
        { "explicit width and height, taller than the page", 2, 2, 300, 800, 900, 400, 0, "150 0 0 400 0 0" },
        { "explicit height that fits", 2, 2, null, 300, 900, 400, 0, "900 0 0 300 0 100" },
        { "explicit width that fits", 2, 2, 300, null, 900, 400, 0, "300 0 0 300 0 100" },
        { "explicit width and height that fit", 2, 2, 401, 300, 900, 400, 0, "401 0 0 300 0 100" },
        { "infinite width", 2, 2, double.PositiveInfinity, null, 900, 400, 0, "400 0 0 400 0 0" },
        { "square on A4, already fits", 2, 2, null, null, 595.28, 841.89, 72, "451.28 0 0 451.28 72 318.61" },
    };

    [Theory]
    [MemberData(nameof(Fitted))]
    public void FitToPage_scalesTheSizeTheOtherMembersProduce(
        string name, int sw, int sh, double? width, double? height, double pw, double ph, double margin, string cm)
    {
        _ = name;
        using var doc = NewDocument(pw, ph, new EdgeInsets(margin));
        doc.Add(Image(sw, sh, width, height, fit: true));

        var pdf = Save(doc);

        Assert.Equal(1, PageCount(pdf));
        Assert.Equal([cm], Cms(Assert.Single(PageStreams(pdf))));
    }

    [Fact]
    public void FitToPage_leavesAnImageThatFitsByteIdenticalToTheDefault()
    {
        // Never enlarged, and nothing else changes: the whole file is equal, not only the matrix.
        foreach (var (w, h) in new (double?, double?)[] { (300, null), (400, null), (null, 300), (401, 300), (null, 400) })
        {
            using var plain = NewDocument();
            plain.Add(Image(2, 2, w, h));
            using var fitted = NewDocument();
            fitted.Add(Image(2, 2, w, h, fit: true));

            Assert.Equal(Save(plain), Save(fitted));
        }
    }

    [Fact]
    public void FitToPage_scalesByOneFactor_soTheProportionsTheOtherMembersMadeAreKept()
    {
        // Stretched to 900 by 401, then both extents multiplied by 400 / 401.
        using var doc = NewDocument();
        doc.Add(Image(2, 2, null, 401, fit: true));

        var cm = Cms(Assert.Single(PageStreams(Save(doc))))[0].Split(' ');

        Assert.Equal(900.0 * 400 / 401, double.Parse(cm[0], CultureInfo.InvariantCulture), 5e-6);
        Assert.Equal("400", cm[3]);
    }

    // ── Margins, alignment and the page ──────────────────────────────────────

    [Fact]
    public void ImageMargins_leaveTheFittedHeightTheContentHeightLessTheirVerticalSum()
    {
        using var doc = NewDocument();
        doc.Add(Image(2, 2, fit: true, margins: new EdgeInsets(10, 0, 10, 0)));

        Assert.Equal(["380 0 0 380 0 10"], Cms(Assert.Single(PageStreams(Save(doc)))));
    }

    [Fact]
    public void ImageMargins_onAllSides_fitTheDeflatedHeightAndTheWidthFollows()
    {
        // The area is 860 wide, so the unfitted height is 860, and 380 is what 400 leaves.
        using var doc = NewDocument();
        doc.Add(Image(2, 2, fit: true, margins: new EdgeInsets(10, 20, 10, 20)));

        Assert.Equal(["380 0 0 380 20 10"], Cms(Assert.Single(PageStreams(Save(doc)))));
    }

    [Fact]
    public void FittedImage_isAlignedInsideItsMargins()
    {
        using var doc = NewDocument();
        doc.Add(Image(2, 2, fit: true, margins: new EdgeInsets(10, 20, 10, 20), align: HorizontalAlignment.Center));

        // 20 + (860 - 380) / 2
        Assert.Equal(["380 0 0 380 260 10"], Cms(Assert.Single(PageStreams(Save(doc)))));
    }

    [Fact]
    public void RunningFooter_shortensTheFittedHeight()
    {
        using var doc = NewDocument(footer: true);
        doc.Add(Image(2, 2, fit: true));

        var pdf = Save(doc);

        Assert.Equal(1, PageCount(pdf));
        Assert.Equal(["388 0 0 388 0 12"], Cms(Assert.Single(PageStreams(pdf))));
    }

    // ── Where the image goes ─────────────────────────────────────────────────

    [Fact]
    public void AnImageTooTallForTheSpaceLeft_movesToTheNextPageAtThePageSize()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("Hello", Helv));
        doc.Add(Image(2, 2, fit: true));

        var pages = PageStreams(Save(doc));

        Assert.Equal(2, pages.Count);
        Assert.Empty(Cms(pages[0]));
        Assert.Equal(["400 0 0 400 0 0"], Cms(pages[1]));
    }

    [Fact]
    public void FittedSize_isTheSameInBothPasses_whenAFooterCountsThePages()
    {
        // {pages} makes the document lay out twice. A pass that fitted to the area left after the
        // paragraph would size the image differently from the other and break the agreement.
        using var doc = NewDocument(footer: true);
        doc.Add(new Paragraph("Hello", Helv));
        doc.Add(Image(2, 2, fit: true));

        var pages = PageStreams(Save(doc));

        Assert.Equal(2, pages.Count);
        Assert.Equal(["388 0 0 388 0 12"], Cms(pages[1]));
        Assert.Contains("2/2", pages[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(440.16, "329.73 0 0 329.73 72 72")]
    [InlineData(440.04, "329.85 0 0 329.85 72 72")]
    public void OnAShortContentArea_theFittedImageSavesOnOnePage(double top, string cm)
    {
        // On A4 with a 72 pt margin on three sides the height from the top of the content area to
        // its bottom is a rounding step off the content height at these top margins.
        using var doc = NewDocument(595.28, 841.89, new EdgeInsets(top, 72, 72, 72));
        doc.Add(Image(2, 2, fit: true));

        var pdf = Save(doc);

        Assert.Equal(1, PageCount(pdf));
        Assert.Equal([cm], Cms(Assert.Single(PageStreams(pdf))));
    }

    [Fact]
    public void FitToPage_underMirroredMargins_givesTheSameSizeOnBothSidesOfTheSpread()
    {
        // 300 wide, inside 80 and outside 40: the content box is 180 by 260 on every page and its
        // left edge is 80 on a right-hand page and 40 on a left-hand one. A 1 by 2 source is
        // 360 tall at the full width, so it is fitted to 260 and its width is 180 * 260 / 360 = 130.
        using var doc = NewDocument(300, 300, new EdgeInsets(20, 40, 20, 80), mirror: true);
        doc.Add(Image(1, 2, fit: true));
        doc.Add(new PageBreak());
        doc.Add(Image(1, 2, fit: true));

        var pages = PageStreams(Save(doc));

        Assert.Equal(2, pages.Count);
        Assert.Equal(["130 0 0 260 80 20"], Cms(pages[0]));
        Assert.Equal(["130 0 0 260 40 20"], Cms(pages[1]));
    }

    [Fact]
    public void AnAnchor_isAtTheTopEdgeOfTheFittedImage_insideItsMargins()
    {
        using var doc = NewDocument();
        doc.Add(Image(2, 2, fit: true, margins: new EdgeInsets(10, 0, 10, 0), anchor: "fig"));

        var text = Encoding.Latin1.GetString(Save(doc));

        // The image is drawn from y = 10 to y = 390, so its top edge is at 390.
        Assert.Matches(@"/XYZ 0 390 null\]", text);
    }

    [Fact]
    public void KeepWithNext_onAnImageAsTallAsThePage_isRelaxedAndReported_andSavesTwice_identically()
    {
        // The paragraph cannot share a page with an image that fills one, so the request is
        // dropped and reported, as for any group the pages cannot hold. Fitting reads the box a
        // fresh page offers and nothing the keep trial lays out ahead, so two saves agree.
        Document Build()
        {
            var doc = NewDocument();
            doc.Add(Image(2, 2, fit: true, keep: true));
            doc.Add(new Paragraph("Caption", Helv));
            return doc;
        }

        using var first = Build();
        using var second = Build();
        var pdf = Save(first);

        Assert.Equal(pdf, Save(second));
        var pages = PageStreams(pdf);
        Assert.Equal(2, pages.Count);
        Assert.Equal(["400 0 0 400 0 0"], Cms(pages[0]));
        Assert.Contains("(Caption)", pages[1], StringComparison.Ordinal);
        var diagnostic = Assert.Single(first.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.KeepWithNextRelaxed, diagnostic.Code);
        Assert.Equal(LayoutElementKind.Image, diagnostic.ElementKind);
    }

    [Fact]
    public void KeepWithNext_onAnImageThatSharesAPageWithItsCaption_isKept()
    {
        // A fitted height under half the page leaves room for the caption on the same page.
        using var doc = NewDocument(900, 400, new EdgeInsets(0));
        doc.Add(Image(2, 2, height: 100, fit: true, keep: true));
        doc.Add(new Paragraph("Caption", Helv));

        var pages = PageStreams(Save(doc));

        Assert.Single(pages);
        Assert.Empty(doc.Diagnostics);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void FitToPage_defaultsToFalse_andTheInitAccessorStoresTheValueWithoutRefusingIt()
    {
        var image = Image(2, 2, fit: true);

        Assert.True(image.FitToPage);
        Assert.False(Image(2, 2).FitToPage);
    }

    [Fact]
    public void AFittedExtentUnderTheSmallestWrittenOne_isRefusedFromTheSave()
    {
        // The 1 by 2 source is 1800 pt tall at the 900 pt width, and the content box leaves 8e-6
        // pt of height, so the fitted width is 4e-6 pt: written as 0, a matrix with no inverse.
        using var doc = NewDocument();
        doc.Add(Image(2, 4, fit: true, margins: new EdgeInsets(399.999992, 0, 0, 0)));

        var e = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("width of 0.000004", e.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be painted", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAPositiveHeightToFitTo_theImageIsNotScaled_andTheSaveRefusesItAsTooTall()
    {
        using var doc = NewDocument(margins: new EdgeInsets(0));
        doc.Add(Image(2, 2, fit: true, margins: new EdgeInsets(400, 0, 0, 0)));

        var e = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(
            TooTall + " The height derived from the width and the image's proportions, 900pt, " +
            "exceeds the 0pt content height.",
            e.Message);
    }

    [Fact]
    public void AValueRefusedBeforeTheScale_isRefusedWithTheMessageItHasWithoutTheMember()
    {
        using var plain = NewDocument();
        plain.Add(Image(2, 2, 0, null));
        using var fitted = NewDocument();
        fitted.Add(Image(2, 2, 0, null, fit: true));

        var expected = Assert.Throws<InvalidOperationException>(() => Save(plain));
        var actual = Assert.Throws<InvalidOperationException>(() => Save(fitted));

        Assert.Equal(expected.Message, actual.Message);
    }

    // ── The too-tall message, with the member unset ──────────────────────────

    [Theory]
    [InlineData(null, null, 2, 2, "The height derived from the width and the image's proportions, 900pt, exceeds the 400pt content height; set FitToPage to scale it down.")]
    [InlineData(401.0, null, 2, 2, "The height derived from the width and the image's proportions, 401pt, exceeds the 400pt content height; set FitToPage to scale it down.")]
    [InlineData(null, 401.0, 2, 2, "LayoutImage.Height 401pt exceeds the 400pt content height.")]
    [InlineData(null, null, 4, 2, "The height derived from the width and the image's proportions, 450pt, exceeds the 400pt content height; set FitToPage to scale it down.")]
    [InlineData(null, null, 2, 4, "The height derived from the width and the image's proportions, 1800pt, exceeds the 400pt content height; set FitToPage to scale it down.")]
    public void ATooTallImage_namesTheAxisAndBothNumbers(double? width, double? height, int sw, int sh, string reason)
    {
        using var doc = NewDocument();
        doc.Add(Image(sw, sh, width, height));

        var e = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(TooTall + " " + reason, e.Message);
    }

    [Fact]
    public void ATooTallImage_underARunningFooter_startsWithTheBandMessage()
    {
        using var doc = NewDocument(footer: true);
        doc.Add(Image(2, 2));

        var e = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.StartsWith(
            "An element is too tall to fit on a single page and cannot be rendered. " +
            "On a 400.0pt page, 0.0pt of margins and 12.0pt of running bands (header 0.0pt, footer 12.0pt) " +
            "leave a content area 388.0pt tall. ",
            e.Message,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "The height derived from the width and the image's proportions, 900pt, exceeds the " +
            "388pt content height; set FitToPage to scale it down.",
            e.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ATooTallImageAfterAParagraph_namesTheContentHeightOfAFreshPage()
    {
        // The retry is offered the whole content area, so the numbers are the page's, not what is
        // left below the paragraph.
        using var doc = NewDocument();
        doc.Add(new Paragraph("Hello", Helv));
        doc.Add(Image(2, 2, null, 500));

        var e = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(TooTall + " LayoutImage.Height 500pt exceeds the 400pt content height.", e.Message);
    }

    // ── A context the caller built ───────────────────────────────────────────

    [Fact]
    public void UnderACallerBuiltContext_theImageIsFittedToTheAreaOffered()
    {
        var renderer = new LayoutImageRenderer(Image(2, 2, fit: true));

        var result = renderer.Layout(new LayoutContext(new LayoutBox(10, 20, 900, 250)));

        Assert.Equal(LayoutResult.Outcome.Full, result.Status);
        Assert.Equal(new LayoutBox(10, 20, 900, 250), result.OccupiedArea);
    }

    [Fact]
    public void UnderACallerBuiltContext_withoutFitToPage_anImageTooTallForTheAreaAnswersNothing()
    {
        var renderer = new LayoutImageRenderer(Image(2, 2));

        var result = renderer.Layout(new LayoutContext(new LayoutBox(10, 20, 900, 250)));

        Assert.Equal(LayoutResult.Outcome.Nothing, result.Status);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void WhenTheContextCarriesAFreshPage_theImageIsFittedToThatAndNotToTheAreaOffered()
    {
        var renderer = new LayoutImageRenderer(Image(2, 2, fit: true));
        var offered = new LayoutBox(0, 100, 900, 300);

        // The fresh page is 400 tall. The fitted image is 400 and the area left is 300, so it is
        // refused here and placed on the next page, where it gets the 400.
        var result = renderer.Layout(new LayoutContext(offered) { FreshPageArea = new LayoutBox(0, 0, 900, 400) });

        Assert.Equal(LayoutResult.Outcome.Nothing, result.Status);
        var retry = renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 900, 400)) { FreshPageArea = new LayoutBox(0, 0, 900, 400) });
        Assert.Equal(LayoutResult.Outcome.Full, retry.Status);
        Assert.Equal(400, retry.OccupiedArea!.Value.Height);
    }

    [Fact]
    public void TheReason_isBuiltOnlyForACallAtAPageTop()
    {
        var renderer = new LayoutImageRenderer(Image(2, 2));
        var area = new LayoutBox(0, 0, 900, 400);

        var inside = renderer.Layout(new LayoutContext(area) { AtPageTop = false });
        var atTop = renderer.Layout(new LayoutContext(area) { AtPageTop = true });

        Assert.Null(inside.Reason);
        Assert.Equal(
            "The height derived from the width and the image's proportions, 900pt, exceeds the " +
            "400pt content height; set FitToPage to scale it down.",
            atTop.Reason);
    }
}
