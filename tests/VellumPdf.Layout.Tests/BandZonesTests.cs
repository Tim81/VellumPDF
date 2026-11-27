// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Layout.Tests.NoteBlockSupport;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #365: a running band of three zones. Pages are 300 x 300 pt with 20 pt margins unless a test says
/// otherwise, and the text is Courier at 10 pt, so every character is exactly 6 pt wide, a band is
/// 16 pt tall, and the header's baseline is at 267 and the footer's at 23. Expected x values are worked
/// out from those widths, not read back from the file.
/// </summary>
public sealed class BandZonesTests
{
    private const double HeaderBaseline = 267;
    private const double FooterBaseline = 23;

    private static Document NewPage(EdgeInsets? margins = null) => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 300),
        Margins = margins ?? new EdgeInsets(20),
    };

    private static BandZone Z(string text, TextStyle? style = null) => new(text, style ?? Courier10);

    private static RunningBand Band(string left, string center, string right) => new(Z(left), Z(center), Z(right));

    // One page per chapter, each opened by a level-0 heading.
    private static Document Pages(Document doc, int count, bool firstOnNewPage = true)
    {
        for (var i = 1; i <= count; i++)
            doc.Add(new Heading($"H{i}", Courier10)
            {
                Level = 0,
                BreakBefore = i > 1 || firstOnNewPage ? PageBreakMode.Page : PageBreakMode.None,
            });
        return doc;
    }

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static readonly Regex TextPlacement = new(
        @"^1 0 0 1 (?<x>\S+) (?<y>\S+) Tm\n\((?<t>(?:\\.|[^\\)])*)\) Tj$", RegexOptions.Multiline);

    private static double N(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    // The text shown on the baseline y of a page, from the left edge to the right.
    private static List<(double X, string Text)> LineAt(string content, double y) =>
        [.. TextPlacement.Matches(content)
            .Where(m => Math.Abs(N(m.Groups["y"].Value) - y) < 1e-6)
            .Select(m => (N(m.Groups["x"].Value), m.Groups["t"].Value))
            .OrderBy(p => p.Item1)];

    private static void AssertLine(string content, double y, params (double X, string Text)[] expected)
    {
        var actual = LineAt(content, y);
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Text, actual[i].Text);
            Assert.Equal(expected[i].X, actual[i].X, 1e-5);
        }
    }

    // ── Zones ────────────────────────────────────────────────────────────────

    [Fact]
    public void ThreeZones_standOnOneLine_atTheExactX()
    {
        using var doc = Pages(NewPage(), 2);
        doc.Header = Band("LEFT", "CENTRE", "RIGHT");

        var pages = PageContents(Save(doc));

        // 20 is the left edge, 280 - 5 * 6 the start of the right zone, 150 - 36 / 2 the centre's.
        foreach (var page in pages)
            AssertLine(page, HeaderBaseline, (20, "LEFT"), (132, "CENTRE"), (250, "RIGHT"));
    }

    [Fact]
    public void AFooter_stands_atTheFootersBaseline()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Footer = Band("a", "bb", "ccc");

        AssertLine(PageContents(Save(doc))[0], FooterBaseline, (20, "a"), (144, "bb"), (262, "ccc"));
    }

    [Fact]
    public void RunsOfOneZone_followEachOther_withTheirOwnStyles_onOneBaseline()
    {
        var small = new TextStyle { Font = Standard14.Courier, FontSize = 8, Color = new ColorRgb(1, 0, 0) };
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(
            new BandZone([new TextRun("AB", Courier10), new TextRun("CD", small)]),
            BandZone.Empty,
            new BandZone([new TextRun("x", small), new TextRun("yz", Courier10)]));

        var content = PageContents(Save(doc))[0];

        // The first run is 2 * 6 wide, so the second starts at 32. The right zone is 4.8 + 12 wide.
        AssertLine(content, HeaderBaseline, (20, "AB"), (32, "CD"), (263.2, "x"), (268, "yz"));
        Assert.Contains("1 0 0 rg\n1 0 0 1 32 267 Tm", content, StringComparison.Ordinal);
        Assert.Contains(" 8 Tf", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyZone_drawsNothing_andTheOthersStayWhereTheyStand()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(BandZone.Empty, Z("mid"), BandZone.Empty);

        AssertLine(PageContents(Save(doc))[0], HeaderBaseline, (141, "mid"));
    }

    [Fact]
    public void ZonedBandsInAHeaderAndAFooter_bothDraw()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = Band("h", "", "");
        doc.Footer = Band("", "", "f");

        var content = PageContents(Save(doc))[0];
        AssertLine(content, HeaderBaseline, (20, "h"));
        AssertLine(content, FooterBaseline, (274, "f"));
    }

    // ── Tokens ───────────────────────────────────────────────────────────────

    private static RunningBand TokenBand(bool chapterToken = true) => new(
        new BandZone([new TextRun("of ", Courier10), new TextRun("{pages}", Courier10)]),
        Z("{chapter}"),
        Z("{page}"))
    { ChapterToken = chapterToken };

    [Fact]
    public void Tokens_resolveInEveryZone_onEveryPage()
    {
        using var doc = Pages(NewPage(), 12);
        doc.Header = TokenBand();

        var pages = PageContents(Save(doc));

        Assert.Equal(12, pages.Count);
        for (var i = 1; i <= 12; i++)
        {
            var number = i.ToString(CultureInfo.InvariantCulture);
            var title = "H" + number;
            AssertLine(
                pages[i - 1], HeaderBaseline,
                (20, "of "), (38, "12"),
                (150 - 6 * title.Length / 2.0, title),
                (280 - 6 * number.Length, number));
        }
    }

    [Fact]
    public void AChapterToken_staysLiteral_untilTheBandAsksForIt()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = TokenBand(chapterToken: false);

        var line = LineAt(PageContents(Save(doc))[0], HeaderBaseline);

        Assert.Contains(line, p => p.Text == "{chapter}");
    }

    [Fact]
    public void Tokens_resolve_inADocumentWithPageReferences_whichMeasuresAgain()
    {
        using var doc = Pages(NewPage(), 3);
        doc.Header = TokenBand();
        doc.Add(new Paragraph([new TextRun("see page ", Courier10), TextRun.PageReference("end", Courier10)]));
        doc.Add(new Heading("Last", Courier10) { Level = 0, BreakBefore = PageBreakMode.Page, Anchor = "end" });

        var pages = PageContents(Save(doc));

        Assert.Equal(4, pages.Count);
        for (var i = 1; i <= 4; i++)
        {
            var line = LineAt(pages[i - 1], HeaderBaseline);
            Assert.Equal("of ", line[0].Text);
            Assert.Equal("4", line[1].Text);
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), line[^1].Text);
        }
    }

    [Fact]
    public void Page_printsTheLabel_whenTheDocumentDefinesLabelRanges()
    {
        using var doc = NewPage();
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman);
        doc.Header = Band("", "", "{page}");
        Pages(doc, 3);
        doc.Add(new PageBreak { PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal) });
        Pages(doc, 3, firstOnNewPage: false);

        var pages = PageContents(Save(doc));

        // The break opens the fourth page, and the labels of the range start again at 1.
        Assert.Equal(["i", "ii", "iii", "1", "2", "3"], pages.Select(p => LineAt(p, HeaderBaseline)[0].Text));
    }

    // ── Mirrored pages ───────────────────────────────────────────────────────

    // Left margin 60 and right margin 30: a recto page's content box is [60, 270], a verso page's
    // [30, 240].
    private static readonly EdgeInsets Uneven = new(20, 30, 20, 60);

    private static Document Mirrored(bool mirrorMargins, bool mirrorZones, int pages)
    {
        var doc = NewPage(Uneven);
        doc.MirrorMargins = mirrorMargins;
        doc.Header = new RunningBand(Z("LLLL"), Z("CC"), Z("RRRR")) { MirrorZones = mirrorZones };
        return Pages(doc, pages);
    }

    [Fact]
    public void Zones_swapSides_onEvenPages_andNotOnOddOnes()
    {
        using var doc = Mirrored(mirrorMargins: true, mirrorZones: true, pages: 4);

        var pages = PageContents(Save(doc));

        // Odd: left zone at 60, right zone ends at 270. Even: the content box is [30, 240], the
        // right zone stands at 30 and the left zone ends at 240. The centre is 6 pt either side of
        // the box's middle: 165 on an odd page, 135 on an even one.
        AssertLine(pages[0], HeaderBaseline, (60, "LLLL"), (159, "CC"), (246, "RRRR"));
        AssertLine(pages[1], HeaderBaseline, (30, "RRRR"), (129, "CC"), (216, "LLLL"));
        AssertLine(pages[2], HeaderBaseline, (60, "LLLL"), (159, "CC"), (246, "RRRR"));
        AssertLine(pages[3], HeaderBaseline, (30, "RRRR"), (129, "CC"), (216, "LLLL"));
    }

    [Fact]
    public void WithoutMirrorZones_theMirroredMargins_moveTheBox_butNotTheZones()
    {
        using var doc = Mirrored(mirrorMargins: true, mirrorZones: false, pages: 2);

        var pages = PageContents(Save(doc));

        AssertLine(pages[1], HeaderBaseline, (30, "LLLL"), (129, "CC"), (216, "RRRR"));
    }

    [Fact]
    public void MirrorZones_doesNothing_whenTheDocumentDoesNotMirrorItsMargins()
    {
        using var doc = Mirrored(mirrorMargins: false, mirrorZones: true, pages: 2);

        var pages = PageContents(Save(doc));

        AssertLine(pages[1], HeaderBaseline, (60, "LLLL"), (159, "CC"), (246, "RRRR"));
    }

    [Fact]
    public void EvenMeans_thePhysicalPage_notTheLabel_withRomanFrontMatterAndADecimalBody()
    {
        using var doc = NewPage(Uneven);
        doc.MirrorMargins = true;
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman);
        doc.Header = new RunningBand(Z("L"), Z("{page}"), Z("R")) { MirrorZones = true };
        Pages(doc, 3);
        doc.Add(new PageBreak { PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal) });
        Pages(doc, 3, firstOnNewPage: false);

        var pages = PageContents(Save(doc));

        // Physical pages 1 to 6 carry the labels i, ii, iii, 1, 2, 3. Physical 4 is labelled "1", an
        // odd label, and swaps; physical 5 is labelled "2", an even label, and does not.
        var swapped = pages.Select(p => LineAt(p, HeaderBaseline)[0].Text == "R").ToArray();
        Assert.Equal([false, true, false, true, false, true], swapped);
        Assert.Equal(["i", "ii", "iii", "1", "2", "3"], pages.Select(p => LineAt(p, HeaderBaseline)[1].Text));
    }

    // ── Rule ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ARuleBelowAHeader_isDrawnOnEveryPage_atTheExactCoordinatesAndWidth()
    {
        using var doc = Pages(NewPage(), 3);
        doc.Header = new RunningBand(Z("a"), Z("b"), Z("c")) { Rule = new BandRule(BandRulePosition.Below, 1.5, new ColorRgb(1, 0, 0)) };

        // The band spans y 20 to 36 from the top. The 1.5 pt line is centred 0.75 pt inside the
        // bottom edge: 35.25 from the top, 264.75 up.
        foreach (var page in PageContents(Save(doc)))
        {
            Assert.Contains("q\n1 0 0 RG\n1.5 w\n20 264.75 m\n280 264.75 l\nS\nQ\n", page, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(page, @" w\n"));
        }
    }

    [Fact]
    public void ARuleAboveAFooter_liesOnTheBandsTopEdge_inTheMirroredMarginsOfItsPage()
    {
        using var doc = Pages(NewPage(Uneven), 2);
        doc.MirrorMargins = true;
        doc.Footer = new RunningBand(Z("a"), Z("b"), Z("c")) { Rule = new BandRule(BandRulePosition.Above) };

        var pages = PageContents(Save(doc));

        // The footer spans 264 to 280 from the top, so the 0.5 pt line's centre is 264.25 from the
        // top, 35.75 up. A recto page spans 60 to 270, a verso page 30 to 240.
        Assert.Contains("0 0 0 RG\n0.5 w\n60 35.75 m\n270 35.75 l\nS\n", pages[0], StringComparison.Ordinal);
        Assert.Contains("0 0 0 RG\n0.5 w\n30 35.75 m\n240 35.75 l\nS\n", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleOnAStringBand_isDrawn_andTheTextIsAsItWas()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand("title", Courier10, HorizontalAlignment.Left)
        {
            Rule = new BandRule(BandRulePosition.Below, 0),
        };

        var content = PageContents(Save(doc))[0];

        AssertLine(content, HeaderBaseline, (20, "title"));
        Assert.Contains("0 w\n20 264 m\n280 264 l\nS\n", content, StringComparison.Ordinal);
    }

    // ── Logo ─────────────────────────────────────────────────────────────────

    private static LayoutImage Logo(int width = 8, int height = 4) =>
        LayoutImage.FromBytes(ImageFixtures.Jpeg(width, height));

    [Fact]
    public void ALogo_isScaledToTheBandsHeight_atItsZonesEdge_asDecoration()
    {
        using var doc = Pages(NewPage(), 2);
        doc.Tagged = true;
        doc.Header = new RunningBand(BandZone.Logo(Logo()), BandZone.Empty, Z("p{page}")) { Height = 30 };

        var bytes = Save(doc);
        var pages = PageContents(bytes);

        // An 8 x 4 image at 30 pt tall is 60 wide. The band is y 20 to 50 from the top, so its bottom
        // is 250 up.
        foreach (var page in pages)
        {
            Assert.Contains("q\n60 0 0 30 20 250 cm\n/", page, StringComparison.Ordinal);
            Assert.Contains("/Artifact BMC", page, StringComparison.Ordinal);
        }

        // Decoration: no Figure element, so no alternative text to give.
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("/Figure", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogoInEachZone_standsAtThatZonesPlace()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(BandZone.Logo(Logo()), BandZone.Logo(Logo(4, 4)), BandZone.Logo(Logo(8, 2))) { Height = 20 };

        var content = PageContents(Save(doc))[0];

        // Widths at 20 pt tall: 40, 20 and 80. Left at 20, centred at 140, right ends at 280.
        Assert.Contains("40 0 0 20 20 260 cm", content, StringComparison.Ordinal);
        Assert.Contains("20 0 0 20 140 260 cm", content, StringComparison.Ordinal);
        Assert.Contains("80 0 0 20 200 260 cm", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogoThatDoesNotFitBesideTheOthers_isRefusedBySave_namingTheBand()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(BandZone.Logo(Logo()), BandZone.Empty, BandZone.Logo(Logo())) { Height = 70 };

        // Two logos of 140 pt in a box of 260 with a gap of 6: 286 do not fit.
        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("header band", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogoWiderThanTheContentBox_isRefusedBySave()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Footer = new RunningBand(BandZone.Empty, BandZone.Logo(Logo()), BandZone.Empty) { Height = 140 };

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("footer band", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGapBetweenLogos_decidesWhetherTheyFit_atItsBoundary()
    {
        // Heights of 24: the logos are 48, 24 and 96 wide. The centre one stands at 118 to 142 in a
        // box 260 wide, the left one ends at 48 and the right one starts at 164, so the tightest
        // neighbours are the centre and the right logo, 22 apart.
        static RunningBand Logos(double gap) => new(
            BandZone.Logo(Logo()), BandZone.Logo(Logo(4, 4)), BandZone.Logo(Logo(8, 2)))
        { Height = 24, ZoneGap = gap };

        using var fits = Pages(NewPage(), 1);
        fits.Header = Logos(22);
        Assert.NotEmpty(Save(fits));

        using var tight = Pages(NewPage(), 1);
        tight.Header = Logos(22.5);
        Assert.Throws<InvalidOperationException>(() => Save(tight));
    }

    // ── Collisions ───────────────────────────────────────────────────────────

    [Fact]
    public void ALongCentre_yieldsToTheRightZone_andIsCutAtAWord_andReported()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = Band("", "ONE TWO THREE FOUR FIVE SIX SEVEN EIGHT NINE TEN", "{page}");

        var content = PageContents(Save(doc))[0];

        // The right zone is 6 wide, so it starts at 274 and the centre may reach 268: 118 to the
        // right of 150, so 236 wide, 39 characters. The word cut keeps 33, 198 wide, from 51.
        AssertLine(content, HeaderBaseline, (51, "ONE TWO THREE FOUR FIVE SIX SEVEN"), (274, "1"));
        var warning = Assert.Single(doc.BandTruncations);
        Assert.Equal(RunningBandKind.Header, warning.Band);
        Assert.Equal(1, warning.PageNumber);
        Assert.Equal(34, warning.DrawnCharacters);
        Assert.Equal(49, warning.ResolvedCharacters);
        Assert.Contains(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.BandTruncated);
    }

    [Fact]
    public void ALongLeft_yieldsToTheRightZone_andTheCutZoneStaysTheLeftOneWhenZonesSwap()
    {
        var left = "aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj";
        foreach (var swapped in new[] { false, true })
        {
            using var doc = Pages(NewPage(), 2);
            doc.MirrorMargins = true;
            doc.Header = new RunningBand(Z(left), BandZone.Empty, Z("RR")) { MirrorZones = true };

            var pages = PageContents(Save(doc));
            var page = pages[swapped ? 1 : 0];

            // Margins are 20 on each side, so both pages span [20, 280]. The right zone is 12 wide,
            // so the left has 242 to the 6 pt gap: 40 characters, 39 after the word cut, 234 wide.
            if (swapped)
                AssertLine(page, HeaderBaseline, (20, "RR"), (46, "aaaa bbbb cccc dddd eeee ffff gggg hhhh"));
            else
                AssertLine(page, HeaderBaseline, (20, "aaaa bbbb cccc dddd eeee ffff gggg hhhh"), (268, "RR"));
        }
    }

    [Fact]
    public void ACentreNextToALogo_andALeftBesideIt_yieldToTheLogo()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(Z("aaaa bbbb cccc dddd eeee ffff"), BandZone.Logo(Logo()), BandZone.Empty) { Height = 20 };

        var content = PageContents(Save(doc))[0];

        // The logo is 40 wide, centred at 130 to 170. The left zone may reach 124: 104 wide, 17
        // characters, cut to 14 at the space. A band 20 tall puts its baseline at 265.
        AssertLine(content, 265, (20, "aaaa bbbb cccc"));
        Assert.Contains("40 0 0 20 130 260 cm", content, StringComparison.Ordinal);
        Assert.Single(doc.BandTruncations);
    }

    [Fact]
    public void AZoneCut_failsASaveInStrictMode()
    {
        using var doc = Pages(NewPage(), 1);
        doc.StrictDiagnostics = true;
        doc.Header = Band(new string('x', 60), "", "y");

        Assert.Throws<InvalidOperationException>(() => Save(doc));
    }

    [Fact]
    public void ARunAfterTheCut_isNotDrawn_andItsCharactersAreCounted()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(
            new BandZone([new TextRun(new string('x', 50), Courier10), new TextRun("tail", Courier10)]),
            BandZone.Empty,
            BandZone.Empty);

        var content = PageContents(Save(doc))[0];

        // 43 of 50 fit in 260 and there is no space to cut at, so the run is cut at 43 characters.
        AssertLine(content, HeaderBaseline, (20, new string('x', 43)));
        var warning = Assert.Single(doc.BandTruncations);
        Assert.Equal(43, warning.DrawnCharacters);
        Assert.Equal(54, warning.ResolvedCharacters);
    }

    [Fact]
    public void Zones_neverOverprint_whateverTheirLengths_onEitherKindOfPage()
    {
        var random = new Random(365);
        for (var round = 0; round < 120; round++)
        {
            string Text() => new string('m', random.Next(0, 70)).Replace("mmmmm", "mmmm ");
            var texts = new[] { Text(), Text(), Text() };
            using var doc = Pages(NewPage(), 2);
            doc.MirrorMargins = true;
            doc.Header = new RunningBand(Z(texts[0]), Z(texts[1]), Z(texts[2]))
            {
                MirrorZones = round % 2 == 0,
                ZoneGap = 3 + round % 7,
            };

            foreach (var page in PageContents(Save(doc)))
            {
                var line = LineAt(page, HeaderBaseline);
                for (var i = 0; i < line.Count; i++)
                {
                    var end = line[i].X + 6 * line[i].Text.Length;
                    Assert.True(line[i].X >= 20 - 1e-6 && end <= 280 + 1e-6, $"round {round}: zone outside the box");
                    if (i > 0)
                    {
                        var before = line[i - 1].X + 6 * line[i - 1].Text.Length;
                        Assert.True(line[i].X - before >= doc.Header.ZoneGap - 1e-6, $"round {round}: zones {i - 1} and {i} are too close");
                    }
                }
            }
        }
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void ABandRefuses_aNullZone_namingIt()
    {
        Assert.Equal("left", Assert.Throws<ArgumentNullException>(() => new RunningBand(null!, BandZone.Empty, BandZone.Empty)).ParamName);
        Assert.Equal("center", Assert.Throws<ArgumentNullException>(() => new RunningBand(BandZone.Empty, null!, BandZone.Empty)).ParamName);
        Assert.Equal("right", Assert.Throws<ArgumentNullException>(() => new RunningBand(BandZone.Empty, BandZone.Empty, null!)).ParamName);
    }

    [Fact]
    public void AZoneRefuses_nullRuns_aNullRun_aNullText_aNullStyle()
    {
        Assert.Throws<ArgumentNullException>(() => new BandZone((IEnumerable<TextRun>)null!));
        Assert.Throws<ArgumentException>(() => new BandZone([null!]));
        Assert.Throws<ArgumentNullException>(() => new BandZone((string)null!, Courier10));
        Assert.Throws<ArgumentNullException>(() => new BandZone("x", null!));
        Assert.Throws<ArgumentException>(() => new BandZone([new TextRun("x", null!)]));
    }

    [Fact]
    public void AZoneRefuses_aPageReference_andALink()
    {
        Assert.Throws<ArgumentException>(() => new BandZone([TextRun.PageReference("a", Courier10)]));
        Assert.Throws<ArgumentException>(() => new BandZone("x", new TextStyle { Font = Standard14.Courier, LinkUri = "https://example.com" }));
        Assert.Throws<ArgumentException>(() => new BandZone("x", new TextStyle { Font = Standard14.Courier, LinkAnchor = "a" }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-3)]
    public void AZoneRefuses_aFontSizeThatIsNotAPositiveFiniteNumber(double size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandZone("x", new TextStyle { Font = Standard14.Courier, FontSize = size }));

    [Theory]
    [InlineData(1.5, 0, 0)]
    [InlineData(0, -0.1, 0)]
    [InlineData(0, 0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0, 0)]
    public void AZoneRefuses_aTextColourOutsideZeroToOne(double r, double g, double b) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandZone("x", new TextStyle { Font = Standard14.Courier, Color = new ColorRgb(r, g, b) }));

    [Fact]
    public void ALogoZone_refusesNull_andAnImageWithNoArea()
    {
        Assert.Throws<ArgumentNullException>(() => BandZone.Logo(null!));
        Assert.Throws<ArgumentException>(() => BandZone.Logo(Logo(0, 4)));
        Assert.Throws<ArgumentException>(() => BandZone.Logo(Logo(8, 0)));
    }

    [Fact]
    public void AZoneCopiesItsRuns()
    {
        var runs = new List<TextRun> { new("a", Courier10) };
        var zone = new BandZone(runs);
        runs.Add(new TextRun("b", Courier10));

        Assert.Single(zone.Runs);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.5)]
    public void ARuleRefuses_aWidthThatIsNotAFiniteNumberOfAtLeastZero(double width) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandRule(BandRulePosition.Below, width));

    [Theory]
    [InlineData(1.5, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, double.NaN)]
    public void ARuleRefuses_aColourOutsideZeroToOne(double r, double g, double b) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandRule(BandRulePosition.Below, 1, new ColorRgb(r, g, b)));

    [Fact]
    public void ARuleRefuses_anUnnamedPosition() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandRule((BandRulePosition)7));

    [Fact]
    public void ARuleOfZeroWidth_isAccepted() =>
        Assert.Equal(0, new BandRule(BandRulePosition.Above, 0).Width);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void AZoneGap_refusesWhatIsNotAFiniteNumberOfAtLeastZero(double gap) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunningBand(BandZone.Empty, BandZone.Empty, BandZone.Empty) { ZoneGap = gap });

    [Fact]
    public void AStringBand_refusesTheZoneMembers()
    {
        Assert.Throws<InvalidOperationException>(() => new RunningBand("x") { MirrorZones = true });
        Assert.Throws<InvalidOperationException>(() => new RunningBand("x") { ZoneGap = 3 });
        Assert.Null(new RunningBand("x").Left);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(9.5)]
    public void AZonedBand_refusesAHeightItsTextCannotFitIn(double height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunningBand(Z("x"), BandZone.Empty, BandZone.Empty) { Height = height });

    [Fact]
    public void AZonedBand_acceptsAHeightEqualToItsLargestFontSize()
    {
        var band = new RunningBand(Z("x"), BandZone.Empty, BandZone.Empty) { Height = 10 };

        Assert.Equal(10, band.EffectiveHeight);
    }

    [Fact]
    public void AZonedBand_defaultHeight_isTheLargestLeadingPlusFour()
    {
        Assert.Equal(16, new RunningBand(Z("x"), BandZone.Empty, BandZone.Empty).EffectiveHeight);
        Assert.Equal(
            TextStyle.Default.EffectiveLeading + 4,
            new RunningBand(BandZone.Empty, BandZone.Empty, BandZone.Empty).EffectiveHeight);
    }

    [Fact]
    public void ARuleThatTheHeightCannotHold_isRefusedBySave_namingTheBand()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Footer = new RunningBand(Z("x"), BandZone.Empty, BandZone.Empty) { Height = 11, Rule = new BandRule(BandRulePosition.Above, 1) };

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("footer band", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_isRefused_onAZonedBand() =>
        Assert.Throws<InvalidOperationException>(() => Band("a", "b", "c").Resolve(1, 1));
}
