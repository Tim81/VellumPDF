// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Layout.Tests.NoteBlockSupport;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The note block (#703): geometry, splitting, runs, tagging and refusals. Every expected number is
/// worked out from the page and the block's own values, not read back from a file. A 10 pt Courier
/// line is 12 pt tall and each character is 6 pt wide.
/// </summary>
public sealed class NoteBlockTests
{
    private static readonly ColorRgb Tint = new(0.9, 0.95, 1);
    private static readonly ColorRgb Red = new(1, 0, 0);

    // ── Geometry ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Page 300 x 400, margin 20, so the box starts at (20, 20) and is 260 wide. Border 2 and
    /// padding top 10, right 12, bottom 14, left 16: the text starts at x 20 + 2 + 16 = 38 and at
    /// y 20 + 2 + 10 = 32, two lines end at 56, and the bottom border and padding add 16, so the box
    /// is 52 tall and ends at 72. In PDF space the box is y 400 - 72 = 328.
    /// </summary>
    [Fact]
    public void TheBackgroundTheBorderAndTheTextSitAtTheirOperands()
    {
        using var doc = NewDoc(300, 400);
        doc.Add(new NoteBlock
        {
            Background = Tint,
            BorderWidth = 2,
            BorderColor = Red,
            Padding = new EdgeInsets(10, 12, 14, 16),
        }.Add(new Paragraph("line one\nline two", Courier10)));

        var content = Assert.Single(PageContents(Save(doc)));

        // The fill is the whole box, border included, and the four border bars follow in one path:
        // left, right, top, bottom.
        Assert.Equal(
            ["20 328 260 52", "20 328 2 52", "278 328 2 52", "22 378 256 2", "22 328 256 2"],
            Rects(content));
        Assert.Contains("0.9 0.95 1 rg\n20 328 260 52 re\nf\n1 0 0 rg\n20 328 2 52 re\n", content, StringComparison.Ordinal);

        // The text: 38 from the left, the first baseline one font size below the top of its line
        // (400 - 32 - 10), the second one leading lower.
        Assert.Equal(["38 358", "38 346"], TmPositions(content));

        // Painted before the text, so the text is on top.
        Assert.True(content.IndexOf("20 328 260 52 re", StringComparison.Ordinal) < content.IndexOf("BT\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// The wrap width is 260 - 2 - 2 - 16 - 12 = 228, 38 columns of Courier 10. A 38-character word
    /// fits; the word after it goes to the next line. The right padding, not only the left, narrows
    /// the line.
    /// </summary>
    [Fact]
    public void TheTextWrapsInsideTheBorderAndThePadding()
    {
        using var doc = NewDoc(300, 400);
        var word = new string('x', 38);
        doc.Add(new NoteBlock { BorderWidth = 2, Padding = new EdgeInsets(10, 12, 14, 16) }
            .Add(new Paragraph(word + " y", Courier10)));
        var content = Assert.Single(PageContents(Save(doc)));
        Assert.Equal([word, "y"], Shown(content));

        // One column narrower than the word: the 38th character no longer fits.
        using var narrow = NewDoc(300, 400);
        narrow.Add(new NoteBlock { BorderWidth = 2, Padding = new EdgeInsets(10, 13, 14, 16) }
            .Add(new Paragraph(word, Courier10)));
        Assert.Equal([new string('x', 37), "x"], Shown(Assert.Single(PageContents(Save(narrow)))));
    }

    /// <summary>The paragraphs sit one after the other, and the gap of one is spent before the next.</summary>
    [Fact]
    public void TwoParagraphsAreSeparatedByTheFirstOnesSpaceAfter()
    {
        using var doc = NewDoc(300, 400);
        doc.Add(new NoteBlock { Background = Tint, Padding = new EdgeInsets(10) }
            .Add(new Paragraph("a", Courier10) { SpaceAfter = 6 })
            .Add(new Paragraph("b", Courier10) { SpaceAfter = 50 }));

        var content = Assert.Single(PageContents(Save(doc)));

        // Padding 10 on every side: a is at 30..42, the gap 6, b at 48..60, and the last gap is
        // dropped, so the box ends at 60 + 10 = 70 and is 50 tall. The baselines are 10 below the
        // tops of their lines.
        Assert.Equal(["30 360", "30 342"], TmPositions(content));
        Assert.Equal(["20 330 260 50"], Rects(content));
    }

    /// <summary>The gap after the block moves the next element down by its size and not into the box.</summary>
    [Fact]
    public void TheBlocksSpaceAfterLiesOutsideTheBox()
    {
        using var doc = NewDoc(300, 400);
        doc.Add(new NoteBlock { Background = Tint, Padding = new EdgeInsets(10), SpaceAfter = 5 }
            .Add(new Paragraph("in", Courier10)));
        doc.Add(new Paragraph("after", Courier10));

        var content = Assert.Single(PageContents(Save(doc)));

        // The box is 20..52 (10 + 12 + 10), the gap ends at 57, the baseline is 10 below that.
        Assert.Equal(["20 348 260 32"], Rects(content));
        Assert.Equal("20 333", TmPositions(content)[^1]);
    }

    // ── Splitting ────────────────────────────────────────────────────────────

    // Page 300 x 200, margin 20: 160 pt of content, 13 lines of 12 pt, with 4 pt over.
    // Padding top 10, bottom 14, border 2: the top takes 12 and the bottom 16.
    private static NoteBlock Box() => new()
    {
        Background = Tint,
        BorderWidth = 2,
        BorderColor = Red,
        Padding = new EdgeInsets(10, 12, 14, 16),
    };

    private static NoteBlock Frame(int lines) => Box().Add(new Paragraph(Lines(lines), Courier10));

    /// <summary>A block that ends exactly where a page does adds no page.</summary>
    [Fact]
    public void ABlockThatFillsAPageExactlyAddsNoBlankPage()
    {
        // 160 - 12 - 16 = 132 = 11 lines.
        using var doc = NewDoc(300, 200);
        doc.Add(Frame(11));
        var pages = PageContents(Save(doc));
        Assert.Single(pages);

        // 11 lines are 132 tall and the box ends at the foot of the content box, 180, so it is 160
        // tall and starts at PDF y 20; both the top and the bottom bar are there.
        Assert.Equal(["20 20 260 160", "20 20 2 160", "278 20 2 160", "22 178 256 2", "22 20 256 2"], Rects(pages[0]));
    }

    /// <summary>A second block after a split one starts below the last piece, not at the page top.</summary>
    [Fact]
    public void TheElementAfterASplitBlockStartsBelowItsLastPiece()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(Frame(14));
        doc.Add(new Paragraph("next", Courier10));
        var pages = PageContents(Save(doc));
        Assert.Equal(2, pages.Count);

        // Page 1 holds 11 lines (the room is 160 - 12 - 16 = 132). Page 2 holds three, in a whole box:
        // 12 + 36 + 16 = 64, so the box ends at 84 and the next paragraph's baseline is 10 below that.
        Assert.Equal(["L12", "L13", "L14"], Shown(pages[1]).Take(3));
        Assert.Equal("20 106", TmPositions(pages[1])[^1]);
    }

    // ── Runs ─────────────────────────────────────────────────────────────────

    private static readonly TextStyle Bold = new() { Font = Standard14.HelveticaBold, FontSize = 10 };
    private static readonly TextStyle Plain = new() { Font = Standard14.Helvetica, FontSize = 10 };
    private static readonly TextStyle Linked = new() { Font = Standard14.Helvetica, FontSize = 10, LinkUri = "https://example.com/x" };

    private static Paragraph Runs() => new(
    [
        new TextRun("Note: ", Bold),
        new TextRun("read ", Plain),
        new TextRun("this", Linked),
        new TextRun(" on page ", Plain),
        TextRun.PageReference("target", Plain),
        new TextRun(".", Plain),
    ]);

    private static Document RunsDocument(bool inBlock)
    {
        var doc = NewDoc(300, 300);
        if (inBlock)
            doc.Add(new NoteBlock { BorderWidth = 1, Padding = new EdgeInsets(8) }.Add(Runs()));
        else
            doc.Add(Runs());
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("target", Plain) { Anchor = "target" });
        return doc;
    }

    /// <summary>
    /// A bold lead-in run, a link and a page reference work as in a paragraph outside the block:
    /// the same fonts, the same text and the same page number, with the link rectangle moved by
    /// the border and the padding, 9 to the right and 9 down.
    /// </summary>
    [Fact]
    public void ABoldLeadInALinkAndAPageReferenceWorkAsInAParagraphOutsideTheBlock()
    {
        var outside = Save(RunsDocument(inBlock: false));
        var inside = Save(RunsDocument(inBlock: true));

        var outsidePage = PageContents(outside)[0];
        var insidePage = PageContents(inside)[0];

        // Two fonts are selected, the bold one first, and the reference shows page 2 in both.
        Assert.Matches(@"/F1 10 Tf", insidePage);
        Assert.Matches(@"/F2 10 Tf", insidePage);
        Assert.Equal(Shown(outsidePage), Shown(insidePage));
        Assert.Contains("2", Shown(insidePage));
        Assert.Equal("Note: ", Shown(insidePage)[0]);

        // Two links: the one over the word "this" and the reference, which links to its own anchor.
        // Each is where the same link is outside the block, moved by the border and the padding.
        var outsideRects = LinkRects(outside);
        var insideRects = LinkRects(inside);
        Assert.Equal(2, outsideRects.Count);
        Assert.Equal(2, insideRects.Count);
        Assert.Contains("/URI (https://example.com/x)", Encoding.Latin1.GetString(inside), StringComparison.Ordinal);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(outsideRects[i][0] + 9, insideRects[i][0], 3);
            Assert.Equal(outsideRects[i][2] + 9, insideRects[i][2], 3);
            Assert.Equal(outsideRects[i][1] - 9, insideRects[i][1], 3);
            Assert.Equal(outsideRects[i][3] - 9, insideRects[i][3], 3);
        }
    }

    // ── Tagging ──────────────────────────────────────────────────────────────

    private static Document Tagged(PdfConformance claim, bool embedded)
    {
        var doc = NewDoc(300, 200);
        doc.Conformance = claim;
        doc.Tagged = true;
        doc.Language = "en-US";
        doc.Info.Title = "t";
        var style = embedded ? new TextStyle { FontRef = doc.LoadTrueTypeFont(DejaVuPath), FontSize = 10 } : Courier10;
        doc.Add(new NoteBlock { Background = Tint, BorderWidth = 1, Padding = new EdgeInsets(4) }
            .Add(new Paragraph("first", style))
            .Add(new Paragraph("second", style)));
        return doc;
    }

    /// <summary>
    /// The block is a Div in every file: under no claim (written as PDF 2.0) and under each claim
    /// (written as PDF 1.7), tagged. Aside is not written, because a PDF 2.0 type needs its own
    /// structure namespace.
    /// </summary>
    [Theory]
    [InlineData(PdfConformance.None, "%PDF-2.0")]
    [InlineData(PdfConformance.PdfA2b, "%PDF-1.7")]
    [InlineData(PdfConformance.PdfA2u, "%PDF-1.7")]
    [InlineData(PdfConformance.PdfA2a, "%PDF-1.7")]
    [InlineData(PdfConformance.PdfUA1, "%PDF-1.7")]
    public void TheBlockIsADivThatHoldsItsParagraphsUnderEveryClaim(PdfConformance claim, string header)
    {
        var pdf = Save(Tagged(claim, embedded: claim != PdfConformance.None));
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith(header, text, StringComparison.Ordinal);
        Assert.Equal("Document(Div(P,P))", StructTree(pdf));
        Assert.DoesNotContain("/S /Aside", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/S /Table", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/S /Note", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/NS", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The background and the border are an artifact: they carry no marked-content id and no
    /// structure element, so a tagged page holds exactly the two paragraphs' ids.
    /// </summary>
    [Fact]
    public void TheBackgroundAndTheBorderAreAnArtifactOutsideTheStructureTree()
    {
        var pdf = Save(Tagged(PdfConformance.None, embedded: false));
        var content = Assert.Single(PageContents(pdf));

        Assert.StartsWith("/Artifact BMC\nq\n", content, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(content, @"/P <</MCID \d+>> BDC").Count);
        Assert.DoesNotContain("/Div <<", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A block over three pages is one structure element holding the paragraph pieces of every page,
    /// not one element for each page.
    /// </summary>
    [Fact]
    public void ABlockOverThreePagesIsOneStructureElement()
    {
        using var doc = NewDoc(300, 200);
        doc.Tagged = true;
        doc.Language = "en-US";
        doc.Info.Title = "t";
        doc.Add(Frame(30));
        var pdf = Save(doc);

        Assert.Equal(3, PageContents(pdf).Count);
        Assert.Equal("Document(Div(P,P,P))", StructTree(pdf));
    }

    /// <summary>An untagged document writes no structure for the block.</summary>
    [Fact]
    public void AnUntaggedDocumentWritesNoStructureForTheBlock()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(Frame(3));
        var text = Encoding.Latin1.GetString(Save(doc));
        Assert.DoesNotContain("StructTreeRoot", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Artifact", PdfTestUtil.DecompressAllFlatStreams(Encoding.Latin1.GetBytes(text)), StringComparison.Ordinal);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]
    [InlineData(-0.001)]
    public void ABorderWidthThatIsNotFiniteOrIsNegativeIsRefusedWhereItIsSet(double width)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new NoteBlock { BorderWidth = width });
        Assert.Equal("BorderWidth", ex.ParamName);
    }

    [Theory]
    [InlineData("Top")]
    [InlineData("Right")]
    [InlineData("Bottom")]
    [InlineData("Left")]
    public void APaddingEdgeThatIsNotFiniteOrIsNegativeIsRefusedWhereItIsSet(string edge)
    {
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
        {
            var insets = edge switch
            {
                "Top" => new EdgeInsets(bad, 0, 0, 0),
                "Right" => new EdgeInsets(0, bad, 0, 0),
                "Bottom" => new EdgeInsets(0, 0, bad, 0),
                _ => new EdgeInsets(0, 0, 0, bad),
            };
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new NoteBlock { Padding = insets });
            Assert.Equal("Padding." + edge, ex.ParamName);
        }
    }

    [Fact]
    public void ZeroAndNegativeZeroAreAccepted()
    {
        var block = new NoteBlock { BorderWidth = -0.0, Padding = new EdgeInsets(-0.0, 0, 0, 0), SpaceAfter = -0.0 };
        Assert.Equal(0, block.BorderWidth);
    }

    [Fact]
    public void ANullParagraphOrSequenceIsRefusedWhereItIsPassed()
    {
        Assert.Throws<ArgumentNullException>(() => new NoteBlock().Add((Paragraph)null!));
        Assert.Throws<ArgumentNullException>(() => new NoteBlock().Add((string)null!));
        Assert.Throws<ArgumentNullException>(() => new NoteBlock((IEnumerable<Paragraph>)null!));
        var ex = Assert.Throws<ArgumentNullException>(() => new NoteBlock([new Paragraph("a"), null!]));
        Assert.Equal("paragraphs", ex.ParamName);
        Assert.Throws<ArgumentNullException>(() => new Document().Add((NoteBlock)null!));
    }

    /// <summary>
    /// A paragraph that sets a page break, a keep request or a line count is refused when it is
    /// added, with the property named. A list, a table, a heading and a preformatted block cannot
    /// be added: the block has no overload that takes them.
    /// </summary>
    [Theory]
    [InlineData("BreakBefore")]
    [InlineData("KeepWithNext")]
    [InlineData("KeepTogether")]
    [InlineData("MinLinesBeforeBreak")]
    [InlineData("MinLinesAfterBreak")]
    [InlineData("Margins.Top")]
    [InlineData("Margins.Bottom")]
    [InlineData("Margins.Left")]
    [InlineData("Margins.Right")]
    public void AParagraphThatSetsAKeepRequestALineCountOrAMarginIsRefusedWhereItIsAdded(string property)
    {
        var paragraph = property switch
        {
            "BreakBefore" => new Paragraph("a") { BreakBefore = PageBreakMode.Recto },
            "KeepWithNext" => new Paragraph("a") { KeepWithNext = true },
            "KeepTogether" => new Paragraph("a") { KeepTogether = true },
            "MinLinesBeforeBreak" => new Paragraph("a") { MinLinesBeforeBreak = 2 },
            "Margins.Top" => new Paragraph("a") { Margins = new EdgeInsets(7, 0, 0, 0) },
            "Margins.Bottom" => new Paragraph("a") { Margins = new EdgeInsets(0, 0, 7, 0) },
            "Margins.Left" => new Paragraph("a") { Margins = new EdgeInsets(0, 0, 0, 7) },
            "Margins.Right" => new Paragraph("a") { Margins = new EdgeInsets(0, 7, 0, 0) },
            _ => new Paragraph("a") { MinLinesAfterBreak = 3 },
        };

        var one = Assert.Throws<ArgumentException>("paragraph", () => new NoteBlock().Add(paragraph));
        Assert.Contains(property, one.Message, StringComparison.Ordinal);
        var many = Assert.Throws<ArgumentException>("paragraphs", () => new NoteBlock([new Paragraph("ok"), paragraph]));
        Assert.Contains(property, many.Message, StringComparison.Ordinal);

        // The defaults are accepted: a count of 1 asks for nothing.
        var accepted = new NoteBlock().Add(new Paragraph("a") { MinLinesBeforeBreak = 1, MinLinesAfterBreak = 1 });
        Assert.Single(accepted.Paragraphs);
    }

    [Fact]
    public void ThereIsNoOverloadForAnyOtherElement()
    {
        var adders = typeof(NoteBlock).GetMethods().Where(m => m.Name == "Add").Select(m => m.GetParameters()[0].ParameterType).ToHashSet();
        Assert.Equal(new HashSet<Type> { typeof(Paragraph), typeof(string) }, adders);
    }

    // ── Colours ──────────────────────────────────────────────────────────────

    public static TheoryData<double, double, double> BadChannels => new()
    {
        { double.NaN, 0, 0 },
        { 0, double.PositiveInfinity, 0 },
        { 0, 0, double.NegativeInfinity },
        { 1.0000001, 0, 0 },
        { 0, -0.0001, 0 },
        { 2, 0, -1 },
    };

    [Theory]
    [MemberData(nameof(BadChannels))]
    public void ABackgroundOrBorderColourOutsideZeroToOneIsRefusedWhereItIsSet(double r, double g, double b)
    {
        var colour = new ColorRgb(r, g, b);
        var background = Assert.Throws<ArgumentOutOfRangeException>(() => new NoteBlock { Background = colour });
        Assert.Equal("Background", background.ParamName);

        // Refused whether or not the border is drawn.
        var border = Assert.Throws<ArgumentOutOfRangeException>(() => new NoteBlock { BorderColor = colour, BorderWidth = 0 });
        Assert.Equal("BorderColor", border.ParamName);
    }

    [Fact]
    public void TheEdgesOfTheColourRangeAreAccepted()
    {
        var block = new NoteBlock { Background = new ColorRgb(0, 1, 0.5), BorderColor = new ColorRgb(1, 0, 1) };
        Assert.Equal(new ColorRgb(0, 1, 0.5), block.Background);
        Assert.Null(new NoteBlock { Background = null }.Background);
    }

    [Fact]
    public void ABlockWithNoParagraphIsRefusedBeforeAnyLayoutAndTheDocumentStaysSaveable()
    {
        using var doc = NewDoc(300, 200);
        var block = new NoteBlock { Background = Tint };
        doc.Add(new Paragraph("before", Courier10));
        doc.Add(block);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("holds no paragraph", ex.Message, StringComparison.Ordinal);

        // Refused before the layout started, so the same document saves once the block holds one.
        block.Add("now", Courier10);
        Assert.Equal(["before", "now"], Shown(Assert.Single(PageContents(Save(doc)))));
    }

    [Fact]
    public void ABorderAndPaddingThatLeaveNoWidthAreRefusedAsTooTallToFit()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(new NoteBlock { BorderWidth = 2, Padding = new EdgeInsets(0, 130, 0, 130) }.Add("a", Courier10));
        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("too tall", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no width", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATabStopBeyondTheWidthTheBlockLeavesIsRefusedBeforeAnyLayout()
    {
        using var doc = NewDoc(300, 200);
        var paragraph = new Paragraph("a\tb", Courier10) { TabStops = [new TabStop(240)] };
        var block = new NoteBlock { Padding = new EdgeInsets(0, 20, 0, 20) }.Add(paragraph);
        doc.Add(block);

        // 260 - 40 = 220 is the width; a stop at 240 lies beyond it.
        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("tab stop", ex.Message, StringComparison.Ordinal);
    }

    // ── Flow properties ──────────────────────────────────────────────────────

    [Fact]
    public void BreakBeforePageStartsTheBlockOnTheNextPage()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(new Paragraph("first", Courier10));
        doc.Add(new NoteBlock { BreakBefore = PageBreakMode.Page, Background = Tint }.Add("note", Courier10));
        var pages = PageContents(Save(doc));
        Assert.Equal(2, pages.Count);
        Assert.Equal(["first"], Shown(pages[0]));
        Assert.Equal(["note"], Shown(pages[1]));
        Assert.Equal(["20 168 260 12"], Rects(pages[1]));

        Assert.Throws<ArgumentOutOfRangeException>(() => new NoteBlock { BreakBefore = (PageBreakMode)7 });
    }

    // A filler of eight lines (96) leaves 64 pt. The block is 16 + 24 + 16 = 56 tall, so it fits and
    // leaves 8 pt, which holds no line of the paragraph after it.
    private static Document KeepDocument(bool keepWithNext)
    {
        var doc = NewDoc(300, 200);
        doc.Add(new Paragraph(Lines(8), Courier10));
        doc.Add(new NoteBlock { Padding = new EdgeInsets(16), KeepWithNext = keepWithNext, Background = Tint }
            .Add(new Paragraph("n1\nn2", Courier10)));
        doc.Add(new Paragraph("a\nb\nc\nd\ne\nf", Courier10));
        return doc;
    }

    [Fact]
    public void KeepWithNextMovesTheBlockToTheNextPageWithTheElementAfterIt()
    {
        // Without the request the block stays on page 1 and the element after it follows.
        var plain = PageContents(Save(KeepDocument(keepWithNext: false)));
        Assert.Contains("n1", Shown(plain[0]));

        // With it, the block does not stay on a page that cannot hold the first piece of the next.
        var kept = PageContents(Save(KeepDocument(keepWithNext: true)));
        Assert.DoesNotContain("n1", Shown(kept[0]));
        Assert.Contains("n1", Shown(kept[1]));
    }

    [Fact]
    public void KeepTogetherMovesTheBlockWholeAndIsReportedWhenAPageCannotHoldIt()
    {
        // A filler of six lines leaves 88 pt; the block is 24 + 8 * 12 = 120 tall.
        using var doc = NewDoc(300, 200);
        doc.Add(new Paragraph(Lines(6), Courier10));
        doc.Add(new NoteBlock { Padding = new EdgeInsets(12), KeepTogether = true }.Add(new Paragraph(Lines(8, 20), Courier10)));
        var pages = PageContents(Save(doc));
        Assert.Equal(2, pages.Count);
        Assert.Equal(6, Shown(pages[0]).Count);
        Assert.Equal(8, Shown(pages[1]).Count);

        // Taller than a page: it splits, and the save lists the request as relaxed against the block.
        using var tall = NewDoc(300, 200);
        tall.Add(new NoteBlock { KeepTogether = true }.Add(new Paragraph(Lines(20), Courier10)));
        Assert.Equal(2, PageContents(Save(tall)).Count);
        var relaxed = Assert.Single(tall.Diagnostics, d => d.Code == LayoutDiagnosticCode.KeepTogetherRelaxed);
        Assert.Equal(LayoutElementKind.NoteBlock, relaxed.ElementKind);
    }

    [Fact]
    public void TheBlocksAnchorIsTheTopOfItsBoxAndAParagraphsIsItsFirstLine()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(new Paragraph("first", Courier10));
        doc.Add(new PageBreak());
        doc.Add(new NoteBlock { Anchor = "note", BorderWidth = 2, Padding = new EdgeInsets(10) }
            .Add(new Paragraph("a", Courier10) { Anchor = "inner" }));
        var dests = Dests(Save(doc));

        // Page 2, box top at 20: the destination is 200 - 20, above the border and the padding.
        Assert.Equal(new Dest(1, 180), dests["note"]);

        // The paragraph's first line starts below the border and the padding: 20 + 2 + 10.
        Assert.Equal(new Dest(1, 168), dests["inner"]);
    }

    [Fact]
    public void ARepeatedAnchorIsRefusedBeforeAnyLayoutWhateverTheKinds()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(new Paragraph("a", Courier10) { Anchor = "same" });
        doc.Add(new NoteBlock().Add(new Paragraph("b", Courier10) { Anchor = "same" }));
        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("same", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NoteBlock 1", ex.Message, StringComparison.Ordinal);
    }

    // ── What the paragraphs inside keep ──────────────────────────────────────

    /// <summary>
    /// A paragraph's language is written on each piece of it, also when it is split over pages: three
    /// pieces of the first paragraph, one of the second.
    /// </summary>
    [Fact]
    public void AParagraphsLanguageIsWrittenOnEachPieceOfIt()
    {
        using var doc = NewDoc(300, 200);
        doc.Tagged = true;
        doc.Language = "en-US";
        doc.Info.Title = "t";
        doc.Add(Box()
            .Add(new Paragraph(Lines(30), Courier10) { Language = "nl-NL" })
            .Add(new Paragraph("end", Courier10) { Language = "fr-FR" }));
        var pdf = Save(doc);
        var text = Encoding.Latin1.GetString(pdf);

        Assert.Equal(3, PageContents(pdf).Count);
        Assert.Equal("Document(Div(P,P,P,P))", StructTree(pdf));
        Assert.Equal(3, LangCount(text, "nl-NL"));
        Assert.Equal(1, LangCount(text, "fr-FR"));
    }

    // How many structure elements carry /Lang with this value. The value is a text string, which
    // the writer gives as UTF-16BE behind a byte order mark, so the file text holds a NUL before
    // each character when it is read as Latin-1.
    private static int LangCount(string text, string lang)
    {
        var needle = "/Lang (þÿ" + string.Concat(lang.Select(c => " " + c)) + ")";
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// Runs with a null style that yield no text are reported once for the block, counted for every
    /// run, also in a paragraph that splits over pages.
    /// </summary>
    [Fact]
    public void RunsWithANullStyleAreCountedOnceForTheBlockEvenWhenAParagraphSplits()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(Box()
            .Add(new Paragraph([new TextRun("  ", null!), new TextRun("  ", null!), new TextRun(Lines(30), Courier10)]))
            .Add(new Paragraph([new TextRun("  ", null!), new TextRun("x", Courier10)])));
        Assert.True(PageContents(Save(doc)).Count >= 3);

        var item = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.NullTextRunStyle);
        Assert.Equal(LayoutElementKind.NoteBlock, item.ElementKind);
        Assert.Equal(0, item.ElementIndex);
        Assert.Equal(3, item.Count);
    }

    // ── What the block does between lines ────────────────────────────────────

    /// <summary>
    /// Inside the block lines are placed in order and the block splits between any two of them,
    /// whatever the paragraphs are: ten lines leave room for three of the next paragraph's four,
    /// which a paragraph with the default counts also splits 3 and 1.
    /// </summary>
    [Fact]
    public void ABlockSplitsBetweenAnyTwoLines()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(new NoteBlock()
            .Add(new Paragraph(Lines(10), Courier10))
            .Add(new Paragraph(Lines(4, 20), Courier10)));
        var pages = PageContents(Save(doc));
        Assert.Equal([13, 1], pages.Select(p => Shown(p).Count));
        Assert.Equal(["L23"], Shown(pages[1]));
    }

    // ── Splitting: every page holds a whole box ──────────────────────────────

    /// <summary>
    /// 30 lines in Frame (top 12, bottom 16): the room on each page is 160 - 28 = 132, which is 11
    /// lines, so the pages hold 11, 11 and 8. Every page has the fill, the left and right border and
    /// both the top and the bottom border, and the text starts below the top padding on every page.
    /// </summary>
    [Fact]
    public void ABlockOverThreePagesHoldsAWholeBoxOnEachPage()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(Frame(30));
        var pdf = Save(doc);
        var pages = PageContents(pdf);

        Assert.Equal(3, pages.Count);

        // Pages 1 and 2: the box is 12 + 132 + 16 = 160 tall, PDF y 20.
        foreach (var page in pages.Take(2))
        {
            Assert.Equal(["20 20 260 160", "20 20 2 160", "278 20 2 160", "22 178 256 2", "22 20 256 2"], Rects(page));
            Assert.Equal("38 158", TmPositions(page)[0]);
            Assert.Equal(11, Shown(page).Count);
        }

        // Page 3: eight lines, 12 + 96 + 16 = 124 tall, ending at 144, PDF y 56.
        Assert.Equal(["20 56 260 124", "20 56 2 124", "278 56 2 124", "22 178 256 2", "22 56 256 2"], Rects(pages[2]));
        Assert.Equal("38 158", TmPositions(pages[2])[0]);
        Assert.Equal(["L23", "L24", "L25", "L26", "L27", "L28", "L29", "L30"], Shown(pages[2]));
    }

    /// <summary>12 lines in a room of 11: the twelfth is alone on a second page, in a whole box.</summary>
    [Fact]
    public void ALineThatDoesNotFitStartsAnotherWholeBox()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(Frame(12));
        var pages = PageContents(Save(doc));

        Assert.Equal(2, pages.Count);
        Assert.Equal(["L12"], Shown(pages[1]));

        // 12 + 12 + 16 = 40 tall, PDF y 200 - 60.
        Assert.Equal(["20 140 260 40", "20 140 2 40", "278 140 2 40", "22 178 256 2", "22 140 256 2"], Rects(pages[1]));
    }

    /// <summary>
    /// With 30 below the lines, 13 lines of 12 pt leave a room of 130 on each page: 10 lines, then 3,
    /// each page a whole box. With 50 below, 9 and 4.
    /// </summary>
    [Theory]
    [InlineData(30, 10)]
    [InlineData(50, 9)]
    public void ABottomPaddingTakesItsRoomOnEveryPage(double padding, int first)
    {
        using var doc = NewDoc(300, 200);
        doc.Add(new NoteBlock { Padding = new EdgeInsets(0, 0, padding, 0), Background = Tint }
            .Add(new Paragraph(Lines(13), Courier10)));
        var pages = PageContents(Save(doc));

        Assert.Equal(2, pages.Count);
        Assert.Equal(first, Shown(pages[0]).Count);
        Assert.Equal(13 - first, Shown(pages[1]).Count);

        var heights = new[] { first * 12 + padding, (13 - first) * 12 + padding };
        Assert.Equal([$"20 {200 - 20 - heights[0]} 260 {heights[0]}"], Rects(pages[0]));
        Assert.Equal([$"20 {200 - 20 - heights[1]} 260 {heights[1]}"], Rects(pages[1]));
    }

    /// <summary>
    /// Lines of 30 pt and 12 pt in a room of 50 (padding 55 above and below): the 30 pt line and one
    /// 12 pt line fill the first page, and the second 12 pt line has a page of its own.
    /// </summary>
    [Fact]
    public void LinesOfDifferentHeightsFillTheRoomEachAtItsOwnParagraphsHeight()
    {
        var tall = new TextStyle { Font = VellumPdf.Fonts.Standard14.Courier, FontSize = 25, Leading = 30 };
        using var doc = NewDoc(300, 200);
        doc.Add(new NoteBlock { Padding = new EdgeInsets(55, 0, 55, 0), Background = Tint }
            .Add("B01", tall)
            .Add(new Paragraph("L02\nL03", Courier10)));
        var pages = PageContents(Save(doc));

        Assert.Equal(["B01", "L02"], Shown(pages[0]));
        Assert.Equal(["L03"], Shown(pages[1]));
        Assert.Equal(["20 28 260 152"], Rects(pages[0]));
        Assert.Equal(["20 58 260 122"], Rects(pages[1]));
    }

    /// <summary>
    /// A block is refused as too tall only where an empty page cannot hold the two insets and one
    /// line of the tallest line the block holds. The room is 160 - 2 x padding: 30 pt lines need
    /// padding 65 at most, and the 12 pt lines of the same block do not matter.
    /// </summary>
    [Theory]
    [InlineData(65, true)]
    [InlineData(66, false)]
    [InlineData(80, false)]
    public void ABlockIsRefusedOnlyWhereTheRoomHoldsNotEvenItsTallestLine(double padding, bool saved)
    {
        var tall = new TextStyle { Font = VellumPdf.Fonts.Standard14.Courier, FontSize = 25, Leading = 30 };
        using var doc = NewDoc(300, 200);
        doc.Add(new NoteBlock { Padding = new EdgeInsets(padding, 0, padding, 0) }
            .Add("L01", Courier10)
            .Add("B02", tall));
        if (saved)
        {
            Assert.NotEmpty(PageContents(Save(doc)));
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("too tall", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blank paragraph is a blank line, as at the top level: it takes a line of the room and draws
    /// nothing, and a page that ends after it carries a whole box.
    /// </summary>
    [Fact]
    public void ABlankLineIsALineAcrossAPageBreak()
    {
        // Room 24: two lines a page.
        using var doc = NewDoc(300, 200);
        doc.Add(new NoteBlock { Padding = new EdgeInsets(68, 0, 68, 0), Background = Tint }
            .Add(new Paragraph("L01\n\nL02", Courier10)));
        var pages = PageContents(Save(doc));

        Assert.Equal(2, pages.Count);
        Assert.Equal(["L01"], Shown(pages[0]));
        Assert.Equal(["L02"], Shown(pages[1]));
        Assert.Equal(["20 20 260 160"], Rects(pages[0]));
        Assert.Equal(["20 32 260 148"], Rects(pages[1]));

        // A paragraph with no text at all is a blank line too.
        using var blank = NewDoc(300, 200);
        blank.Add(new NoteBlock().Add("L01", Courier10).Add("", Courier10).Add("L03", Courier10));
        var blankPage = Assert.Single(PageContents(Save(blank)));
        Assert.Equal(["L01", "L03"], Shown(blankPage));
        Assert.Equal(["20 170", "20 143.6"], TmPositions(blankPage));
    }

    // ── Rounding, and the fuzz ──────────────────────────────────────────────

    private static (int Pages, string Verdict) Place(double pageHeight, NoteBlock block, int leadLines = 0)
    {
        using var doc = NewDoc(300, pageHeight);
        if (leadLines > 0)
            doc.Add(new Paragraph(Lines(leadLines, 900), Courier10));
        doc.Add(block);
        try
        {
            return (PageContents(Save(doc)).Count, "saved");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("too tall", StringComparison.Ordinal))
        {
            return (0, "refused");
        }
    }

    /// <summary>
    /// A block fits wherever a paragraph with the same top and bottom margins fits, over fractional
    /// borders and paddings whose sum makes the box exactly the content height.
    /// </summary>
    [Fact]
    public void AnExactFitIsNotLostToRounding()
    {
        var rng = new Random(721);
        var fitted = 0;
        for (var n = 0; n < 3000; n++)
        {
            var border = Math.Round(rng.NextDouble() * 5, 2);
            var top = Math.Round(rng.NextDouble() * 8, 2);
            var bottom = Math.Round(rng.NextDouble() * 8, 2);
            var lines = rng.Next(1, 6);
            var height = 40 + 12.0 * lines + 2 * border + top + bottom;

            using var asParagraph = NewDoc(300, height);
            asParagraph.Add(new Paragraph(Lines(lines), Courier10) { Margins = new EdgeInsets(border + top, 0, border + bottom, 0) });
            try
            {
                if (PageContents(Save(asParagraph)).Count != 1) continue;
            }
            catch (InvalidOperationException)
            {
                // The paragraph itself is refused at this geometry.
                continue;
            }

            using var asBlock = NewDoc(300, height);
            asBlock.Add(new NoteBlock { BorderWidth = border, Padding = new EdgeInsets(top, 0, bottom, 0) }.Add(Lines(lines), Courier10));
            Assert.True(PageContents(Save(asBlock)).Count == 1, $"border {border}, top {top}, bottom {bottom}, {lines} lines, page {height}");
            fitted++;
        }

        Assert.True(fitted > 1000, $"only {fitted} geometries fit as a paragraph");
    }

    /// <summary>
    /// Random geometry, paragraphs, blank lines, gaps, page positions, footers and page references.
    /// Whatever is saved draws each line once and in order, holds no page without text, gives every
    /// page the block reaches a whole box at exact coordinates (the fill, the two sides, the top and
    /// the bottom bar), prints the right page for a reference to what follows the block and the
    /// right footer on every page; and a block is refused exactly where the room an empty page leaves
    /// after both insets holds none of the tallest line.
    /// </summary>
    [Fact]
    public void FuzzedBlocksLoseNoLineHoldWholeBoxesAndAreRefusedOnlyWhereTheTypeSays()
    {
        var rng = new Random(20261127);
        var saved = 0;
        var refused = 0;
        var references = 0;
        for (var n = 0; n < 2000; n++)
        {
            var page = 60 + rng.Next(0, 18) * 12 + rng.Next(0, 12);
            var border = new[] { 0.0, 0.5, 1.0, 2.0 }[rng.Next(4)];
            var top = new[] { 0, 5, 20, 40, 60, 80, 100 }[rng.Next(7)] + rng.Next(0, 12) + (rng.Next(3) == 0 ? 0.5 : 0);
            var bottom = new[] { 0, 5, 20, 40, 60, 80, 100 }[rng.Next(7)] + rng.Next(0, 12) + (rng.Next(3) == 0 ? 0.25 : 0);
            var lead = rng.Next(0, 6);
            var withReference = rng.Next(3) == 0;
            var withFooter = rng.Next(4) == 0;
            var counts = Enumerable.Range(0, rng.Next(1, 5)).Select(_ => rng.Next(1, 14)).ToArray();
            var heights = counts.Select(_ => new[] { 6.0, 12.0, 24.0 }[rng.Next(3)]).ToArray();

            var block = new NoteBlock { BorderWidth = border, Background = Tint, Padding = new EdgeInsets(top, 4, bottom, 4) };
            var next = 1;
            var tokens = new List<string>();
            for (var k = 0; k < counts.Length; k++)
            {
                var style = new TextStyle { Font = Standard14.Courier, FontSize = heights[k] / 1.2, Leading = heights[k] };
                var text = new System.Text.StringBuilder();
                for (var j = 0; j < counts[k]; j++)
                {
                    if (j > 0) text.Append(rng.Next(6) == 0 ? "\n\n" : "\n");
                    var token = "L" + (next + j).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                    tokens.Add(token);
                    text.Append(token);
                }

                block.Add(new Paragraph(text.ToString(), style) { SpaceAfter = rng.Next(3) == 0 ? rng.Next(0, 9) : 0 });
                next += counts[k];
            }

            if (withReference)
            {
                var small = new TextStyle { Font = Standard14.Courier, FontSize = 5, Leading = 6 };
                block.Add(new Paragraph([new TextRun("see ", small), TextRun.PageReference("tgt", small)]));
                heights = [.. heights, 6.0];
            }

            var footer = withFooter ? new RunningBand("{page} of {pages}") : null;
            var contentHeight = page - 40.0 - (footer?.EffectiveHeight ?? 0);
            var room = contentHeight - (border + top) - (border + bottom);
            var expectSaved = room >= heights.Max();

            using var doc = NewDoc(300, page);
            if (footer is not null) doc.Footer = footer;
            if (lead > 0) doc.Add(new Paragraph(Lines(lead, 900), Courier10));
            doc.Add(block);
            if (withReference) doc.Add(new Paragraph("tgt", Courier10) { Anchor = "tgt" });
            byte[] pdf;
            try
            {
                pdf = Save(doc);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("too tall", StringComparison.Ordinal))
            {
                Assert.False(expectSaved, $"case {n}: refused where the type says a page can hold it (page {page}, border {border}, top {top}, bottom {bottom}, lines {string.Join('+', counts)}): {ex.Message}");
                refused++;
                continue;
            }

            Assert.True(expectSaved, $"case {n}: saved where the type says it is refused");
            saved++;
            var pages = PageContents(pdf);
            var all = pages.Select(Shown).ToList();
            var shown = all.SelectMany(x => x).Where(t => t.Length == 3 && t[0] == 'L' && int.Parse(t[1..], System.Globalization.CultureInfo.InvariantCulture) < 900).ToList();
            Assert.True(tokens.SequenceEqual(shown), $"case {n}: lines lost, repeated or out of order");
            Assert.All(pages, c => Assert.True(Shown(c).Count > 0 || c.Contains("0.9 0.95 1 rg", StringComparison.Ordinal), $"case {n}: a page with neither text nor a box"));

            // Each page that holds a piece has the fill, the two sides and the top and bottom bar.
            var inner = (260 - 2 * border).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var content in pages.Where(c => c.Contains("0.9 0.95 1 rg", StringComparison.Ordinal)))
            {
                var rects = Rects(content);
                Assert.Equal("20", rects[0].Split(' ')[0]);
                Assert.Equal("260", rects[0].Split(' ')[2]);
                if (border > 0)
                {
                    Assert.Equal(5, rects.Count);
                    var box = rects[0].Split(' ');
                    var y = double.Parse(box[1], System.Globalization.CultureInfo.InvariantCulture);
                    var h = double.Parse(box[3], System.Globalization.CultureInfo.InvariantCulture);
                    Assert.Equal(2, rects.Count(r => r.Split(' ')[2] == inner));
                    static double Y(string r) => double.Parse(r.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
                    Assert.Contains(rects, r => r.Split(' ')[2] == inner && Math.Abs(Y(r) - y) < 1e-4);
                    Assert.Contains(rects, r => r.Split(' ')[2] == inner && Math.Abs(Y(r) - (y + h - border)) < 1e-4);
                }
            }

            if (withFooter)
                for (var i = 0; i < pages.Count; i++)
                    Assert.Contains($"{i + 1} of {pages.Count}", all[i]);

            if (withReference)
            {
                var dest = Dests(pdf)["tgt"];
                var label = (dest.Page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(1, all.SelectMany(x => x).Count(t => t == label));
                references++;
            }
        }

        Assert.True(saved > 600 && refused > 100 && references > 100, $"saved {saved}, refused {refused}, references {references}");
    }
}
