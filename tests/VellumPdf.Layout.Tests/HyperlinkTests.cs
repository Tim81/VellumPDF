// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

public sealed class HyperlinkTests
{
    private static string SaveToString(Document doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    [Fact]
    public void LinkedText_producesLinkAnnotation()
    {
        using var doc = new Document();
        var linkStyle = new TextStyle { LinkUri = "https://example.com" };
        doc.Add(new Paragraph(new[]
        {
            new TextRun("Visit ", TextStyle.Default),
            new TextRun("example.com", linkStyle),
        }));

        var content = SaveToString(doc);
        Assert.Contains("/Subtype /Link", content);
    }

    [Fact]
    public void LinkedText_producesUriAction()
    {
        using var doc = new Document();
        var linkStyle = new TextStyle { LinkUri = "https://example.com/page" };
        doc.Add(new Paragraph("Click here", linkStyle));

        var content = SaveToString(doc);
        Assert.Contains("/URI", content);
        Assert.Contains("example.com/page", content);
    }

    [Fact]
    public void LinkedText_annotsKeyOnPageDict()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("link", new TextStyle { LinkUri = "https://x.com" }));

        var content = SaveToString(doc);
        Assert.Contains("/Annots", content);
    }

    [Fact]
    public void NoLinkedText_noAnnotsKey()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("plain text"));

        var content = SaveToString(doc);
        Assert.DoesNotContain("/Annots", content);
    }

    [Fact]
    public void MultipleLinkFragmentsOnSameParagraph_allAnnotationsPresent()
    {
        using var doc = new Document();
        var styleA = new TextStyle { LinkUri = "https://a.com" };
        var styleB = new TextStyle { LinkUri = "https://b.com" };
        doc.Add(new Paragraph(new[]
        {
            new TextRun("link-a", styleA),
            new TextRun(" and ", TextStyle.Default),
            new TextRun("link-b", styleB),
        }));

        var content = SaveToString(doc);
        Assert.Contains("a.com", content);
        Assert.Contains("b.com", content);
    }

    [Fact]
    public void LinkAnnotation_hasRectEntry()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("link", new TextStyle { LinkUri = "https://z.com" }));

        var content = SaveToString(doc);
        Assert.Contains("/Rect", content);
    }
}
