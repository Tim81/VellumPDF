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
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.ContentStreamReadback;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <c>SpaceAfter</c>: a gap set once on an element. The page is 300 x 200 pt with 20 pt margins, so
/// the content area is 160 pt tall and holds 11 lines of the 12 pt body style, whose leading is
/// 14.4 pt. Every position is read from the decoded content stream of each page and compared with
/// a number worked out from that geometry, not with what the layout code computes.
/// </summary>
public sealed class SpaceAfterTests
{
    private const double Gap = 20;

    public enum Kind
    {
        Paragraph,
        Heading,
        List,
        Preformatted,
        Table,
        Image,
        Separator,
        Pie,
    }

    private static Document NewDoc() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 200),
        Margins = new EdgeInsets(20),
        Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
        DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
    };

    // A paragraph of `count` lines, each one word: tag1, tag2, ...
    private static Paragraph Lines(
        string tag, int count, double space = 0, bool keepWithNext = false, bool keepTogether = false) =>
        new(string.Join('\n', Enumerable.Range(1, count).Select(i => tag + i.ToString(CultureInfo.InvariantCulture))), Body)
        {
            SpaceAfter = space,
            KeepWithNext = keepWithNext,
            KeepTogether = keepTogether,
        };

    private static object ElementOf(Kind kind, double space) => kind switch
    {
        Kind.Paragraph => new Paragraph("First", Body) { SpaceAfter = space },
        Kind.Heading => new Heading("First", Body) { SpaceAfter = space },
        Kind.List => new ListElement(ListStyle.Unordered, [new ListItem("First", Body)]) { SpaceAfter = space },
        Kind.Preformatted => new Preformatted("First") { SpaceAfter = space },
        Kind.Table => TableOf(space),
        Kind.Image => new LayoutImage(PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng())) { Width = 20, Height = 20, SpaceAfter = space },
        Kind.Separator => new LineSeparator { SpaceAfter = space },
        Kind.Pie => new PieChart { Slices = [new PieSlice(1, ColorRgb.Black)], Diameter = 20, SpaceAfter = space },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static TableElement TableOf(double space)
    {
        var table = new TableElement { SpaceAfter = space };
        table.AddRow().AddCell("First");
        return table;
    }

    private static void Add(Document doc, object element)
    {
        switch (element)
        {
            case Paragraph e: doc.Add(e); break;
            case Heading e: doc.Add(e); break;
            case ListElement e: doc.Add(e); break;
            case Preformatted e: doc.Add(e); break;
            case TableElement e: doc.Add(e); break;
            case LayoutImage e: doc.Add(e); break;
            case LineSeparator e: doc.Add(e); break;
            case PieChart e: doc.Add(e); break;
            default: throw new ArgumentException("unknown element", nameof(element));
        }
    }

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // The decoded content stream of every page, in page-tree order.
    private static List<string> PageStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pages = new List<string>();
        foreach (Match kid in Regex.Matches(kids, @"(\d+) 0 R"))
        {
            var number = int.Parse(kid.Groups[1].Value, CultureInfo.InvariantCulture);
            var page = Regex.Match(text, $@"(?m)^{number} 0 obj\n<<(?<d>.*?)>>\nendobj", RegexOptions.Singleline);
            Assert.True(page.Success, $"page object {number}");
            var contents = Regex.Match(page.Groups["d"].Value, @"/Contents (\d+) 0 R");
            Assert.True(contents.Success, $"page object {number} has no /Contents reference");
            pages.Add(StreamOf(pdf, text, int.Parse(contents.Groups[1].Value, CultureInfo.InvariantCulture)));
        }

        return pages;
    }

    private static string StreamOf(byte[] pdf, string text, int objectNumber)
    {
        var head = Regex.Match(text, $@"(?m)^{objectNumber} 0 obj\n(?<d><<.*?>>)\nstream\n", RegexOptions.Singleline);
        Assert.True(head.Success, $"stream object {objectNumber}");
        var length = int.Parse(Regex.Match(head.Groups["d"].Value, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        using var input = new MemoryStream(pdf, head.Index + head.Length, length);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.Latin1.GetString(output.ToArray());
    }

    // The baseline y of a word, and the page it is on (1-based).
    private static (int Page, double Y) Find(List<string> pages, string word)
    {
        for (var i = 0; i < pages.Count; i++)
            foreach (var p in TextPlacements(pages[i]))
                if (p.Text == word) return (i + 1, p.Y);
        throw new InvalidOperationException($"'{word}' is on no page");
    }

    private static (int Page, double Y) FindIn(Document doc, string word) => Find(PageStreams(Save(doc)), word);

    // The y of the first line on a page: the top of the content area, as the layout puts it.
    private static double TopY(List<string> pages, string word) => Find(pages, word).Y;

    // ── The gap ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Kind.Paragraph)]
    [InlineData(Kind.Heading)]
    [InlineData(Kind.List)]
    [InlineData(Kind.Preformatted)]
    [InlineData(Kind.Table)]
    [InlineData(Kind.Image)]
    [InlineData(Kind.Separator)]
    [InlineData(Kind.Pie)]
    public void TheNextElement_startsExactlyTheGapLower(Kind kind)
    {
        (int Page, double Y) Next(double space)
        {
            using var doc = NewDoc();
            Add(doc, ElementOf(kind, space));
            doc.Add(new Paragraph("Next", Body));
            return FindIn(doc, "Next");
        }

        var without = Next(0);
        var with = Next(Gap);

        Assert.Equal(1, without.Page);
        Assert.Equal(1, with.Page);
        Assert.Equal(Gap, without.Y - with.Y, 4);
    }

    [Fact]
    public void TheGapIsAddedAfterEachElement_notOnce()
    {
        using var doc = NewDoc();
        doc.Add(new Paragraph("One", Body) { SpaceAfter = 10 });
        doc.Add(new Paragraph("Two", Body) { SpaceAfter = 10 });
        doc.Add(new Paragraph("Three", Body));
        var pages = PageStreams(Save(doc));

        // One 12 pt line is 14.4 pt of leading, and each gap is 10.
        Assert.Equal(14.4 + 10, Find(pages, "One").Y - Find(pages, "Two").Y, 4);
        Assert.Equal(14.4 + 10, Find(pages, "Two").Y - Find(pages, "Three").Y, 4);
    }

    [Fact]
    public void AZeroGap_isTheSameDocumentAsNoGap()
    {
        byte[] Build(bool set)
        {
            using var doc = NewDoc();
            doc.Add(set ? new Paragraph("a", Body) { SpaceAfter = 0 } : new Paragraph("a", Body));
            var table = set ? new TableElement { SpaceAfter = -0.0 } : new TableElement();
            table.AddRow().AddCell("cell");
            doc.Add(table);
            doc.Add(new Paragraph("b", Body));
            return Save(doc);
        }

        Assert.Equal(NormalisedSha256(Build(false)), NormalisedSha256(Build(true)));
    }

    [Fact]
    public void AMarginsBottom_stillAddsNoSpace()
    {
        double Next(double bottom)
        {
            using var doc = NewDoc();
            doc.Add(new Paragraph("First", Body) { Margins = new EdgeInsets(0, 0, bottom, 0) });
            doc.Add(new Paragraph("Next", Body));
            return FindIn(doc, "Next").Y;
        }

        Assert.Equal(Next(0), Next(60), 4);
    }

    [Fact]
    public void AnImagesGap_isAddedToItsBottomMargin()
    {
        double Next(double space)
        {
            using var doc = NewDoc();
            doc.Add(new LayoutImage(PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng()))
            {
                Width = 20,
                Height = 20,
                Margins = new EdgeInsets(0, 0, 10, 0),
                SpaceAfter = space,
            });
            doc.Add(new Paragraph("Next", Body));
            return FindIn(doc, "Next").Y;
        }

        Assert.Equal(Gap, Next(0) - Next(Gap), 4);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    public static TheoryData<Kind, double> Refused()
    {
        var data = new TheoryData<Kind, double>();
        foreach (var kind in Enum.GetValues<Kind>())
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, -double.Epsilon, double.MinValue })
                data.Add(kind, value);
        return data;
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void ANonFiniteOrNegativeGap_isRefusedAtTheSetter(Kind kind, double value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ElementOf(kind, value));
        Assert.Equal("value", ex.ParamName);
    }

    [Theory]
    [InlineData(Kind.Paragraph)]
    [InlineData(Kind.Heading)]
    [InlineData(Kind.List)]
    [InlineData(Kind.Preformatted)]
    [InlineData(Kind.Table)]
    [InlineData(Kind.Image)]
    [InlineData(Kind.Separator)]
    [InlineData(Kind.Pie)]
    public void AFiniteNonNegativeGap_isAccepted_andReadBack(Kind kind)
    {
        foreach (var value in new[] { 0, -0.0, double.Epsilon, 12.5, double.MaxValue })
            Assert.NotNull(ElementOf(kind, value));

        Assert.Equal(12.5, ((Paragraph)ElementOf(Kind.Paragraph, 12.5)).SpaceAfter);
    }

    [Fact]
    public void AGapNoPageCanHold_putsTheNextElementOnTheNextPage_andSaves()
    {
        using var doc = NewDoc();
        doc.Add(Lines("a", 2, double.MaxValue));
        doc.Add(Lines("b", 2));
        var pages = PageStreams(Save(doc));

        Assert.Equal(2, pages.Count);
        Assert.Equal(1, Find(pages, "a1").Page);
        Assert.Equal(2, Find(pages, "b1").Page);
        Assert.Equal(Find(pages, "a1").Y, Find(pages, "b1").Y, 4);
    }

    // ── Page breaks ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(20)] // fits in what is left (30.4), the next line does not
    [InlineData(40)] // does not fit in what is left
    public void AGapNearThePageFoot_isDropped_andTheNextElementStartsAtThePageTop(double space)
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 9, space));
        doc.Add(new Paragraph("Next", Body));
        var pages = PageStreams(Save(doc));

        Assert.Equal(2, pages.Count);
        var next = Find(pages, "Next");
        Assert.Equal(2, next.Page);
        Assert.Equal(TopY(pages, "f1"), next.Y, 4);
    }

    // A 150 pt rule ends 150 pt down a 160 pt area, so exactly 10 pt are left (170 and 180 are
    // exact in a double). An element with no height is the only one that can tell a gap that
    // takes all of that from one that takes more: anything taller starts the next page either way.
    private static byte[] RuleThenNothing(double gap)
    {
        using var doc = NewDoc();
        doc.Add(new LineSeparator { LineWidth = 150, Margins = EdgeInsets.Zero, SpaceAfter = gap });
        doc.Add(new LineSeparator { LineWidth = 0, Margins = EdgeInsets.Zero });
        return Save(doc);
    }

    [Fact]
    public void AGapEqualToWhatIsLeft_fits_andAGapOneStepLarger_doesNot()
    {
        Assert.Single(PageStreams(RuleThenNothing(10)));
        Assert.Single(PageStreams(RuleThenNothing(double.BitDecrement(10))));
        Assert.Equal(2, PageStreams(RuleThenNothing(double.BitIncrement(10))).Count);
    }

    [Fact]
    public void AfterASplitElementWhoseLastPieceEndsAPage_theGapIsDropped()
    {
        // 22 lines fill two pages with 1.6 pt to spare, so the gap does not fit after the last
        // piece, and the next element starts the third page with nothing carried over.
        using var doc = NewDoc();
        doc.Add(Lines("s", 22, Gap));
        doc.Add(new Paragraph("Next", Body));
        var pages = PageStreams(Save(doc));

        Assert.Equal(3, pages.Count);
        Assert.Equal(3, Find(pages, "Next").Page);
        Assert.Equal(TopY(pages, "s1"), Find(pages, "Next").Y, 4);
    }

    [Fact]
    public void AfterASplitElementWhoseLastPieceEndsMidPage_theGapIsKept()
    {
        double Next(double space)
        {
            using var doc = NewDoc();
            doc.Add(Lines("s", 15, space));
            doc.Add(new Paragraph("Next", Body));
            var pages = PageStreams(Save(doc));
            Assert.Equal(2, Find(pages, "Next").Page);
            return Find(pages, "Next").Y;
        }

        Assert.Equal(Gap, Next(0) - Next(Gap), 4);
    }

    [Theory]
    [InlineData(Kind.Paragraph)]
    [InlineData(Kind.Table)]
    [InlineData(Kind.List)]
    public void AfterTheLastElement_theGapAddsNothing(Kind kind)
    {
        byte[] Build(double space)
        {
            using var doc = NewDoc();
            doc.Add(new Paragraph("Before", Body));
            Add(doc, ElementOf(kind, space));
            return Save(doc);
        }

        Assert.Equal(NormalisedSha256(Build(0)), NormalisedSha256(Build(Gap)));
        Assert.Equal(NormalisedSha256(Build(0)), NormalisedSha256(Build(1e6)));
    }

    [Fact]
    public void AfterALastElementThatEndsThePage_noPageIsAdded()
    {
        byte[] Build(double space)
        {
            using var doc = NewDoc();
            doc.Add(Lines("s", 22, space));
            return Save(doc);
        }

        Assert.Equal(2, PageCount(Build(Gap)));
        Assert.Equal(NormalisedSha256(Build(0)), NormalisedSha256(Build(Gap)));
    }

    [Theory]
    [InlineData(PageBreakMode.Page)]
    [InlineData(PageBreakMode.Recto)]
    public void ABreakBeforeTheNextElement_dropsTheGap(PageBreakMode mode)
    {
        byte[] Build(double space)
        {
            using var doc = NewDoc();
            doc.Add(new Paragraph("First", Body) { SpaceAfter = space });
            doc.Add(new Paragraph("Next", Body) { BreakBefore = mode });
            return Save(doc);
        }

        var pages = PageStreams(Build(Gap));
        Assert.Equal(TopY(pages, "First"), Find(pages, "Next").Y, 4);
        Assert.Equal(NormalisedSha256(Build(0)), NormalisedSha256(Build(Gap)));
    }

    [Fact]
    public void APageBreakElementAfterTheElement_dropsTheGap()
    {
        byte[] Build(double space)
        {
            using var doc = NewDoc();
            doc.Add(new Paragraph("First", Body) { SpaceAfter = space });
            doc.Add(new PageBreak());
            doc.Add(new Paragraph("Next", Body));
            return Save(doc);
        }

        var pages = PageStreams(Build(Gap));
        Assert.Equal(2, pages.Count);
        Assert.Equal(TopY(pages, "First"), Find(pages, "Next").Y, 4);
        Assert.Equal(NormalisedSha256(Build(0)), NormalisedSha256(Build(Gap)));
    }

    [Fact]
    public void WithABand_theMeasuringAndDrawingPassesAgreeOnThePageTheGapMakes()
    {
        List<string> Build(double space)
        {
            using var doc = NewDoc();
            doc.Footer = new RunningBand("{page}/{pages}");
            // The footer takes some of the page. Eight lines and one more fit above it, but not
            // with the gap between them.
            doc.Add(Lines("f", 8, space));
            doc.Add(new Paragraph("Next", Body));
            return PageStreams(Save(doc));
        }

        var one = Build(0);
        var two = Build(Gap);

        Assert.Single(one);
        Assert.Contains(TextPlacements(one[0]), p => p.Text == "1/1");
        Assert.Equal(2, two.Count);
        Assert.Contains(TextPlacements(two[0]), p => p.Text == "1/2");
        Assert.Contains(TextPlacements(two[1]), p => p.Text == "2/2");
    }

    // ── Keep requests ────────────────────────────────────────────────────────

    private static void AssertDiagnostics(Document doc, params (LayoutDiagnosticCode Code, LayoutElementKind Kind, int Index, int Page)[] expected) =>
        Assert.Equal(
            expected,
            doc.Diagnostics.Select(d => (d.Code, d.ElementKind, d.ElementIndex ?? -1, d.PageNumber ?? -1)).ToArray());

    [Fact]
    public void KeepWithNext_countsTheGapBetweenTheKeptElements_andMovesTheRun()
    {
        // Eight lines, the heading and one more line end 144 pt down and fit. With the gap the
        // line would start below 160, so the heading, which asked to stay with it, moves.
        (List<string> Pages, int Diagnostics) Build(double space, bool keep)
        {
            using var doc = NewDoc();
            doc.Add(Lines("f", 8));
            doc.Add(new Heading("HeadK", Body) { KeepWithNext = keep, SpaceAfter = space });
            doc.Add(new Paragraph("BodyK", Body));
            var pages = PageStreams(Save(doc));
            return (pages, doc.Diagnostics.Count);
        }

        var (free, freeDiagnostics) = Build(0, keep: true);
        Assert.Equal(1, Find(free, "BodyK").Page);
        Assert.Equal(0, freeDiagnostics);

        // The gap with no request strands the heading, as the same document did before the gap.
        var (stranded, _) = Build(Gap, keep: false);
        Assert.Equal(1, Find(stranded, "HeadK").Page);
        Assert.Equal(2, Find(stranded, "BodyK").Page);

        var (kept, keptDiagnostics) = Build(Gap, keep: true);
        Assert.Equal(2, Find(kept, "HeadK").Page);
        Assert.Equal(2, Find(kept, "BodyK").Page);
        Assert.Equal(TopY(kept, "f1"), Find(kept, "HeadK").Y, 4);
        Assert.Equal(0, keptDiagnostics);

        // On the new page the gap lies between them, on top of the heading's own line.
        var headToBody = Find(kept, "HeadK").Y - Find(kept, "BodyK").Y;
        var headToBodyNoGap = Find(free, "HeadK").Y - Find(free, "BodyK").Y;
        Assert.Equal(Gap, headToBody - headToBodyNoGap, 4);
    }

    [Fact]
    public void KeepWithNext_isRelaxedAndReported_onlyWhenTheGapIsWhatMakesTheGroupNotHold()
    {
        // At the top of a page, a heading and a ten-line block that must stay whole: 158.4 pt of
        // lines hold in 160 with nothing between, and not with a gap of 5.
        void Run(double space, params (LayoutDiagnosticCode, LayoutElementKind, int, int)[] expected)
        {
            using var doc = NewDoc();
            doc.Add(new Heading("HeadR", Body) { KeepWithNext = true, SpaceAfter = space });
            doc.Add(Lines("k", 10, keepTogether: true));
            _ = Save(doc);
            AssertDiagnostics(doc, expected);
        }

        Run(0);
        Run(5, (LayoutDiagnosticCode.KeepWithNextRelaxed, LayoutElementKind.Heading, 0, 1));
    }

    [Fact]
    public void KeepWithNext_reportsTheSameWithAndWithoutAGapOfZero_whereTheGapIsNotInvolved()
    {
        void Run(bool setZero, params (LayoutDiagnosticCode, LayoutElementKind, int, int)[] expected)
        {
            using var doc = NewDoc();
            doc.Add(setZero
                ? new Heading("HeadR", Body) { KeepWithNext = true, SpaceAfter = 0 }
                : new Heading("HeadR", Body) { KeepWithNext = true });
            doc.Add(Lines("k", 11, keepTogether: true));
            _ = Save(doc);
            AssertDiagnostics(doc, expected);
        }

        // The heading and eleven lines cannot hold on one page whatever lies between them.
        var relaxed = (LayoutDiagnosticCode.KeepWithNextRelaxed, LayoutElementKind.Heading, 0, 1);
        Run(false, relaxed);
        Run(true, relaxed);
    }

    [Fact]
    public void KeepTogether_isMeasuredWithoutItsOwnGap()
    {
        // Five lines, then five more that stay whole: 144 pt, which fits. The gap after the second
        // block is not part of it, so the block does not move; the next element starts the page.
        using var doc = NewDoc();
        doc.Add(Lines("f", 5));
        doc.Add(Lines("k", 5, 20, keepTogether: true));
        doc.Add(new Paragraph("Next", Body));
        var pages = PageStreams(Save(doc));

        Assert.Equal(1, Find(pages, "k5").Page);
        Assert.Equal(2, Find(pages, "Next").Page);
        Assert.Equal(TopY(pages, "f1"), Find(pages, "Next").Y, 4);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void KeepTogether_startsBelowTheGapBeforeIt()
    {
        List<string> Build(double space)
        {
            using var doc = NewDoc();
            doc.Add(Lines("f", 5, space));
            doc.Add(Lines("k", 5, keepTogether: true));
            return PageStreams(Save(doc));
        }

        var free = Build(0);
        Assert.Equal(1, Find(free, "k5").Page);

        // 72 + 20 + 72 does not fit in 160, so the block moves whole to the next page.
        var gap = Build(Gap);
        Assert.Equal(2, Find(gap, "k1").Page);
        Assert.Equal(2, Find(gap, "k5").Page);
        Assert.Equal(TopY(gap, "f1"), Find(gap, "k1").Y, 4);
    }

    [Fact]
    public void WithABand_aGapOnTheLastElement_addsNoSpaceAndNoPage()
    {
        byte[] Build(double space)
        {
            using var doc = NewDoc();
            doc.Footer = new RunningBand("{page}/{pages}");
            doc.Add(Lines("p", 1));
            doc.Add(Lines("q", 1, space));
            return Save(doc);
        }

        var plain = Build(0);
        foreach (var space in new[] { 30, 1000, 1e6 })
        {
            var gapped = Build(space);
            Assert.Equal(NormalisedSha256(plain), NormalisedSha256(gapped));
            var pages = PageStreams(gapped);
            Assert.Single(pages);
            Assert.Contains(TextPlacements(pages[0]), t => t.Text == "1/1");
        }
    }

    [Fact]
    public void AGapBeforeAKeepRunThatMoves_isDropped_andTheRunStartsThePage()
    {
        // The first line and its gap end 134.4 pt down, the kept line ends at 148.8, and the three
        // lines that must stay whole do not fit below it, so the run moves. The gap that was
        // spent in testing the run from this page must not be spent again on the next one.
        using var doc = NewDoc();
        doc.Add(Lines("a", 1, 120));
        doc.Add(Lines("k", 1, keepWithNext: true));
        doc.Add(Lines("c", 3, keepTogether: true));
        var pages = PageStreams(Save(doc));

        Assert.Equal(2, Find(pages, "k1").Page);
        Assert.Equal(TopY(pages, "a1"), Find(pages, "k1").Y, 4);
        Assert.Equal(2, Find(pages, "c1").Page);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void ATestOfAKeepRun_leavesNoGapBehindForTheElementThatStartsIt()
    {
        // The run is k and c. Testing it places c, whose own gap is 30; the gap before k is 0,
        // so k must sit directly below a, and c directly below k.
        using var doc = NewDoc();
        doc.Add(Lines("a", 1));
        doc.Add(Lines("k", 1, keepWithNext: true));
        doc.Add(Lines("c", 1, 30));
        doc.Add(Lines("d", 1));
        var pages = PageStreams(Save(doc));

        Assert.Equal(14.4, Find(pages, "a1").Y - Find(pages, "k1").Y, 4);
        Assert.Equal(14.4, Find(pages, "k1").Y - Find(pages, "c1").Y, 4);
        Assert.Equal(14.4 + 30, Find(pages, "c1").Y - Find(pages, "d1").Y, 4);
    }

    // ── Where it is read ─────────────────────────────────────────────────────

    [Fact]
    public void ARendererAddedToTheDocument_readsTheGapOfItsElement()
    {
        double Next(double space)
        {
            using var doc = NewDoc();
            doc.Add(new ParagraphRenderer(new Paragraph("First", Body) { SpaceAfter = space }));
            doc.Add(new Paragraph("Next", Body));
            return FindIn(doc, "Next").Y;
        }

        Assert.Equal(Gap, Next(0) - Next(Gap), 4);
    }

    private sealed class Wrapper(IRenderer inner) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => inner.Layout(context);

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    [Fact]
    public void AnElementACallersRendererHolds_hasNoGap()
    {
        double Next(double space)
        {
            using var doc = NewDoc();
            doc.Add(new Wrapper(new ParagraphRenderer(new Paragraph("First", Body) { SpaceAfter = space })));
            doc.Add(new Paragraph("Next", Body));
            return FindIn(doc, "Next").Y;
        }

        Assert.Equal(Next(0), Next(Gap), 4);
    }

    [Fact]
    public void TheContinuationBound_doesNotReadTheGap()
    {
        var plain = new DocumentRenderer(NewPdf(), Page, Margins);
        var withGap = new ParagraphRenderer(new Paragraph("some words to bound", Body) { SpaceAfter = Gap });
        var without = new ParagraphRenderer(new Paragraph("some words to bound", Body));

        Assert.Equal(plain.ContinuationBoundOf(without), plain.ContinuationBoundOf(withGap));
    }
}
