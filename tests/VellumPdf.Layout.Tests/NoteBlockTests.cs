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
    private static NoteBlock Frame(int lines) => new NoteBlock
    {
        Background = Tint,
        BorderWidth = 2,
        BorderColor = Red,
        Padding = new EdgeInsets(10, 12, 14, 16),
    }.Add(new Paragraph(Lines(lines), Courier10));

    /// <summary>
    /// 30 lines. Page 1 holds (160 - 12) / 12 = 12 of them, page 2 holds 13, page 3 holds 5 and the
    /// bottom border and padding. The fill and the left and right border run on every page, the top
    /// border and padding are on page 1 only, and the bottom ones on page 3 only.
    /// </summary>
    [Fact]
    public void ABlockOverThreePagesKeepsItsFillAndSidesAndHasOneTopAndOneBottom()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(Frame(30));
        var pdf = Save(doc);
        var pages = PageContents(pdf);

        Assert.Equal(3, pages.Count);

        // Page 1: the box is 20..176, 156 tall, so PDF y 24. The top bar is there, the bottom is not.
        Assert.Equal(["20 24 260 156", "20 24 2 156", "278 24 2 156", "22 178 256 2"], Rects(pages[0]));
        Assert.Equal("38 158", TmPositions(pages[0])[0]);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => "L" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)), Shown(pages[0]));

        // Page 2: no top padding, so the text starts at the top of the content box, and the sides
        // continue. There is neither a top nor a bottom bar.
        Assert.Equal(["20 24 260 156", "20 24 2 156", "278 24 2 156"], Rects(pages[1]));
        Assert.Equal("38 170", TmPositions(pages[1])[0]);
        Assert.Equal(13, Shown(pages[1]).Count);

        // Page 3: five lines (60) and then the bottom padding and border (16): the box is 76 tall
        // and ends at 96, PDF y 104. The bottom bar is at y 94 in layout space.
        Assert.Equal(["20 104 260 76", "20 104 2 76", "278 104 2 76", "22 104 256 2"], Rects(pages[2]));
        Assert.Equal("38 170", TmPositions(pages[2])[0]);
        Assert.Equal(["L26", "L27", "L28", "L29", "L30"], Shown(pages[2]));
    }

    /// <summary>
    /// 38 lines: page 3 could hold the last 13 without the bottom padding, but not with it. The last
    /// line moves to a fourth page with the padding, so no page holds padding alone.
    /// </summary>
    [Fact]
    public void TheClosingLineMovesWithTheBottomPaddingInsteadOfLeavingItAlone()
    {
        using var doc = NewDoc(300, 200);
        doc.Add(Frame(38));
        var pages = PageContents(Save(doc));

        Assert.Equal(4, pages.Count);
        Assert.Equal(12, Shown(pages[2]).Count);
        Assert.Equal(["L38"], Shown(pages[3]));

        // Page 3 is not the last page, so it has the sides and nothing else: 12 lines, 144 tall,
        // ending at 164, PDF y 36.
        Assert.Equal(["20 36 260 144", "20 36 2 144", "278 36 2 144"], Rects(pages[2]));

        // Page 4: one line and the bottom, 12 + 16 = 28 tall, PDF y 200 - 48 = 152.
        Assert.Equal(["20 152 260 28", "20 152 2 28", "278 152 2 28", "22 152 256 2"], Rects(pages[3]));
    }

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

        // Page 2 holds two lines (L13, L14) and the bottom: 24 + 16 = 40, so the box ends at 60 and
        // the next paragraph's first baseline is 60 + 10 below the top.
        Assert.Equal(["L13", "L14"], Shown(pages[1]).Take(2));
        Assert.Equal("20 130", TmPositions(pages[1])[^1]);
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

    [Fact]
    public void InAPdf2FileTheBlockIsAnAsideThatHoldsItsParagraphs()
    {
        var pdf = Save(Tagged(PdfConformance.None, embedded: false));
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-2.0", text, StringComparison.Ordinal);
        Assert.Equal("Document(Aside(P,P))", StructTree(pdf));
        Assert.DoesNotContain("/S /Table", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/S /Note", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2a)]
    [InlineData(PdfConformance.PdfUA1)]
    public void UnderAPdf17ClaimTheBlockIsADivThatHoldsItsParagraphs(PdfConformance claim)
    {
        var pdf = Save(Tagged(claim, embedded: true));
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.7", text, StringComparison.Ordinal);
        Assert.Equal("Document(Div(P,P))", StructTree(pdf));
        Assert.DoesNotContain("/S /Aside", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/S /Table", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/S /Note", text, StringComparison.Ordinal);
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
        Assert.DoesNotContain("/Aside <<", content, StringComparison.Ordinal);
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
        Assert.Equal("Document(Aside(P,P,P))", StructTree(pdf));
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
    /// A paragraph that asks for a page break is refused when it is added. A list, a table, a
    /// heading and a preformatted block cannot be added: the block has no overload that takes them.
    /// </summary>
    [Fact]
    public void AnUnsupportedChildIsRefusedWhereItIsAdded()
    {
        foreach (var mode in new[] { PageBreakMode.Page, PageBreakMode.Recto })
        {
            var paragraph = new Paragraph("a") { BreakBefore = mode };
            Assert.Throws<ArgumentException>("paragraph", () => new NoteBlock().Add(paragraph));
            Assert.Throws<ArgumentException>("paragraphs", () => new NoteBlock([paragraph]));
        }

        var adders = typeof(NoteBlock).GetMethods().Where(m => m.Name == "Add").Select(m => m.GetParameters()[0].ParameterType).ToHashSet();
        Assert.Equal(new HashSet<Type> { typeof(Paragraph), typeof(string) }, adders);
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

    // ── Rules of the paragraphs inside ───────────────────────────────────────

    // Page 300 x 200, margin 20: 13 lines and 4 pt over. A block with no border and no padding.
    [Fact]
    public void AParagraphThatKeepsTogetherMovesWholeWhenItFitsOnTheNextPage()
    {
        NoteBlock Build(bool keep) => new NoteBlock()
            .Add(new Paragraph(Lines(10), Courier10))
            .Add(new Paragraph(Lines(4, 20), Courier10) { KeepTogether = keep });

        using var split = NewDoc(300, 200);
        split.Add(Build(keep: false));
        var splitPages = PageContents(Save(split));
        Assert.Equal([13, 1], splitPages.Select(p => Shown(p).Count));

        using var whole = NewDoc(300, 200);
        whole.Add(Build(keep: true));
        var wholePages = PageContents(Save(whole));
        Assert.Equal([10, 4], wholePages.Select(p => Shown(p).Count));
    }

    [Fact]
    public void AParagraphThatKeepsWithNextMovesWithTheOneAfterIt()
    {
        // 11 lines leave 28 pt. The next paragraph asks for two lines before a break, and one fits.
        NoteBlock Build(bool keep) => new NoteBlock()
            .Add(new Paragraph(Lines(11), Courier10))
            .Add(new Paragraph("kept", Courier10) { KeepWithNext = keep })
            .Add(new Paragraph(Lines(3, 30), Courier10) { MinLinesBeforeBreak = 2 });

        using var apart = NewDoc(300, 200);
        apart.Add(Build(keep: false));
        var apartPages = PageContents(Save(apart));
        Assert.Contains("kept", Shown(apartPages[0]));
        Assert.DoesNotContain("L30", Shown(apartPages[0]));

        using var together = NewDoc(300, 200);
        together.Add(Build(keep: true));
        var togetherPages = PageContents(Save(together));
        Assert.DoesNotContain("kept", Shown(togetherPages[0]));
        Assert.Equal(["kept", "L30", "L31", "L32"], Shown(togetherPages[1]));
    }

    [Fact]
    public void LineCountsHoldInsideTheBlock()
    {
        // 10 lines leave 40 pt: three lines fit. Two must carry over, so two stay.
        using var doc = NewDoc(300, 200);
        doc.Add(new NoteBlock()
            .Add(new Paragraph(Lines(10), Courier10))
            .Add(new Paragraph(Lines(4, 20), Courier10) { MinLinesAfterBreak = 2 }));
        var pages = PageContents(Save(doc));
        Assert.Equal([12, 2], pages.Select(p => Shown(p).Count));
    }
}
