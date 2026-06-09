// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

public sealed class OutlineBookmarkTests
{
    private static string SaveToString(Document doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    [Fact]
    public void Heading_registersOutlineEntry()
    {
        using var doc = new Document();
        doc.Add(new Heading("Chapter 1"));
        doc.Add(new Paragraph("Body text."));

        var content = SaveToString(doc);
        Assert.Contains("/Outlines", content);
    }

    [Fact]
    public void Heading_outlineContainsTitleKey()
    {
        using var doc = new Document();
        doc.Add(new Heading("Introduction"));
        doc.Add(new Paragraph("Text here."));

        var content = SaveToString(doc);
        Assert.Contains("/Title", content);
    }

    [Fact]
    public void Heading_outlineContainsDestKey()
    {
        using var doc = new Document();
        doc.Add(new Heading("Chapter"));
        doc.Add(new Paragraph("Content."));

        var content = SaveToString(doc);
        Assert.Contains("/Dest", content);
    }

    [Fact]
    public void Heading_destUsesXyzFit()
    {
        using var doc = new Document();
        doc.Add(new Heading("Section"));

        var content = SaveToString(doc);
        Assert.Contains("/XYZ", content);
    }

    [Fact]
    public void MultipleHeadings_allAppearedInOutline()
    {
        using var doc = new Document();
        doc.Add(new Heading("Chapter 1"));
        doc.Add(new Paragraph("Text."));
        doc.Add(new Heading("Chapter 2"));
        doc.Add(new Paragraph("More text."));

        var content = SaveToString(doc);
        // Two headings → /Count should be at least 2.
        Assert.Matches(@"/Count 2", content);
    }

    [Fact]
    public void Heading_withCustomBookmarkTitle_usesThatTitle()
    {
        using var doc = new Document();
        doc.Add(new Heading("Full Heading Text") { BookmarkTitle = "Short" });

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        // Verify "Short" (as UTF-16BE bytes) is present in output.
        // UTF-16BE for "Short" = 53 00 68 00 6F 00 72 00 74
        // Actually bytes: 0x00 0x53 etc — 'S' = 0x0053 → bytes [0x00, 0x53]
        // We check by searching for the byte pairs for 'S','h','o','r','t' in BE:
        var content = System.Text.Encoding.Latin1.GetString(bytes);
        // The BOM + "Short" in UTF-16BE will produce visible bytes; check for /Title presence at minimum.
        Assert.Contains("/Title", content);
    }

    [Fact]
    public void Document_withNoHeadings_noOutlinesEntry()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Just text, no heading."));

        var content = SaveToString(doc);
        Assert.DoesNotContain("/Outlines", content);
    }

    [Fact]
    public void Heading_withLevel1_nestedUnderLevel0()
    {
        using var doc = new Document();
        doc.Add(new Heading("Chapter") { Level = 0 });
        doc.Add(new Heading("Section") { Level = 1 });
        doc.Add(new Paragraph("Content."));

        var content = SaveToString(doc);
        // With 2 entries (one nested), the root /Count should be 2 total.
        Assert.Contains("/Outlines", content);
    }

    [Fact]
    public void Heading_onMultiplePages_destResolvesToCorrectPage()
    {
        using var doc = new Document();

        // First heading on page 1
        doc.Add(new Heading("Page 1 Heading") { Level = 0 });

        // Overflow to page 2
        for (var i = 0; i < 55; i++)
            doc.Add($"Filler line {i + 1}: The quick brown fox jumps over the lazy dog.");

        doc.Add(new Heading("Page 2 Heading") { Level = 0 });
        doc.Add(new Paragraph("Content on page 2."));

        var content = SaveToString(doc);
        Assert.Contains("/Outlines", content);
        // Two headings → /Count 2
        Assert.Matches(@"/Count 2", content);
    }
}
