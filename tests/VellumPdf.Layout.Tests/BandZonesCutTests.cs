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
/// #365, the cut of a zone and the places of a zone: a zone is cut as one text, a token's value is
/// never cut inside, a zone of zero width has a place, and what can be known at build is refused at
/// build. Pages are 300 x 300 pt with 20 pt margins unless a test says otherwise, in Courier at 10 pt,
/// so every character is 6 pt wide.
/// </summary>
public sealed class BandZonesCutTests
{
    private static Document NewPage(double margin = 20, double width = 300) => new()
    {
        PageSize = new PdfRectangle(0, 0, width, 300),
        Margins = new EdgeInsets(margin),
    };

    private static Document Pages(Document doc, int count, string title = "H")
    {
        for (var i = 1; i <= count; i++)
            doc.Add(new Heading(title == "H" ? $"H{i}" : title, Courier10) { Level = 0, BreakBefore = PageBreakMode.Page });
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

    // What the page shows above y = 262: the band, whatever its font, from the left to the right.
    private static List<(double X, string Text)> Header(string content) =>
        [.. TextPlacement.Matches(content)
            .Where(m => double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) > 262)
            .Select(m => (double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture), m.Groups["t"].Value))
            .OrderBy(p => p.Item1)];

    private static BandZone Runs(params string[] texts) => new(texts.Select(t => new TextRun(t, Courier10)));

    // ── H1: a zone is cut as one text ────────────────────────────────────────

    [Fact]
    public void ALabelAndANumber_inTwoRuns_areCutAtTheSpace_andNeverPrintAHalfNumber()
    {
        // The right zone is 36 characters, 216 pt, which leaves the left zone 260 - 216 - 6 = 38 pt:
        // room for "Page 1" (36 pt) and not for "Page 12".
        using var doc = Pages(NewPage(), 12);
        doc.Header = new RunningBand(Runs("Page ", "{page}"), BandZone.Empty, new BandZone(new string('R', 36), Courier10));

        var pages = PageContents(Save(doc));

        Assert.Equal([(20.0, "Page "), (50.0, "1"), (64.0, new string('R', 36))], Header(pages[0]));
        Assert.Equal([(20.0, "Page"), (64.0, new string('R', 36))], Header(pages[11]));
        Assert.Equal([(20.0, "Page"), (64.0, new string('R', 36))], Header(pages[9]));
        Assert.DoesNotContain(Header(pages[11]), p => p.Text is "1" or "12" or "Page 1");
        Assert.Contains(doc.BandTruncations, w => w.PageNumber == 10);
    }

    [Fact]
    public void ATokenAtARunBoundary_isDrawnWholeOrNotAtAll()
    {
        // "abcde" is 30 pt and the page number 6 pt a digit: 38 pt holds "abcde1" and not "abcde12".
        using var doc = Pages(NewPage(), 12);
        doc.Header = new RunningBand(Runs("abcde", "{page}"), BandZone.Empty, new BandZone(new string('R', 36), Courier10));

        var pages = PageContents(Save(doc));

        Assert.Equal([(20.0, "abcde"), (50.0, "1"), (64.0, new string('R', 36))], Header(pages[0]));
        Assert.Equal([(20.0, "abcde"), (64.0, new string('R', 36))], Header(pages[11]));
    }

    [Fact]
    public void AChapterTitle_isNotCutAtASpaceInsideIt_andAZoneThatHoldsOnlyItIsDropped()
    {
        using var doc = NewPage();
        doc.Add(new Heading("Alpha Beta Gamma", Courier10) { Level = 0 });
        doc.Header = new RunningBand(Runs("{chapter}"), BandZone.Empty, new BandZone(new string('R', 36), Courier10))
        {
            ChapterToken = true,
        };

        var line = Header(PageContents(Save(doc))[0]);

        // 38 pt would hold "Alpha" (30 pt), which is before a space of the title. The title is a token.
        Assert.Equal([(64.0, new string('R', 36))], line);
        var warning = Assert.Single(doc.BandTruncations);
        Assert.Equal(36, warning.DrawnCharacters);
        Assert.Equal(52, warning.ResolvedCharacters);
    }

    [Fact]
    public void ALiteralSpaceAfterAToken_isACutPoint_whereTheRunsMeet()
    {
        // "{page}" + " rest of the label": the space that opens the second run is a boundary. The zone has
        // 38 pt and the walk stops at the space after "rest", which does not fit, so the last boundary it
        // passed is the one between the number and "rest", as for a string band's text.
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(Runs("{page}", " rest of the label"), BandZone.Empty, new BandZone(new string('R', 36), Courier10));

        Assert.Equal([(20.0, "1"), (64.0, new string('R', 36))], Header(PageContents(Save(doc))[0]));
    }

    [Fact]
    public void ARunBoundaryWithNoSpace_isNotAWordBoundary()
    {
        // "ab" + "cd ef": room for "abcd e": the space inside the second run is the cut.
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(Runs("ab", "cd ef"), BandZone.Empty, new BandZone(new string('R', 37), Courier10));

        // 260 - 222 - 6 = 32 pt: five characters, "abcd " fits and "e" does not. Cut before the space.
        var line = Header(PageContents(Save(doc))[0]);

        Assert.Equal([(20.0, "ab"), (32.0, "cd"), (58.0, new string('R', 37))], line);
    }

    [Fact]
    public void ARunBoundaryWithNoSpace_inAZoneWithNoSpace_cutsInsideTheRunAndNotAtTheBoundary()
    {
        // No literal space anywhere: the zone falls back to the last place that fits, mid-word as a
        // string band's single long word is, and the point where the runs meet is not a cut.
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(Runs("ab", "cdefghijklmnopqrstuvwxyz"), BandZone.Empty, new BandZone(new string('R', 37), Courier10));

        var line = Header(PageContents(Save(doc))[0]);

        Assert.Equal([(20.0, "ab"), (32.0, "cde"), (58.0, new string('R', 37))], line);
    }

    [Fact]
    public void AZoneCutAcrossRuns_neverPrintsAPartOfATokenValue_forAnySplitOfTheTemplate()
    {
        var random = new Random(3650);
        var template = "Page {page} of {pages} - {chapter} end";
        for (var round = 0; round < 150; round++)
        {
            // Split the template at random places outside the tokens.
            var tokens = Regex.Matches(template, @"\{[a-z]+\}").Select(m => (m.Index, End: m.Index + m.Length)).ToList();
            var cuts = Enumerable.Range(1, template.Length - 1)
                .Where(i => !tokens.Any(t => i > t.Index && i < t.End) && random.Next(3) == 0)
                .ToList();
            var parts = new List<string>();
            var from = 0;
            foreach (var cut in cuts) { parts.Add(template[from..cut]); from = cut; }
            parts.Add(template[from..]);

            var margin = 20 + random.Next(0, 4) * 10;
            using var doc = NewPage(margin, 160 + random.Next(0, 15) * 10);
            doc.Add(new Heading("Alpha Beta", Courier10) { Level = 0 });
            for (var i = 2; i <= 12; i++)
                doc.Add(new Heading("Alpha Beta", Courier10) { Level = 0, BreakBefore = PageBreakMode.Page });
            doc.Header = new RunningBand(new BandZone(parts.Select(t => new TextRun(t, Courier10))), BandZone.Empty, BandZone.Empty)
            {
                ChapterToken = true,
            };

            var pages = PageContents(Save(doc));
            for (var p = 0; p < pages.Count; p++)
            {
                var resolved = template
                    .Replace("{pages}", pages.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{page}", (p + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("{chapter}", "Alpha Beta", StringComparison.Ordinal);
                var drawn = string.Concat(Header(pages[p]).Select(x => x.Text));

                Assert.StartsWith(drawn, resolved, StringComparison.Ordinal);

                // The values on this page, by where they stand in the resolved text.
                var spans = new List<(int Start, int End)>();
                var offset = 0;
                foreach (Match piece in Regex.Matches(template, @"\{[a-z]+\}|[^{]+|\{"))
                {
                    var value = piece.Value switch
                    {
                        "{page}" => (p + 1).ToString(CultureInfo.InvariantCulture),
                        "{pages}" => pages.Count.ToString(CultureInfo.InvariantCulture),
                        "{chapter}" => "Alpha Beta",
                        _ => null,
                    };
                    if (value is not null) spans.Add((offset, offset + value.Length));
                    offset += value?.Length ?? piece.Length;
                }

                Assert.DoesNotContain(spans, s => drawn.Length > s.Start && drawn.Length < s.End);
            }
        }
    }

    // ── M1: a zone that measures no width still has a place ──────────────────

    private static readonly TextStyle SymbolStyle = new() { Font = Standard14.Symbol, FontSize = 10 };

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void AZoneThatMeasuresZero_standsAtItsSidesEdge_insideTheContentBox(double margin)
    {
        using var doc = NewPage(margin);
        doc.Add(new Paragraph("body", Courier10) { Margins = new EdgeInsets(200, 0, 0, 0) });
        doc.Header = new RunningBand(Z("LEFT"), new BandZone("abc", SymbolStyle), new BandZone("xyz", SymbolStyle));

        var line = Header(PageContents(Save(doc))[0]);

        // LEFT at the left edge. The centre one at the centre, the right one at the right edge, 300 - margin.
        Assert.Equal(
            [(margin, "LEFT"), (150.0, "abc"), (300.0 - margin, "xyz")],
            line);
        Assert.All(line, p => Assert.InRange(p.X, margin, 300 - margin));
    }

    private static BandZone Z(string text) => new(text, Courier10);

    // ── M2: what can be known at build is refused at build ───────────────────

    [Fact]
    public void AZone_refusesAStandard14ValueTheEnumerationDoesNotName()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BandZone("a", new TextStyle { FontRef = (Standard14)99 }));
        Assert.Equal("runs", ex.ParamName);
    }

    // The attribute cannot hold a lone surrogate (the metadata is UTF-8), so the unit is built here.
    [Theory]
    [InlineData("a", 0xD800, "b")]
    [InlineData("", 0xDC00, "")]
    [InlineData("tail", 0xD83D, "")]
    public void AZone_refusesAnUnpairedSurrogate_inAnyFont(string before, int unit, string after)
    {
        var text = before + (char)unit + after;
        Assert.Throws<ArgumentException>(() => new BandZone(text, Courier10));
        using var doc = new Document();
        var embedded = new TextStyle { FontRef = doc.LoadTrueTypeFont(DejaVuPath), FontSize = 10 };
        Assert.Throws<ArgumentException>(() => new BandZone([new TextRun("ok", Courier10), new TextRun(text, embedded)]));
    }

    [Fact]
    public void AZone_acceptsAPairedSurrogate()
    {
        Assert.NotNull(new BandZone("a😀b", Courier10));
    }

    [Fact]
    public void AFontHandleFromAnotherDocument_isRefusedBySave()
    {
        using var other = new Document();
        var foreign = new TextStyle { FontRef = other.LoadTrueTypeFont(DejaVuPath), FontSize = 10 };
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(new BandZone("x", foreign), BandZone.Empty, BandZone.Empty);

        Assert.Throws<ArgumentException>(() => Save(doc));
    }

    [Theory]
    [InlineData(1e-6)]
    [InlineData(4.9e-6)]
    public void AZone_refusesAFontSizeThatIsWrittenAsZero(double size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandZone("x", new TextStyle { Font = Standard14.Courier, FontSize = size }));

    [Fact]
    public void ALogoThatWouldBeWrittenWithNoArea_isRefusedBySave_namingTheBand()
    {
        // The band is 2e-6 pt tall, so the logo is.
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(BandZone.Logo(LayoutImage.FromBytes(ImageFixtures.Jpeg(8, 4))), BandZone.Empty, BandZone.Empty)
        {
            Height = 2e-6,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("header band", ex.Message, StringComparison.Ordinal);
    }

    // ── M5: the behaviours a mutant could drop ───────────────────────────────

    [Fact]
    public void ASecondRunThatOverflows_isCut_andStaysInTheContentBox()
    {
        using var doc = Pages(NewPage(), 1);
        var second = string.Join(' ', Enumerable.Range(0, 12).Select(k => new string((char)('a' + k), 4)));
        doc.Header = new RunningBand(Runs("short ", second), BandZone.Empty, BandZone.Empty);

        var line = Header(PageContents(Save(doc))[0]);

        // 260 pt holds 43 characters of the 65. The zone is cut before the last space it passed, which
        // is character 40, so the second run keeps its first 34.
        var whole = "short " + second;
        Assert.Equal(' ', whole[40]);
        Assert.Equal([(20.0, "short "), (56.0, second[..34])], line);
        Assert.True(line[^1].X + 6 * line[^1].Text.Length <= 280 + 1e-9);
    }

    [Fact]
    public void ALogoLeftAndALogoAtTheCentre_keepTheGap_atItsBoundary()
    {
        // Heights of 24: the left logo is 48 wide and ends at 48, the centre one is 24 wide and starts at
        // 118: 70 pt apart.
        static RunningBand Logos(double gap) => new(
            BandZone.Logo(LayoutImage.FromBytes(ImageFixtures.Jpeg(8, 4))),
            BandZone.Logo(LayoutImage.FromBytes(ImageFixtures.Jpeg(4, 4))),
            BandZone.Empty)
        { Height = 24, ZoneGap = gap };

        using var fits = Pages(NewPage(), 1);
        fits.Header = Logos(70);
        Assert.NotEmpty(Save(fits));

        using var tight = Pages(NewPage(), 1);
        tight.Header = Logos(70.5);
        Assert.Throws<InvalidOperationException>(() => Save(tight));
    }

    [Fact]
    public void ALeftLogoAndARightLogo_keepTheGap_atItsBoundary()
    {
        // Left 48 wide and right 96 wide in a box 260 wide: 116 pt of room.
        static RunningBand Logos(double gap) => new(
            BandZone.Logo(LayoutImage.FromBytes(ImageFixtures.Jpeg(8, 4))),
            BandZone.Empty,
            BandZone.Logo(LayoutImage.FromBytes(ImageFixtures.Jpeg(8, 2))))
        { Height = 24, ZoneGap = gap };

        using var fits = Pages(NewPage(), 1);
        fits.Header = Logos(116);
        Assert.NotEmpty(Save(fits));

        using var tight = Pages(NewPage(), 1);
        tight.Header = Logos(116.5);
        Assert.Throws<InvalidOperationException>(() => Save(tight));
    }

    [Fact]
    public void AStringBandsRule_isInsideTheArtifactMarkedContent_ofATaggedDocument()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Tagged = true;
        doc.Header = new RunningBand("title", Courier10, HorizontalAlignment.Left)
        {
            Rule = new BandRule(BandRulePosition.Below, 1),
        };

        var content = PageContents(Save(doc))[0];

        var stroke = content.IndexOf(" w\n", StringComparison.Ordinal);
        Assert.True(stroke > 0, content);
        var start = content.LastIndexOf("/Artifact BMC", stroke, StringComparison.Ordinal);
        var end = content.IndexOf("EMC", stroke, StringComparison.Ordinal);
        var otherEnd = start < 0 ? -1 : content.IndexOf("EMC", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > stroke && otherEnd == end, "the rule must lie between /Artifact BMC and its EMC:\n" + content);
    }

    [Fact]
    public void ATruncationWarning_namesThePageThatLostTheMost()
    {
        // Pages 1 and 2 hold a short chapter title, page 3 a long one, so only page 3 is cut.
        using var doc = NewPage();
        doc.Add(new Heading("Short", Courier10) { Level = 0 });
        doc.Add(new Heading("Short", Courier10) { Level = 0, BreakBefore = PageBreakMode.Page });
        doc.Add(new Heading(new string('w', 30) + " " + new string('v', 60), Courier10) { Level = 0, BreakBefore = PageBreakMode.Page });
        doc.Header = new RunningBand(BandZone.Empty, Runs("{chapter}"), BandZone.Empty) { ChapterToken = true };

        Save(doc);

        var warning = Assert.Single(doc.BandTruncations);
        Assert.Equal(3, warning.PageNumber);
    }

    [Fact]
    public void ARule_isDrawnOnEveryPage_whereTheZonesDrawNothing()
    {
        using var doc = Pages(NewPage(), 3);
        doc.Footer = new RunningBand(BandZone.Empty, BandZone.Empty, BandZone.Empty) { Rule = new BandRule(BandRulePosition.Above, 1) };

        var pages = PageContents(Save(doc));

        Assert.Equal(3, pages.Count);

        // The band has no text, so its height is the default leading plus 4, 18.4: it spans 261.6 to 280
        // from the top, and the 1 pt line is centred at 262.1.
        Assert.All(pages, page => Assert.Contains("1 w\n20 37.9 m\n280 37.9 l\nS\n", page, StringComparison.Ordinal));
    }

    [Fact]
    public void ALogoBesideARule_keepsOneClearPointFromIt_aboveAndBelow()
    {
        var logo = BandZone.Logo(LayoutImage.FromBytes(ImageFixtures.Jpeg(4, 4)));

        // Band y 20 to 40. Below: the rule is y 38 to 40, the logo 20 to 37, 17 pt tall and wide.
        using var below = Pages(NewPage(), 1);
        below.Header = new RunningBand(logo, BandZone.Empty, BandZone.Empty) { Height = 20, Rule = new BandRule(BandRulePosition.Below, 2) };
        Assert.Contains("17 0 0 17 20 263 cm", PageContents(Save(below))[0], StringComparison.Ordinal);

        // Above: the rule is y 20 to 22, the logo 23 to 40, ending at 260 up.
        using var above = Pages(NewPage(), 1);
        above.Header = new RunningBand(logo, BandZone.Empty, BandZone.Empty) { Height = 20, Rule = new BandRule(BandRulePosition.Above, 2) };
        Assert.Contains("17 0 0 17 20 260 cm", PageContents(Save(above))[0], StringComparison.Ordinal);
    }

    // ── Review r735-2 ────────────────────────────────────────────────────────

    [Fact]
    public void ZonesThatTogetherResolveToMoreThanIntMaxValue_areRefused_namingTheBand()
    {
        // Each run is 800,000,000 characters, under the limit a run is checked against, and three of
        // them add up to 2,400,000,000.
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 400), Margins = new EdgeInsets(20) };
        doc.Add(new Heading("Short", Courier10) { Level = 0, BookmarkTitle = new string('x', 20_000) });
        var run = string.Concat(Enumerable.Repeat("{chapter}", 40_000));
        doc.Header = new RunningBand(Runs(run), Runs(run), Runs(run)) { ChapterToken = true };

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("header band", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoZonesOfTheSameSize_stillSave_andReportTheirCharacters()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 400), Margins = new EdgeInsets(20) };
        doc.Add(new Heading("Short", Courier10) { Level = 0, BookmarkTitle = new string('x', 20_000) });
        var run = string.Concat(Enumerable.Repeat("{chapter}", 40_000));
        doc.Header = new RunningBand(Runs(run), Runs(run), BandZone.Empty) { ChapterToken = true };

        Save(doc);

        Assert.Equal(1_600_000_000, Assert.Single(doc.BandTruncations).ResolvedCharacters);
    }

    [Theory]
    [InlineData(Standard14.Helvetica, 11.5)]
    [InlineData(Standard14.TimesRoman, 11)]
    [InlineData(Standard14.Courier, 10.5)]
    public void AHeightAboveTheFontSize_butBelowTheTextBox_isRefused(Standard14 face, double height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunningBand(new BandZone("x", new TextStyle { Font = face, FontSize = 10 }), BandZone.Empty, BandZone.Empty) { Height = height });

    [Fact]
    public void ADefaultHeight_isTheSameOnEveryRead_whateverTheNumberOfRuns()
    {
        var zone = new BandZone(Enumerable.Range(0, 2000).Select(i => new TextRun("ab ", Courier10)));
        var band = new RunningBand(zone, BandZone.Empty, BandZone.Empty) { Rule = new BandRule(BandRulePosition.Below, 1) };

        Assert.Equal(16, band.EffectiveHeight);
        Assert.Equal(16, band.EffectiveHeight);
    }

    // The two exclusions of the cut, and the characters the report counts.
    [Fact]
    public void ASpaceAtTheStartOfAZone_isNotACutPoint()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(Runs("   " + new string('w', 50)), BandZone.Empty, BandZone.Empty);

        // 260 pt holds 43 characters: the three spaces and 40 w. A cut at the first space would draw nothing.
        Assert.Equal([(20.0, "   " + new string('w', 40))], Header(PageContents(Save(doc))[0]));
        var warning = Assert.Single(doc.BandTruncations);
        Assert.Equal((43, 53), (warning.DrawnCharacters, warning.ResolvedCharacters));
    }

    [Fact]
    public void InARunOfSpaces_onlyTheFirstIsACutPoint()
    {
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(Runs("ab  " + new string('w', 50)), BandZone.Empty, BandZone.Empty);

        // A cut at the second space would draw "ab " and charge the zone for a space with no ink.
        Assert.Equal([(20.0, "ab")], Header(PageContents(Save(doc))[0]));
        var warning = Assert.Single(doc.BandTruncations);
        Assert.Equal((2, 54), (warning.DrawnCharacters, warning.ResolvedCharacters));
    }

    [Fact]
    public void ACut_neverFallsBetweenTheHalvesOfASurrogatePair()
    {
        // 100 pairs, 200 UTF-16 units, in a face that gives each unit 6 pt: 43 units fit, an odd number.
        using var doc = Pages(NewPage(), 1);
        doc.Header = new RunningBand(Runs(string.Concat(Enumerable.Repeat("\U0001D400", 100))), BandZone.Empty, BandZone.Empty);

        Save(doc);

        var warning = Assert.Single(doc.BandTruncations);
        Assert.Equal(200, warning.ResolvedCharacters);
        Assert.True(warning.DrawnCharacters is > 0 && warning.DrawnCharacters % 2 == 0, $"drew {warning.DrawnCharacters}");
    }

    [Theory]
    [InlineData(5e-6)]
    [InlineData(7e-6)]
    [InlineData(1e-5)]
    public void AFontSizeFromFiveMicroPoints_isAccepted(double size) =>
        Assert.NotNull(new BandZone("x", new TextStyle { Font = Standard14.Courier, FontSize = size }));

    // The 14 FontBBox rows of the Adobe Core-14 AFM files (public domain), in thousandths of an em,
    // read 2026-11-27, the same source as the widths in Standard14Metrics. The text box of a
    // 10 pt run is (high - low) / 100 points, and a zoned band refuses a height below it.
    [Theory]
    [InlineData(Standard14.Helvetica, -225, 931)]
    [InlineData(Standard14.HelveticaBold, -228, 962)]
    [InlineData(Standard14.HelveticaOblique, -225, 931)]
    [InlineData(Standard14.HelveticaBoldOblique, -228, 962)]
    [InlineData(Standard14.TimesRoman, -218, 898)]
    [InlineData(Standard14.TimesBold, -218, 935)]
    [InlineData(Standard14.TimesItalic, -217, 883)]
    [InlineData(Standard14.TimesBoldItalic, -218, 921)]
    [InlineData(Standard14.Courier, -250, 805)]
    [InlineData(Standard14.CourierBold, -250, 801)]
    [InlineData(Standard14.CourierOblique, -250, 805)]
    [InlineData(Standard14.CourierBoldOblique, -250, 801)]
    [InlineData(Standard14.Symbol, -293, 1010)]
    [InlineData(Standard14.ZapfDingbats, -143, 820)]
    public void TheTextBoxOfEachStandard14Face_isItsAfmFontBBox(Standard14 face, int low, int high)
    {
        var zone = new BandZone("x", new TextStyle { Font = face, FontSize = 10 });
        var box = (high - low) / 100.0;

        Assert.Throws<ArgumentOutOfRangeException>(() => new RunningBand(zone, BandZone.Empty, BandZone.Empty) { Height = box - 0.001 });
        Assert.Equal(box + 0.001, new RunningBand(zone, BandZone.Empty, BandZone.Empty) { Height = box + 0.001 }.EffectiveHeight);
    }

    [Fact]
    public void TheTextBoxOfAnEmbeddedFont_isItsHeadBox()
    {
        // The head table: unitsPerEm at 18, yMin at 38 and yMax at 42 from its start.
        var file = File.ReadAllBytes(DejaVuPath);
        short Short(int at) => (short)((file[at] << 8) | file[at + 1]);
        var tables = (file[4] << 8) | file[5];
        var head = -1;
        for (var t = 0; t < tables; t++)
        {
            var record = 12 + 16 * t;
            if (System.Text.Encoding.ASCII.GetString(file, record, 4) == "head")
                head = (file[record + 8] << 24) | (file[record + 9] << 16) | (file[record + 10] << 8) | file[record + 11];
        }

        Assert.True(head > 0);
        var upem = (ushort)Short(head + 18);
        var box = (Short(head + 42) - Short(head + 38)) * 10.0 / upem;

        using var doc = new Document();
        var zone = new BandZone("x", new TextStyle { FontRef = doc.LoadTrueTypeFont(DejaVuPath), FontSize = 10 });

        Assert.Throws<ArgumentOutOfRangeException>(() => new RunningBand(zone, BandZone.Empty, BandZone.Empty) { Height = box - 0.001 });
        Assert.Equal(box + 0.001, new RunningBand(zone, BandZone.Empty, BandZone.Empty) { Height = box + 0.001 }.EffectiveHeight);
    }
}
