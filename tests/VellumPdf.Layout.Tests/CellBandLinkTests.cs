// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using static VellumPdf.Layout.Tests.LinkSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A link in the style of a table cell or a running band (#475). The fixtures are 300x400pt with
/// 10pt margins in Helvetica 12pt: a line is 14.4pt, a cell's default padding is 6pt left and 4pt
/// above, so a cell's text starts at x = 16 and its first baseline is at y = 374. The rectangles
/// are worked out from the text the content stream shows and the font's own measure, never from
/// the code that draws them.
/// </summary>
public sealed class CellBandLinkTests
{
    private const string Uri = "https://example.com/x";

    private static TableElement Table(TextStyle? style = null, params double[] widths) =>
        new TableElement { DefaultCellStyle = style }.SetColumnWidths(widths.Length == 0 ? [100, 100] : widths);

    private static double Width(string text, double size = 12) => Helv.MeasureString(text, size);

    [Fact]
    public void AStringCell_linksEachDrawnLine_overTheWidthThatLineIsMeasuredAt()
    {
        using var doc = ShortPage();
        var table = Table();
        table.AddRow().AddCell(new Cell("alpha beta gamma delta epsilon zeta eta theta iota") { Style = Style(Uri) });
        doc.Add(table);

        var pdf = Save(doc);
        var lines = ContentStreamReadback.TextPlacements(Content(pdf)).Where(p => p.Text.Length > 0).ToList();
        var links = Links(pdf);

        // A fixture that wrapped to one line would prove nothing about "per line".
        Assert.True(lines.Count >= 3);
        Assert.Equal(lines.Count, links.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            Assert.Equal(Uri, links[i].Uri);
            Assert.Equal(lines[i].X, links[i].X0, 3);
            Assert.Equal(lines[i].X + Width(lines[i].Text), links[i].X1, 3);

            // The line box is as tall as the leading, and its top is one font size above the
            // baseline.
            Assert.Equal(lines[i].Y + 12, links[i].Y1, 3);
            Assert.Equal(lines[i].Y + 12 - 14.4, links[i].Y0, 3);
        }
    }

    [Fact]
    public void ADefaultCellStyleLink_linksEveryCell_andANonLinkCellStyleOverridesIt()
    {
        using var doc = ShortPage();
        var table = Table(Style(Uri));
        table.AddRow().AddCell("one").AddCell(new Cell("two") { Style = Style() });
        doc.Add(table);

        var link = Assert.Single(Links(Save(doc)));
        Assert.Equal(16, link.X0, 3);
        Assert.Equal(16 + Width("one"), link.X1, 3);
    }

    [Fact]
    public void ACellStylesAnchorLink_isADestinationLinkOverTheLine()
    {
        using var doc = ShortPage();
        doc.Add(new Paragraph("target") { Anchor = "here" });
        var table = Table();
        table.AddRow().AddCell(new Cell("go") { Style = Style(anchor: "here") });
        doc.Add(table);

        var link = Assert.Single(Links(Save(doc)));
        Assert.Null(link.Uri);
        Assert.Equal("here", link.Dest);
    }

    [Fact]
    public void ARunCell_isNotLinkedByTheStyleOfItsCell_butByTheStyleOfItsRuns()
    {
        using var doc = ShortPage();
        var table = Table();
        table.AddRow().AddCell(new Cell([new TextRun("a ", Style()), new TextRun("b", Style())]) { Style = Style(Uri) });
        doc.Add(table);
        Assert.Empty(Links(Save(doc)));

        using var linked = ShortPage();
        var table2 = Table();
        table2.AddRow().AddCell(new Cell([new TextRun("a ", Style()), new TextRun("b", Style(Uri))]) { Style = Style("https://other.example/") });
        linked.Add(table2);
        var link = Assert.Single(Links(Save(linked)));
        Assert.Equal(Uri, link.Uri);
    }

    [Fact]
    public void AnEmptyCell_writesNoLink()
    {
        using var doc = ShortPage();
        var table = Table(Style(Uri));
        table.AddRow().AddCell("").AddCell("x");
        doc.Add(table);

        // Only the cell with text is linked.
        var link = Assert.Single(Links(Save(doc)));
        Assert.Equal(116, link.X0, 3);
    }

    private static TextStyle BandStyle(string? uri = null, string? anchor = null) =>
        new() { FontRef = Helv, FontSize = 12, LinkUri = uri, LinkAnchor = anchor };

    [Fact]
    public void AHeaderAndAFooter_eachWriteOneLinkPerPage_overTheirDrawnText()
    {
        using var doc = ShortPage();
        doc.SetHeader("head", BandStyle(Uri), HorizontalAlignment.Left);
        doc.SetFooter("foot", BandStyle(Uri), HorizontalAlignment.Right);
        doc.Add(new Paragraph("one"));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("two"));

        var links = Links(Save(doc));

        // Two pages, two bands: four annotations, one per band per page.
        Assert.Equal(4, links.Count);

        // The header band is 18.4pt tall (the leading and 4) from the top margin; the footer sits
        // on
        // the bottom margin.
        var headers = links.Where(l => l.Y1 > 300).ToList();
        var footers = links.Where(l => l.Y1 <= 300).ToList();
        Assert.Equal(2, headers.Count);
        Assert.Equal(2, footers.Count);
        foreach (var header in headers)
        {
            Assert.Equal(10, header.X0, 3);
            Assert.Equal(10 + Width("head"), header.X1, 3);
            Assert.Equal(400 - 10, header.Y1, 3);
            Assert.Equal(400 - 10 - 18.4, header.Y0, 3);
        }

        foreach (var footer in footers)
        {
            Assert.Equal(290 - Width("foot"), footer.X0, 3);
            Assert.Equal(290, footer.X1, 3);
            Assert.Equal(10 + 18.4, footer.Y1, 3);
            Assert.Equal(10, footer.Y0, 3);
        }
    }

    [Fact]
    public void MirroredMargins_moveTheBandLinkWithTheText()
    {
        using var doc = ShortPage();
        doc.Margins = new EdgeInsets(10, 40, 10, 10);
        doc.MirrorMargins = true;
        doc.SetHeader("head", BandStyle(Uri), HorizontalAlignment.Left);
        doc.Add(new Paragraph("one"));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("two"));

        var links = Links(Save(doc));
        Assert.Equal(2, links.Count);

        // The inside margin (Left, 10) is on the left of a recto page and on the right of a verso
        // one. A left-aligned band stays against the inside margin, so on the verso page it ends
        // at the page's right edge less that margin.
        var recto = links[0];
        var verso = links[1];
        Assert.Equal(10, recto.X0, 3);
        Assert.Equal(300 - 10 - Width("head"), verso.X0, 3);
        Assert.Equal(300 - 10, verso.X1, 3);
    }

    [Fact]
    public void AnEmptyBand_writesNoLink()
    {
        using var doc = ShortPage();
        doc.SetHeader("", BandStyle(Uri));
        doc.Add(new Paragraph("body"));
        Assert.Empty(Links(Save(doc)));
    }

    [Fact]
    public void ABandOfOnlySpaces_writesNoLink_asABlankCellDoesNot()
    {
        using var doc = ShortPage();
        doc.SetHeader("   ", BandStyle(Uri));
        doc.SetFooter("   ", BandStyle(anchor: "t"));
        doc.Add(new Paragraph("target") { Anchor = "t" });
        var table = Table(Style(Uri));
        table.AddRow().AddCell("   ");
        doc.Add(table);

        Assert.Empty(Links(Save(doc)));
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.LinkNotWritten);
    }

    [Fact]
    public void UnderPdfUa1_aBandOfOnlySpaces_reportsNothing_andAStrictSaveStillSaves()
    {
        using var doc = Ua1(out var link);
        doc.StrictDiagnostics = true;
        doc.SetHeader("   ", link);
        doc.SetFooter("   ", link);
        doc.Add(new Paragraph("body", new TextStyle { FontRef = link.FontRef, FontSize = 12 }));

        Assert.NotEmpty(Save(doc));
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.LinkNotWritten);
    }

    // ── PDF/UA-1 ─────────────────────────────────────────────────────────────

    private static Document Ua1(out TextStyle style)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 400),
            Margins = new EdgeInsets(10),
            Conformance = PdfConformance.PdfUA1,
            Tagged = true,
            Language = "en-US",
        };
        doc.Info.Title = "Links";
        var handle = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
        style = new TextStyle { FontRef = handle, FontSize = 12, LinkUri = Uri };
        return doc;
    }

    [Fact]
    public void UnderPdfUa1_aCellLinkIsNotWritten_andIsReportedOncePerTable()
    {
        const string Text = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda";

        // The same table without the claim writes one link per drawn line, which is what the claim
        // leaves unwritten.
        using var plain = new Document { PageSize = new PdfRectangle(0, 0, 300, 400), Margins = new EdgeInsets(10) };
        var plainStyle = new TextStyle { FontRef = plain.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 12, LinkUri = Uri };
        var plainTable = new TableElement { DefaultCellStyle = plainStyle }.SetColumnWidths(100);
        plainTable.AddRow().AddCell(Text);
        plain.Add(plainTable);
        var expected = Links(Save(plain)).Count;
        Assert.True(expected >= 2);

        using var doc = Ua1(out var link);
        var table = new TableElement { DefaultCellStyle = link }.SetColumnWidths(100);
        table.AddRow().AddCell(Text);
        doc.Add(table);

        Assert.Empty(Links(Save(doc)));
        var item = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.LinkNotWritten);
        Assert.Equal(LayoutDiagnosticSeverity.Error, item.Severity);
        Assert.Equal(LayoutElementKind.Table, item.ElementKind);
        Assert.Equal(expected, item.Count);
    }

    [Fact]
    public void UnderPdfUa1_aBandLinkIsNotWritten_andIsReportedOncePerBandKindWithThePagesAsItsCount()
    {
        using var doc = Ua1(out var link);
        doc.SetHeader("head", link);
        doc.SetFooter("foot", link);
        doc.Add(new Paragraph("one", new TextStyle { FontRef = link.FontRef, FontSize = 12 }));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("two", new TextStyle { FontRef = link.FontRef, FontSize = 12 }));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("three", new TextStyle { FontRef = link.FontRef, FontSize = 12 }));

        Assert.Empty(Links(Save(doc)));

        var items = doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.LinkNotWritten).ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, d => d.ElementKind == LayoutElementKind.Header && d.Count == 3);
        Assert.Contains(items, d => d.ElementKind == LayoutElementKind.Footer && d.Count == 3);
    }

    [Fact]
    public void UnderPdfUa1_strictModeRefusesTheDroppedLink()
    {
        using var doc = Ua1(out var link);
        doc.StrictDiagnostics = true;
        doc.SetHeader("head", link);
        doc.Add(new Paragraph("body", new TextStyle { FontRef = link.FontRef, FontSize = 12 }));

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("LinkNotWritten", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnderPdfUa1_aParagraphLinkIsStillWritten_andNothingIsReported()
    {
        using var doc = Ua1(out var link);
        doc.Add(new Paragraph("go", link));

        Assert.Single(Links(Save(doc)));
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.LinkNotWritten);
    }

    // ── A context with no page, and a rectangle the file cannot hold ─────────

    private static (PdfDocument Pdf, PdfPage Page, PdfCanvas Canvas, RendererContext Context) Surface()
    {
        var pdf = new PdfDocument();
        var page = pdf.AddPage(new PdfRectangle(0, 0, 300, 400));
        return (pdf, page, new PdfCanvas(page), new RendererContext(page, pdf));
    }

    [Fact]
    public void ACellLink_throughAContextWithNoPage_isWrittenNowhere_andRaisesNothing()
    {
        var (pdf, _, canvas, context) = Surface();
        var table = Table(Style(Uri));
        table.AddRow().AddCell("one");
        var renderer = new TableRenderer(table);
        var box = new LayoutBox(0, 0, 300, 400);
        renderer.Layout(new LayoutContext(box));

        renderer.Draw(new DrawContext(canvas, box, context, pdf, null!));

        using var ms = new MemoryStream();
        pdf.Save(ms);
        Assert.Empty(Links(ms.ToArray()));
    }

    [Fact]
    public void AParagraphLink_throughAContextWithNoPage_stillThrows_withTheParameterNameKey()
    {
        var (pdf, _, canvas, context) = Surface();
        var renderer = new ParagraphRenderer(new Paragraph("go", Style(Uri)));
        var box = new LayoutBox(0, 0, 300, 400);
        renderer.Layout(new LayoutContext(box));

        var ex = Assert.Throws<ArgumentNullException>(
            () => renderer.Draw(new DrawContext(canvas, box, context, pdf, null!)));
        Assert.Equal("key", ex.ParamName);
    }

    [Fact]
    public void ARunLinkInACell_throughAContextWithNoPage_throwsAsAParagraphDoes()
    {
        var (pdf, _, canvas, context) = Surface();
        var table = Table();
        table.AddRow().AddCell(new Cell([new TextRun("go", Style(Uri))]));
        var renderer = new TableRenderer(table);
        var box = new LayoutBox(0, 0, 300, 400);
        renderer.Layout(new LayoutContext(box));

        var ex = Assert.Throws<ArgumentNullException>(
            () => renderer.Draw(new DrawContext(canvas, box, context, pdf, null!)));
        Assert.Equal("key", ex.ParamName);
    }

    [Fact]
    public void TheGuardOfTheContext_dropsARectangleThatWouldBeWrittenNonFinite_andWritesOneThatWouldNot()
    {
        var (pdf, page, canvas, context) = Surface();
        var ctx = new DrawContext(canvas, new LayoutBox(0, 0, 300, 400), context, pdf, page);

        // x plus width overflows, so the annotation would write infinity, which the save refuses.
        Assert.False(ctx.TryAddLinkAnnotation(new LayoutBox(double.MaxValue, 0, double.MaxValue, 10), Uri, null, LinkOwner.Cell));
        Assert.False(ctx.TryAddLinkAnnotation(new LayoutBox(double.NaN, 0, 10, 10), Uri, null, LinkOwner.Band));
        Assert.True(ctx.TryAddLinkAnnotation(new LayoutBox(10, 10, 50, 10), Uri, null, LinkOwner.Cell));

        using var ms = new MemoryStream();
        pdf.Save(ms);
        Assert.Single(Links(ms.ToArray()));
    }

    [Theory]
    [InlineData(double.MaxValue, 1e308)]
    [InlineData(1e296, 1e303)]
    public void ACellLink_whoseRectangleWouldBeWrittenNonFinite_isDroppedAndSaves(double x, double bottomSpan)
    {
        byte[] Draw(string? uri)
        {
            var (pdf, _, canvas, context) = Surface();
            var table = Table(Style(uri));
            table.AddRow().AddCell("one");
            var renderer = new TableRenderer(table);
            var box = new LayoutBox(x, 0, 1000, bottomSpan);
            renderer.Layout(new LayoutContext(box));
            renderer.Draw(new DrawContext(canvas, new LayoutBox(0, 0, 300, 400), context, pdf, pdf.Pages[0]));
            using var ms = new MemoryStream();
            pdf.Save(ms);
            return ms.ToArray();
        }

        var linked = Draw(Uri);

        // The fixture draws text far off the page, so the link and the text are the only things
        // that differ: with the style's link the file holds no annotation and saves.
        Assert.Empty(Links(linked));
        Assert.Equal(Draw(null).Length, linked.Length);
    }
}
