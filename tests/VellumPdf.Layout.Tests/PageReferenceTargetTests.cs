// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using static VellumPdf.Layout.Tests.LinkSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// What a page reference points at, and what it prints, for the targets and the labels that are
/// not the plain case: a prefix with control white space, an element that goes on over two pages,
/// the other kinds of element that hold an anchor, and a table whose cache is read across runs.
/// </summary>
public sealed class PageReferenceTargetTests
{
    private static readonly TextStyle S = Style();

    private static Document WithReferenceThenTarget(Action<Document> addTarget)
    {
        var doc = ShortPage();
        doc.Add(new Paragraph([new TextRun("See ", S), TextRun.PageReference("t", S)]));
        doc.Add(new PageBreak());
        addTarget(doc);
        return doc;
    }

    [Theory]
    [InlineData("A\t", "A ")]
    [InlineData("A\n", "A ")]
    public void AControlCharacterInAPrefix_isASpaceInTheLabel_asThePageTokenPrintsIt(string prefix, string printed)
    {
        var doc = WithReferenceThenTarget(d => d.Add(new Paragraph("target") { Anchor = "t" }));
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal, prefix);
        doc.SetFooter("{page}");

        var shown = Shown(Save(doc));

        // The label is "A 1" on the first page, where the reference is, and "A 2" on the second,
        // where its target is; the reference prints the second. The footer prints the same string.
        Assert.Contains(printed + "2", shown);
        Assert.DoesNotContain(shown, t => t.Contains('\t') || t.Contains('\n'));
    }

    [Fact]
    public void ATargetThatSplitsOverTwoPages_isTheLabelOfThePageItsFirstLineIsOn()
    {
        var doc = ShortPage();
        doc.Add(new Paragraph([new TextRun("See ", S), TextRun.PageReference("t", S)]));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("filler"));
        doc.Add(new PageBreak());
        var words = string.Join(" ", Enumerable.Range(0, 400).Select(i => "w" + i));
        doc.Add(new Paragraph(words, Style(size: 12)) { Anchor = "t" });

        var pdf = Save(doc);

        // The target starts on page 3 and runs on to a fourth.
        Assert.Contains("3", Shown(pdf));
        Assert.DoesNotContain("4", Shown(pdf).Where(t => t.Length == 1));
    }

    [Fact]
    public void AReference_toAListAndToAPreformattedBlock_printsThePagesTheyStartOn()
    {
        var doc = ShortPage();
        doc.Add(new Paragraph([new TextRun("List on ", S), TextRun.PageReference("l", S), new TextRun(", code on ", S), TextRun.PageReference("p", S)]));
        doc.Add(new PageBreak());
        doc.Add(new ListElement { Anchor = "l" }.Add("one"));
        doc.Add(new PageBreak());
        doc.Add(new Preformatted("code", Style()) { Anchor = "p" });

        var shown = Shown(Save(doc)).Select(t => t.Trim()).ToList();

        Assert.Contains("2", shown);
        Assert.Contains("3", shown);
    }

    [Fact]
    public void AnAnchorTheDrawingPassNeverReports_isRefusedWithTheSameMessageAsWithoutAReference()
    {
        string Refusal(bool reference)
        {
            var doc = ShortPage();

            // A renderer that continues a paragraph from its second line never draws the first, so
            // the anchor is held and never placed.
            doc.Add(new ParagraphRenderer(new Paragraph(string.Join(" ", Enumerable.Repeat("word", 200))) { Anchor = "t" }, startLine: 1));
            if (reference) doc.Add(new Paragraph([TextRun.PageReference("t", S)]));
            return Assert.Throws<InvalidOperationException>(() => Save(doc)).Message;
        }

        var without = Refusal(reference: false);
        Assert.Equal(without, Refusal(reference: true));
        Assert.Contains("never placed", without, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceInAnItemBelowTheSecondLevel_isNotDrawn_andIsIgnored()
    {
        var doc = ShortPage();
        var leaf = new ListItem([new TextRun("deep ", S), TextRun.PageReference("t", S)]);
        var middle = new ListItem("middle");
        middle.AddChild(leaf);
        var top = new ListItem("top");
        top.AddChild(middle);
        doc.Add(new ListElement().Add(top));
        doc.Add(new Paragraph("target") { Anchor = "t" });

        var shown = Shown(Save(doc));

        Assert.DoesNotContain(shown, t => t.Contains("deep", StringComparison.Ordinal));
    }

    [Fact]
    public void ALeaderBeforeAReferenceAtTwelvePoints_isDrawn()
    {
        var doc = ShortPage();
        doc.Add(new Paragraph([new TextRun("a\t", S), TextRun.PageReference("t", S)])
        {
            TabStops = [new TabStop(200, TabAlignment.Left, TabLeader.Dots)],
        });
        doc.Add(new Paragraph("target") { Anchor = "t" });

        Assert.Contains("0.96 w", Content(Save(doc)), StringComparison.Ordinal);
    }

    [Fact]
    public void ATableThatHoldsAReference_reusesItsGridOnlyWhileNoLabelChanges()
    {
        TableElement Build()
        {
            var table = new TableElement { DefaultCellStyle = S }.SetColumnWidths(100, 100);
            table.AddRow()
                .AddCell(new Cell([new TextRun("see ", S), TextRun.PageReference("t", S)]))
                .AddCell("beside");
            return table;
        }

        var doc = ShortPage();
        doc.Add(Build());
        doc.Add(new PageBreak());
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("filler"));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("target") { Anchor = "t" });

        var probe = new TableMeasureProbe();
        TableMeasureProbe.Current = probe;
        try
        {
            var renderer = doc.CreateRenderer();
            renderer.RunLayout();

            // The label is "1" in the first run and "3" in the next, so the grid is resolved again
            // when it changes and reused when it does not: every pass after the second.
            Assert.Equal(3, renderer.MeasureRuns);
            Assert.True(probe.GridsReused >= 1, $"grids reused: {probe.GridsReused}");
        }
        finally
        {
            TableMeasureProbe.Current = null;
        }
    }

    [Fact]
    public void ARunCellWhoseReferenceHasANonFiniteColour_isRefusedByTheConstructor_asAnyRunThatHoldsTextIs()
    {
        var bad = new TextStyle { FontRef = Helv, FontSize = 12, Color = new ColorRgb(double.NaN, 0, 0) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Cell([TextRun.PageReference("t", bad)]));
        Assert.Equal("runs", ex.ParamName);
    }

    [Fact]
    public void ACellLink_inAnAreaWithAnInfiniteBottom_isDrawnOverTheCellOnly()
    {
        var pdf = new VellumPdf.Document.PdfDocument();
        var page = pdf.AddPage(new PdfRectangle(0, 0, 300, 400));
        var table = new TableElement { DefaultCellStyle = Style("https://e.example/") }.SetColumnWidths(100);
        table.AddRow().AddCell("one");
        var renderer = new TableRenderer(table);
        var box = new LayoutBox(0, 0, 300, double.PositiveInfinity);
        renderer.Layout(new LayoutContext(box));

        renderer.Draw(new DrawContext(new VellumPdf.Canvas.PdfCanvas(page), new LayoutBox(0, 0, 300, 400), new RendererContext(page, pdf), pdf, page));
        using var ms = new MemoryStream();
        pdf.Save(ms);

        // The cell's own height is finite, so the rectangle is and the link is written.
        var link = Assert.Single(Links(ms.ToArray()));
        Assert.True(link.Y1 - link.Y0 < 20);
    }
}
