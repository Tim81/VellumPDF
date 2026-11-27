// Copyright © Timothy van der Ham (@Tim81) SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.LinkSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// One PDF/A-2u document with roman front matter and a decimal body, an embedded font, anchors on a
/// heading, a paragraph, a table, a list item and an image, a link to an anchor from every text
/// site, forward and backward page references in a paragraph, a heading, a list item and a run
/// cell, and a URI link from a cell and a band. It is built from the Layout members alone, and
/// validated by veraPDF, qpdf and poppler.
/// </summary>
public sealed class LinksOracleTests
{
    private static Document Build(bool withReferenceInCell = true)
    {
        var doc = FontReportSupport.NewDocument(PdfConformance.PdfA2u);
        doc.PageSize = new PdfRectangle(0, 0, 400, 500);
        doc.Margins = new EdgeInsets(40);
        doc.Info.Title = "Links";
        doc.Language = "en-US";
        var font = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
        TextStyle Plain() => new() { FontRef = font, FontSize = 11 };
        TextStyle ToAnchor(string name) => new() { FontRef = font, FontSize = 11, LinkAnchor = name };

        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman);
        doc.Header = new RunningBand("Contents link", new TextStyle { FontRef = font, FontSize = 9, LinkAnchor = "intro" }, HorizontalAlignment.Left);
        doc.Footer = new RunningBand("{page}", new TextStyle { FontRef = font, FontSize = 9, LinkUri = "https://example.com/footer" });

        // Front matter, roman: a heading that others refer to, and a forward reference to the body.
        doc.Add(new Heading([new TextRun("Introduction, see page ", Plain()), TextRun.PageReference("body", Plain())]) { Anchor = "intro" });
        doc.Add(new Paragraph([new TextRun("Start of the body: ", Plain()), new TextRun("go to it", ToAnchor("body"))]));
        doc.Add(new PageBreak { PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal) });

        doc.Add(new Paragraph([new TextRun("The body, back on page ", Plain()), TextRun.PageReference("intro", Plain())]) { Anchor = "body" });

        var list = new ListElement { DefaultStyle = Plain() };
        var item = new ListItem([new TextRun("See the table on page ", Plain()), TextRun.PageReference("table", Plain())]) { Anchor = "item" };
        item.AddChild(new ListItem([new TextRun("Child that links ", Plain()), new TextRun("up", ToAnchor("item"))]));
        list.Add(item);
        doc.Add(list);

        var table = new TableElement { DefaultCellStyle = Plain(), Anchor = "table" }.SetColumnWidths(150, 150);
        var row = table.AddRow();
        row.AddCell(new Cell("string cell link") { Style = new TextStyle { FontRef = font, FontSize = 11, LinkUri = "https://example.com/cell" } });
        row.AddCell(withReferenceInCell
            ? new Cell([new TextRun("run cell, page ", Plain()), TextRun.PageReference("image", Plain())])
            : new Cell("run cell, page"));
        doc.Add(table);

        doc.Add(new PageBreak());
        var image = new LayoutImage(VellumPdf.Images.PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng()))
        {
            Width = 60,
            Anchor = "image",
            AltText = "A small image",
        };
        doc.Add(image);
        doc.Add(new Paragraph([new TextRun("Back to the ", Plain()), new TextRun("introduction", ToAnchor("intro"))]));
        return doc;
    }

    [Fact]
    public void TheDocument_isPdfA2uConformant_andItsLinksAndDestinationsResolve()
    {
        using var doc = Build(withReferenceInCell: false);
        var pdf = FontReportSupport.Save(doc);

        // The change is in the file: destinations, destination links, a reference that read a page
        // other than the placeholder's, and a URI link of each of the two kinds.
        var links = Links(pdf);
        Assert.Contains(links, l => l.Dest == "body");
        Assert.Contains(links, l => l.Dest == "intro");
        Assert.Contains(links, l => l.Dest == "item");
        Assert.Contains(links, l => l.Dest == "table");
        Assert.Contains(links, l => l.Uri == "https://example.com/cell");
        Assert.Contains(links, l => l.Uri == "https://example.com/footer");

        Assert.Empty(FontReportSupport.FailedRules(FontReportSupport.VeraPdf("2u", pdf)));

        using var dir = new FontReportSupport.TempDir();
        var path = dir.Path("links.pdf");
        File.WriteAllBytes(path, pdf);

        if (!ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut))
            OracleGate.Unavailable("qpdf");
        Assert.False(timedOut);
        Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);

        // Every named destination resolves to a page, and the page is the anchor's: the heading and
        // the first paragraph's page break put the body on page 2, the image on page 3.
        if (!ExternalTool.TryRun("pdfinfo", ["-dests", path], out exit, out stdout, out stderr, out timedOut))
            OracleGate.Unavailable("pdfinfo");
        Assert.True(exit == 0, $"pdfinfo -dests exit {exit}\n{stdout}\n{stderr}");
        var pages = Regex.Matches(stdout, @"^\s*(?<page>\d+)\s+\[[^\]]*\]\s+""(?<name>[^""]+)""", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups["name"].Value, m => int.Parse(m.Groups["page"].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(pages.Count > 0, stdout);
        Assert.Equal(1, pages["intro"]);
        Assert.Equal(2, pages["body"]);
        Assert.Equal(2, pages["item"]);
        Assert.Equal(2, pages["table"]);
        Assert.Equal(3, pages["image"]);

        // Every link to a name names one of them, and no destination lies off the pages.
        foreach (var name in links.Where(l => l.Dest is not null).Select(l => l.Dest!).Distinct())
            Assert.True(pages.ContainsKey(name), $"the link to '{name}' has no destination in the name tree");
        Assert.All(pages.Values, p => Assert.InRange(p, 1, 3));
    }

    [Fact]
    public void TheReferences_printTheLabelsOfTheirPages_asPopplerReadsThem()
    {
        using var doc = Build();
        var pdf = FontReportSupport.Save(doc);

        using var dir = new FontReportSupport.TempDir();
        var path = dir.Path("labels.pdf");
        File.WriteAllBytes(path, pdf);
        if (!ExternalTool.TryRun("pdftotext", ["-layout", path, "-"], out var exit, out var text, out var stderr, out var timedOut))
            OracleGate.Unavailable("pdftotext");
        Assert.True(exit == 0 && !timedOut, $"pdftotext exit {exit}, stderr: {stderr}");

        // Page 1 is labelled i and the body is the first page of the decimal range, so the forward
        // reference prints 1 and the backward one prints i; the image sits on the range's second
        // page, which is 2.
        var squashed = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("Introduction, see page 1", squashed, StringComparison.Ordinal);
        Assert.Contains("The body, back on page i", squashed, StringComparison.Ordinal);
        Assert.Contains("See the table on page 1", squashed, StringComparison.Ordinal);
        Assert.Contains("run cell, page 2", squashed, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentWithAReferenceInARunCell_isPdfA2uConformantToo()
    {
        using var doc = Build(withReferenceInCell: true);
        var pdf = FontReportSupport.Save(doc);
        Assert.Empty(FontReportSupport.FailedRules(FontReportSupport.VeraPdf("2u", pdf)));
        Assert.Contains(Links(pdf), l => l.Dest == "image");
    }

    [Fact]
    public void ThisFile_namesNoKernelRendererOrCanvasType_theMembersOfLayoutAreEnough()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "LinksOracleTests.cs"));

        // The names are assembled so that this check does not find itself.
        foreach (var name in new[] { "I" + "Renderer", "Pdf" + "Canvas", "Pdf" + "Document", "Draw" + "Context" })
            Assert.DoesNotContain(name, source, StringComparison.Ordinal);
    }
}
