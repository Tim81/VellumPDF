// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.LinkSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <see cref="TextStyle.LinkAnchor"/>: a link to a named destination, drawn wherever a style with
/// <see cref="TextStyle.LinkUri"/> draws one.
/// </summary>
public sealed class AnchorLinkTests
{
    private static readonly TextStyle Go = Style(anchor: "t");

    private static void AddTarget(Document doc)
    {
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("target") { Anchor = "t" });
    }

    private static List<Link> Dests(Document doc) => [.. Links(Save(doc)).Where(l => l.Dest == "t")];

    private static void AssertDestLink(Link link)
    {
        Assert.Equal("t", link.Dest);
        Assert.Null(link.Uri);
    }

    [Fact]
    public void AParagraphRun_isADestinationLink_notAGoToAction()
    {
        var doc = ShortPage();
        doc.Add(new Paragraph([new TextRun("see ", Style()), new TextRun("here", Go)]));
        AddTarget(doc);

        var bytes = Save(doc);
        var text = Encoding.Latin1.GetString(bytes);
        var link = Assert.Single(Links(bytes));
        AssertDestLink(link);

        // The link is /Dest (name) and has no /A action, and the name is a key of the name tree.
        Assert.DoesNotContain("/S /GoTo", text, StringComparison.Ordinal);
        Assert.Contains("/Dests", text, StringComparison.Ordinal);
        Assert.Contains("(t)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadingRun_aListItem_atBothLevels_aCellString_aCellRun_andABand_eachLinkToTheAnchor()
    {
        var doc = ShortPage();
        doc.SetFooter("foot", Go);
        doc.Add(new Heading([new TextRun("head", Go)]));
        var item = new ListItem([new TextRun("one", Go)]);
        item.AddChild(new ListItem([new TextRun("two", Go)]));
        doc.Add(new ListElement().Add(item));
        var table = new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(100, 100);
        table.AddRow().AddCell(new Cell("string") { Style = Go }).AddCell(new Cell([new TextRun("runs", Go)]));
        doc.Add(table);
        AddTarget(doc);

        var links = Dests(doc);

        // The heading, the two list items (their runs), the string cell, the run cell and the footer
        // on each of the two pages. The markers of the default list style carry the style of the
        // list, not the runs, so they add none.
        foreach (var link in links) AssertDestLink(link);
        Assert.Equal(1 + 2 + 1 + 1 + 2, links.Count);
    }

    [Fact]
    public void AListItemGivenTheStyle_linksItsMarkerAndItsText_asALinkUriDoes()
    {
        var doc = ShortPage();
        var list = new ListElement { DefaultStyle = Go };
        list.Add(new ListItem("one"));
        doc.Add(list);
        AddTarget(doc);

        Assert.Equal(2, Dests(doc).Count);
    }

    [Fact]
    public void ALinkToAnUnknownName_isRefusedBeforeAnyLayout_wherever_itSits_andTheDocumentCanStillBeSaved()
    {
        void Check(Action<Document, TextStyle> add, string site)
        {
            var doc = ShortPage();
            var bad = Style(anchor: "missing");
            add(doc, bad);
            var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
            Assert.Contains("\"missing\"", ex.Message, StringComparison.Ordinal);
            Assert.Contains(site, ex.Message, StringComparison.Ordinal);
            Assert.Contains("can be saved", ex.Message, StringComparison.Ordinal);

            // Giving an element the name makes the same document saveable: nothing was laid out.
            doc.Add(new Paragraph("here") { Anchor = "missing" });
            Assert.NotEmpty(Save(doc));
        }

        Check((d, s) => d.Add(new Paragraph("x", s)), "Paragraph 0");
        Check((d, s) => d.Add(new Heading("x", s)), "Heading 0");
        Check((d, s) => d.Add(new ListElement().Add(new ListItem("x", s))), "List 0 (item 0)");
        Check((d, s) =>
        {
            var parent = new ListItem("p");
            parent.AddChild(new ListItem("c", s));
            d.Add(new ListElement().Add(parent));
        }, "child 0");
        Check((d, s) =>
        {
            var table = new TableElement().SetColumnWidths(100);
            table.AddRow().AddCell(new Cell("x") { Style = s });
            d.Add(table);
        }, "row 0, cell 0");
        Check((d, s) =>
        {
            var table = new TableElement { DefaultCellStyle = s }.SetColumnWidths(100);
            table.AddRow().AddCell("x");
            d.Add(table);
        }, "row 0, cell 0");
        Check((d, s) => d.SetHeader("x", s), "Header band");
        Check((d, s) => d.SetFooter("x", s), "Footer band");
    }

    [Fact]
    public void ADefaultCellStyleThatALinkingCellStyleOverrides_isNotRead()
    {
        var doc = ShortPage();
        var table = new TableElement { DefaultCellStyle = Style(anchor: "missing") }.SetColumnWidths(100);
        table.AddRow().AddCell(new Cell("x") { Style = Style() });
        doc.Add(table);

        Assert.NotEmpty(Save(doc));
    }

    [Fact]
    public void ALinkInTheDefaultCellStyle_isRefusedUnderPdfUa1BeforeAnyLayout_andTheDocumentSavesOnceTheClaimIsChanged()
    {
        var doc = ShortPage();
        doc.Conformance = PdfConformance.PdfUA1;
        doc.Tagged = true;
        doc.Language = "en-US";
        doc.Info.Title = "t";
        var table = new TableElement { DefaultCellStyle = Go }.SetColumnWidths(100);
        table.AddRow().AddCell("x");
        doc.Add(table);
        AddTarget(doc);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("row 0, cell 0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("can be saved", ex.Message, StringComparison.Ordinal);

        doc.Conformance = PdfConformance.PdfA2u;
        Assert.NotEmpty(Save(doc));
    }

    [Fact]
    public void ALinkToAnchor_isRefusedUnderPdfUa1_naming_theElement_butSavesUnderPdfA2aAndA2u()
    {
        foreach (var conformance in new[] { PdfConformance.PdfUA1, PdfConformance.PdfA2a, PdfConformance.PdfA2u })
        {
            var doc = ShortPage();
            doc.Conformance = conformance;
            doc.Tagged = true;
            doc.Language = "en-US";
            doc.Info.Title = "t";
            doc.SetHeader("head", Go);
            doc.Add(new Paragraph("x", Go));
            AddTarget(doc);

            if (conformance == PdfConformance.PdfUA1)
            {
                var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
                Assert.Contains("PDF/UA-1", ex.Message, StringComparison.Ordinal);
                Assert.Contains("Paragraph 0", ex.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.NotEmpty(Save(doc));
            }
        }
    }

    // ── Registration, which a renderer the walk cannot see reaches ───────────

    private sealed class Hider(IRenderer inner) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => inner.Layout(context);

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    [Fact]
    public void ALinkInARendererTheWalkCannotSee_isRefusedAtRegistration_underPdfUa1_andForAnUnknownName()
    {
        var ua = ShortPage();
        ua.Conformance = PdfConformance.PdfUA1;
        ua.Tagged = true;
        ua.Language = "en-US";
        ua.Info.Title = "t";
        ua.Add(new Hider(new ParagraphRenderer(new Paragraph("x", Go))));
        Assert.Contains("PDF/UA-1", Assert.Throws<InvalidOperationException>(() => Save(ua)).Message, StringComparison.Ordinal);

        var unknown = ShortPage();
        unknown.Add(new Paragraph("target") { Anchor = "t" });
        unknown.Add(new Hider(new ParagraphRenderer(new Paragraph("x", Style(anchor: "elsewhere")))));
        var ex = Assert.Throws<InvalidOperationException>(() => Save(unknown));
        Assert.Contains("\"elsewhere\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be saved again", Assert.Throws<InvalidOperationException>(() => Save(unknown)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkThroughAContextOfYourOwn_isJudgedByTheKernelDocumentAtItsSave()
    {
        var pdf = new PdfDocument();
        var page = pdf.AddPage(new PdfRectangle(0, 0, 300, 400));
        var box = new LayoutBox(0, 0, 300, 400);
        var renderer = new ParagraphRenderer(new Paragraph("x", Style(anchor: "nowhere")));
        renderer.Layout(new LayoutContext(box));

        // With no layout in progress for this document there is no plan to judge the name against,
        // so the link is registered, and the kernel document refuses it when it is written.
        renderer.Draw(new DrawContext(new PdfCanvas(page), box, new RendererContext(page, pdf), pdf, page));
        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => pdf.Save(ms));
        Assert.Contains("nowhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AParagraphLinkToAnAnchor_throughAContextWithNoPage_throwsAsALinkUriDoes()
    {
        var pdf = new PdfDocument();
        var page = pdf.AddPage(new PdfRectangle(0, 0, 300, 400));
        var box = new LayoutBox(0, 0, 300, 400);
        var renderer = new ParagraphRenderer(new Paragraph("x", Go));
        renderer.Layout(new LayoutContext(box));

        var ex = Assert.Throws<ArgumentNullException>(
            () => renderer.Draw(new DrawContext(new PdfCanvas(page), box, new RendererContext(page, pdf), pdf, null!)));
        Assert.Equal("key", ex.ParamName);
    }

    [Fact]
    public void AStyleThatCopiesForAReference_copiesEveryPublicPropertyThatCanBeSet()
    {
        var source = new TextStyle
        {
            FontRef = new FontReference(VellumPdf.Fonts.Standard14.Courier),
            FontSize = 17.5,
            Leading = 21,
            Color = new ColorRgb(0.1, 0.2, 0.3),
        };
        var copy = source.WithLinkAnchor("x");

        // A property added to the style is a new name here, so this list is where it is noticed;
        // WithLinkAnchor must copy it, and the loop below checks that it does.
        var settable = typeof(TextStyle).GetProperties().Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Color", "Font", "FontRef", "FontSize", "Leading", "LinkAnchor", "LinkUri"], settable);

        // Every property with a public setter other than the two links carries across, which a
        // property added later must do as well: the reflection finds it, and a mismatch names it.
        foreach (var property in typeof(TextStyle).GetProperties().Where(p => p.SetMethod is { IsPublic: true }))
        {
            if (property.Name is nameof(TextStyle.LinkUri) or nameof(TextStyle.LinkAnchor)) continue;
            Assert.True(
                Equals(property.GetValue(source), property.GetValue(copy)),
                $"{property.Name} was not copied");
        }

        Assert.Equal("x", copy.LinkAnchor);
        Assert.Null(copy.LinkUri);
    }
}
