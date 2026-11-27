// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.LinkSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <see cref="TextRun.PageReference"/>: the label of the page an anchor lands on, resolved by the
/// pagination loop. The labels printed are read back from the content stream, and the expected ones
/// are worked out from the page the anchored element is on, never from the table that holds them.
/// </summary>
public sealed class PageReferenceTests
{
    private static readonly TextStyle S = Style();

    // ── Documents whose target is on a known page ────────────────────────────

    // One paragraph on page 1 that holds the reference, then a page of filler per page up to the
    // target's, which starts the page it is on.
    private static Document ForwardTo(int targetPage, string? label = null)
    {
        var doc = ShortPage();
        doc.Add(new Paragraph([new TextRun("See page ", S), label is null ? TextRun.PageReference("target", S) : new TextRun(label, Style())]));
        for (var i = 2; i < targetPage; i++)
        {
            doc.Add(new PageBreak());
            doc.Add(new Paragraph("filler " + i));
        }

        doc.Add(new PageBreak());
        doc.Add(new Paragraph("target") { Anchor = "target" });
        return doc;
    }

    [Fact]
    public void AForwardReferenceToPage12_prints12_inThreeMeasuringRuns_andLinksToTheAnchor()
    {
        using var doc = ForwardTo(12);
        var renderer = doc.CreateRenderer();
        renderer.RunLayout();
        Assert.Equal(3, renderer.MeasureRuns);
        Assert.Equal(["1", "12", "12"], renderer.ReferenceTable!.States.Select(s => Assert.Single(s)).ToArray());

        using var saved = ForwardTo(12);
        var pdf = Save(saved);
        var shown = Shown(pdf);
        Assert.Contains("12", shown);
        Assert.DoesNotContain("1", shown.Where(t => t == "1"));
        var link = Assert.Single(Links(pdf));
        Assert.Equal("target", link.Dest);
        Assert.Null(link.Uri);
    }

    [Fact]
    public void TheLiteralDocument_printsTheSameLabelAndTheSameGeometry()
    {
        using var reference = ForwardTo(12);
        using var literal = ForwardTo(12, label: "12");

        // The one difference is the link the reference is. The literal label is a run of its own
        // style instance, as the reference's label is, so both draw it in a show operator of its
        // own.
        var a = ContentStreamReadback.TextPlacements(Content(Save(reference)));
        var b = ContentStreamReadback.TextPlacements(Content(Save(literal)));
        Assert.Equal(b, a);
    }

    [Fact]
    public void ABackwardReference_printsTheEarlierPage_inTwoMeasuringRuns()
    {
        var doc = ShortPage();
        doc.Add(new Paragraph("target") { Anchor = "target" });
        doc.Add(new PageBreak());
        doc.Add(new PageBreak());
        doc.Add(new Paragraph([new TextRun("Back to page ", S), TextRun.PageReference("target", S)]));

        var renderer = doc.CreateRenderer();
        renderer.RunLayout();
        Assert.Equal(2, renderer.MeasureRuns);

        using var saved = ShortPage();
        saved.Add(new Paragraph("target") { Anchor = "target" });
        saved.Add(new PageBreak());
        saved.Add(new PageBreak());
        saved.Add(new Paragraph([new TextRun("Back to page ", S), TextRun.PageReference("target", S)]));
        Assert.Contains("1", Shown(Save(saved)));
        doc.Dispose();
    }

    [Fact]
    public void ARomanRange_printsTheRomanLabel_asThePageNumberTokenDoes()
    {
        using var doc = ForwardTo(4);
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman);
        doc.SetFooter("{page}");

        var shown = Shown(Save(doc));

        // Page 4's footer prints iv, and so does the reference to the paragraph that starts there.
        Assert.Equal(2, shown.Count(t => t == "iv"));
    }

    [Fact]
    public void AReference_toTheLabelOfAKernelDocumentThatAlreadyHoldsTwoPages_printsWhatThePageTokenPrints()
    {
        var pdf = new PdfDocument();
        pdf.AddPage(new PdfRectangle(0, 0, 300, 400));
        pdf.AddPage(new PdfRectangle(0, 0, 300, 400));
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 400), new EdgeInsets(10))
        {
            Footer = new RunningBand("{page}"),
        };
        renderer.Add(new ParagraphRenderer(new Paragraph([new TextRun("first ", S), TextRun.PageReference("a", S), new TextRun(" second ", S), TextRun.PageReference("b", S)])));
        renderer.Add(new ParagraphRenderer(new Paragraph("on the first layout page") { Anchor = "a" }));
        renderer.Add(new PageBreakRenderer(new PageBreak()));
        renderer.Add(new ParagraphRenderer(new Paragraph("on the second layout page") { Anchor = "b" }));

        using var ms = new MemoryStream();
        renderer.Render(ms);
        var shown = Shown(ms.ToArray());

        // The pages the document held are pages 1 and 2, so the layout starts on page 3, as {page}
        // counts it, and the footers print 3 and 4.
        Assert.Equal(
            ["first", "3", "second", "4", "on the first layout page", "3", "on the second layout page", "4"],
            shown.Select(t => t.Trim()).ToArray());
    }

    // ── A label that changes the layout ──────────────────────────────────────

    // Ten 12pt lines make a page. The paragraph that holds the reference is one line while its
    // label is as narrow as the placeholder and two lines when it is wider, and the target is the
    // 100th line when it is one line, which is the last of page 10, and the 101st when it is two,
    // which is the first of page 11. The page of the target is the label of the reference, so the
    // label 10 puts the target on page 11 and the label 11 keeps it there.
    private static Document Growing(string? literal = null, PageLabelRange? range = null, int lines = 100)
    {
        var small = new TextStyle { FontRef = Helv, FontSize = 10 };
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 42, 140), Margins = new EdgeInsets(10), PageLabel = range };
        doc.Add(new Paragraph([new TextRun("ab ", small), literal is null ? TextRun.PageReference("t", small) : new TextRun(literal, new TextStyle { FontRef = Helv, FontSize = 10 })]));
        for (var i = 2; i < lines; i++) doc.Add(new Paragraph("x", small));
        doc.Add(new Paragraph("t", small) { Anchor = "t" });
        return doc;
    }

    [Fact]
    public void ALabelThatGainsADigit_settlesInFourMeasuringRuns()
    {
        using var doc = Growing();
        var renderer = doc.CreateRenderer();
        renderer.RunLayout();

        Assert.Equal(4, renderer.MeasureRuns);
        Assert.Equal(["1", "10", "11", "11"], renderer.ReferenceTable!.States.Select(s => Assert.Single(s)).ToArray());
    }

    [Fact]
    public void ALabelThatGainsADigit_leavesTheLayoutOfTheDocumentThatHoldsTheSettledLabel()
    {
        using var reference = Growing();
        using var literal = Growing(literal: "11");

        var a = ContentStreamReadback.TextPlacements(Content(Save(reference)));
        var b = ContentStreamReadback.TextPlacements(Content(Save(literal)));

        // The wrap of the first paragraph is two lines, and everything after it is where the
        // literal document puts it.
        Assert.Equal(b, a);
        Assert.Equal(2, a.Count(p => p.Text is "ab" or "11"));
    }

    [Fact]
    public void ALabelThatOscillates_isRefusedAtTheRepeat_naming_theAnchorAndTheHistory()
    {
        // viii is wide and ix narrow. The target is the 80th line when the label is narrow, on page
        // 8, and the 81st when it is wide, on page 9.
        var small = new TextStyle { FontRef = Helv, FontSize = 10 };
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 37, 140),
            Margins = new EdgeInsets(10),
            PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman),
        };
        doc.Add(new Paragraph([new TextRun("a ", small), TextRun.PageReference("t", small)]));
        for (var i = 2; i < 80; i++) doc.Add(new Paragraph("x", small));
        doc.Add(new Paragraph("t", small) { Anchor = "t" });

        var renderer = doc.CreateRenderer();
        var ex = Assert.Throws<InvalidOperationException>(renderer.RunLayout);

        // Run 4's resolve returns to run 2's label, so the loop stops after three measuring runs.
        Assert.Equal(3, renderer.MeasureRuns);
        Assert.Contains("'1', 'viii', 'ix', 'viii'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'t'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Paragraph 0", ex.Message, StringComparison.Ordinal);

        // A fresh document lays out the same way, so the message does not advise one.
        Assert.DoesNotContain("new document", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be saved again", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatesOfTwoReferences_areComparedAsAVector_neverJoined()
    {
        string[][] script = [["1", "12"], ["3", "4"], ["11", "2"], ["11", "2"]];

        // A comparison of the joined strings would see run 3 as run 1, "112" and "112", and refuse
        // a document that settles at run 4.
        Assert.Equal(string.Concat(script[0]), string.Concat(script[2]));
        Assert.Equal(-1, PageReferenceTable.RepeatedRun(script));
        Assert.Equal(-1, PageReferenceTable.RepeatedRun(script[..3]));

        // A real repeat two runs back is found, and the run before the last is not one.
        Assert.Equal(1, PageReferenceTable.RepeatedRun([["a", "b"], ["c", "d"], ["e", "f"], ["c", "d"]]));
        Assert.Equal(-1, PageReferenceTable.RepeatedRun([["a"], ["b"], ["b"]]));
    }

    // ── What is wrapped again ────────────────────────────────────────────────

    [Fact]
    public void AParagraph_isWrappedAgainOnlyWhereAReadLabelChanged()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 400), new EdgeInsets(10));
        var holder = new ParagraphRenderer(new Paragraph([new TextRun("See ", S), TextRun.PageReference("t", S)]));
        var plain = new ParagraphRenderer(new Paragraph("plain text"));
        renderer.Add(holder);
        renderer.Add(plain);
        for (var i = 2; i < 12; i++)
        {
            renderer.Add(new PageBreakRenderer(new PageBreak()));
            renderer.Add(new ParagraphRenderer(new Paragraph("f" + i)));
        }

        renderer.Add(new PageBreakRenderer(new PageBreak()));
        renderer.Add(new ParagraphRenderer(new Paragraph("t") { Anchor = "t" }));
        renderer.RunLayout();

        // The labels read "1", then "12" for the rest of the measuring runs and the drawing pass.
        Assert.Equal(3, renderer.MeasureRuns);
        Assert.Equal(2, holder.WrapCalls);
        Assert.Equal(1, plain.WrapCalls);
    }

    [Fact]
    public void ATextRunThatIsNotAReference_isNotReplaced_andAForwardAnchorCostsNoExtraAllocationPerRun()
    {
        IReadOnlyList<TextRun> runs = [new TextRun("a", S), new TextRun("b", S)];
        ReferenceReads? reads = null;

        Assert.Same(runs, PageReferences.Effective(runs, ref reads));
        Assert.Null(reads);
    }

    [Fact]
    public void AReference_inAHeading_aListItemAndARunCell_printsTheLabelOfItsAnchor()
    {
        var doc = ShortPage();
        doc.Add(new Heading([new TextRun("Heading ", S), TextRun.PageReference("t", S)]));
        doc.Add(new ListElement().Add(new ListItem([new TextRun("item ", S), TextRun.PageReference("t", S)])));
        var table = new VellumPdf.Layout.Elements.Table.TableElement { DefaultCellStyle = S }.SetColumnWidths(100, 100);
        table.AddRow().AddCell(new VellumPdf.Layout.Elements.Table.Cell([new TextRun("cell ", S), TextRun.PageReference("t", S)])).AddCell("x");
        doc.Add(table);
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("page two"));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("target") { Anchor = "t" });

        var pdf = Save(doc);
        var shown = Shown(pdf);

        // Page 3 is where the target starts, and the three references print it.
        Assert.Equal(3, shown.Count(t => t == "3"));
        Assert.Equal(3, Links(pdf).Count(l => l.Dest == "t"));
    }

    [Fact]
    public void TheLabelIsInNoParagraphTextOrHeadingTextOrOutlineTitle()
    {
        var heading = new Heading([new TextRun("Heading ", S), TextRun.PageReference("t", S)]);
        var paragraph = new Paragraph([new TextRun("See ", S), TextRun.PageReference("t", S)]);

        Assert.Equal("Heading ", heading.Text);
        Assert.Equal("See ", paragraph.Text);
        Assert.Equal("Heading ", heading.ResolvedBookmarkTitle);
    }

    [Fact]
    public void ARunAdjacentToAnotherOfTheSameReference_isOneLink_asTheSameTextInOneStyleIs()
    {
        var reference = TextRun.PageReference("t", S);
        byte[] Build(params TextRun[] runs)
        {
            var doc = ShortPage();
            doc.Add(new Paragraph(runs));
            doc.Add(new PageBreak());
            doc.Add(new Paragraph("target") { Anchor = "t" });
            return Save(doc);
        }

        Assert.Single(Links(Build(reference, reference)));
        Assert.Single(Links(Build(reference, new TextRun(" ", S), reference)));
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void ANameNoElementHolds_isRefusedBeforeAnyLayout_andTheDocumentCanStillBeSaved()
    {
        var doc = ShortPage();
        var paragraph = new Paragraph([new TextRun("See ", S), TextRun.PageReference("missing", S)]);
        doc.Add(paragraph);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("\"missing\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Paragraph 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("can be saved", ex.Message, StringComparison.Ordinal);

        // Nothing was laid out, so giving an element the name fixes the same document.
        doc.Add(new Paragraph("here") { Anchor = "missing" });
        Assert.Contains("1", Shown(Save(doc)));
    }

    [Fact]
    public void AReference_orALinkAnchor_isRefusedUnderPdfUa1_naming_theElement_butSavesUnderPdfA2aAndA2u()
    {
        foreach (var conformance in new[] { PdfConformance.PdfUA1, PdfConformance.PdfA2a, PdfConformance.PdfA2u })
        {
            var doc = ShortPage();
            doc.Conformance = conformance;
            doc.Tagged = true;
            doc.Language = "en-US";
            doc.Info.Title = "t";
            doc.Add(new Paragraph([new TextRun("See ", S), TextRun.PageReference("t", S)]));
            doc.Add(new Paragraph("here", Style(anchor: "t")));
            doc.Add(new Paragraph("target") { Anchor = "t" });

            if (conformance == PdfConformance.PdfUA1)
            {
                var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
                Assert.Contains("Paragraph 0", ex.Message, StringComparison.Ordinal);
                Assert.Contains("PDF/UA-1", ex.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.NotEmpty(Save(doc));
            }
        }
    }

    [Fact]
    public void AReferenceWhoseStyleSetsAnotherLink_isRefused()
    {
        foreach (var style in new[] { Style("https://e.example/"), Style(anchor: "other") })
        {
            var doc = ShortPage();
            doc.Add(new Paragraph([TextRun.PageReference("t", style)]));
            doc.Add(new Paragraph("target") { Anchor = "t" });
            var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
            Assert.Contains("reference", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AStyleWithBothLinks_isRefused_andAPreformattedRefusesALinkAnchor()
    {
        var doc = ShortPage();
        doc.Add(new Paragraph("x", Style("https://e.example/", "t")));
        doc.Add(new Paragraph("target") { Anchor = "t" });
        Assert.Throws<InvalidOperationException>(() => Save(doc));

        var ex = Assert.Throws<ArgumentException>(() => new Preformatted("code", Style(anchor: "t")));
        Assert.Equal("style", ex.ParamName);
    }

    [Fact]
    public void ALinkToAnAnchorOnAnItemBelowTheSecondLevel_isRefusedBeforeAnyLayout()
    {
        var doc = ShortPage();
        var leaf = new ListItem("deep") { Anchor = "d" };
        var middle = new ListItem("middle");
        middle.AddChild(leaf);
        var top = new ListItem("top");
        top.AddChild(middle);
        doc.Add(new ListElement().Add(top));
        doc.Add(new Paragraph([TextRun.PageReference("d", S)]));

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("neither lays out nor draws", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALabelOfOnlyWhiteSpace_isRefusedAfterTheLayout_naming_theAnchor()
    {
        var doc = ShortPage();
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.None, " ");
        doc.Add(new Paragraph([new TextRun("See ", S), TextRun.PageReference("t", S)]));
        doc.Add(new Paragraph("target") { Anchor = "t" });

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("'t'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be saved again", Assert.Throws<InvalidOperationException>(() => Save(doc)).Message, StringComparison.Ordinal);
    }

    private sealed class Wrapper(IRenderer inner) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => inner.Layout(context);

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    [Fact]
    public void AReferenceACallersRendererHolds_isRefusedAfterTheLayout_naming_theAnchor()
    {
        var doc = ShortPage();
        doc.Add(new Paragraph("target") { Anchor = "t" });
        doc.Add(new Wrapper(new ParagraphRenderer(new Paragraph([new TextRun("See ", S), TextRun.PageReference("t", S)]))));

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("'t'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("did not find it", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceLaidOutOutsideASave_isRefused()
    {
        var renderer = new ParagraphRenderer(new Paragraph([TextRun.PageReference("t", S)]));
        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 100, 100))));
        Assert.Contains("'t'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("outside a save", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1e-5, true)]
    [InlineData(12.0, false)]
    public void ALeaderTabBeforeAReferenceInATinyFont_isRefusedBeforeAnyLayout_asALiteralIs(double size, bool refused)
    {
        var tiny = new TextStyle { FontRef = Helv, FontSize = size };
        foreach (var useReference in new[] { true, false })
        {
            var doc = ShortPage();
            doc.Add(new Paragraph([new TextRun("a\t", S), useReference ? TextRun.PageReference("t", tiny) : new TextRun("1", tiny)])
            {
                TabStops = [new TabStop(200, TabAlignment.Left, TabLeader.Dots)],
            });
            doc.Add(new Paragraph("target") { Anchor = "t" });

            if (refused)
                Assert.Throws<InvalidOperationException>(() => Save(doc));
            else
                Assert.NotEmpty(Save(doc));
        }
    }
}
