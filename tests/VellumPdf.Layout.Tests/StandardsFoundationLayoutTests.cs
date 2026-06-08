// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Layout-layer tests for the standards-foundation features:
/// XMP metadata, document /ID, PDF/A conformance, and tagged-PDF structure tree.
/// </summary>
public sealed class StandardsFoundationLayoutTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SaveToString(Document doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    // ── XMP via layout Document ───────────────────────────────────────────────

    [Fact]
    public void Document_withInfo_emitsXmpPacket()
    {
        using var doc = new Document();
        doc.Info.Title = "Layout XMP Test";
        doc.Info.Author = "Timothy";
        doc.Add(new Paragraph("Hello"));

        var content = SaveToString(doc);

        Assert.Contains("<?xpacket", content);
        Assert.Contains("dc:title", content);
        Assert.Contains("Layout XMP Test", content);
    }

    [Fact]
    public void Document_always_writesDocumentId()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("ID test"));

        var content = SaveToString(doc);

        Assert.Contains("/ID [<", content);
    }

    // ── PDF/A conformance via layout Document ─────────────────────────────────

    [Fact]
    public void Document_PdfA2b_conformanceProperty_roundtrips()
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        Assert.Equal(PdfConformance.PdfA2b, doc.Conformance);
    }

    [Fact]
    public void Document_PdfA2b_emitsPdfaid()
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Add(new Paragraph("PDF/A-2b test"));

        var content = SaveToString(doc);

        Assert.Contains("pdfaid:part", content);
        Assert.Contains("pdfaid:conformance", content);
        Assert.Contains(">B<", content);
    }

    [Fact]
    public void Document_PdfA2b_setsMarkInfo()
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Add(new Paragraph("Mark info test"));

        var content = SaveToString(doc);

        Assert.Contains("/MarkInfo", content);
        Assert.Contains("/Marked true", content);
    }

    // ── Tagged via layout Document ────────────────────────────────────────────

    [Fact]
    public void Document_tagged_false_default_noStructTreeRoot()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Untagged paragraph"));
        doc.Add(new Heading("Untagged heading"));

        var content = SaveToString(doc);

        Assert.DoesNotContain("/StructTreeRoot", content);
    }

    [Fact]
    public void Document_tagged_true_paragraphEmitsStructTreeRoot()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Paragraph("Tagged paragraph"));

        var content = SaveToString(doc);

        Assert.Contains("/StructTreeRoot", content);
    }

    [Fact]
    public void Document_tagged_true_paragraphEmitsStructElemP()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Paragraph("Paragraph content"));

        var content = SaveToString(doc);

        Assert.Contains("/S /P", content);
        Assert.Contains("/StructElem", content);
    }

    [Fact]
    public void Document_tagged_true_headingEmitsStructElemH1()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Heading("Top Heading", new TextStyle { FontSize = 16 }) { Level = 0 });

        var content = SaveToString(doc);

        Assert.Contains("/S /H1", content);
    }

    [Fact]
    public void Document_tagged_true_headingLevel1EmitsH2()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Heading("Sub Heading", new TextStyle { FontSize = 14 }) { Level = 1 });

        var content = SaveToString(doc);

        Assert.Contains("/S /H2", content);
    }

    [Fact]
    public void Document_tagged_true_mixedContent_bothStructTypes()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Heading("Chapter 1", new TextStyle { FontSize = 16 }) { Level = 0 });
        doc.Add(new Paragraph("Some paragraph text"));

        var content = SaveToString(doc);

        Assert.Contains("/S /H1", content);
        Assert.Contains("/S /P", content);
    }

    [Fact]
    public void Document_tagged_true_setsMarkInfoMarkedTrue()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Paragraph("Marked"));

        var content = SaveToString(doc);

        Assert.Contains("/MarkInfo", content);
        Assert.Contains("/Marked true", content);
    }

    [Fact]
    public void Document_tagged_false_noMarkInfo()
    {
        // Without Tagged or Conformance, /MarkInfo should NOT appear.
        using var doc = new Document();
        doc.Add(new Paragraph("No mark info"));

        var content = SaveToString(doc);

        Assert.DoesNotContain("/MarkInfo", content);
    }

    [Fact]
    public void Document_tagged_true_taggedProperty_roundtrips()
    {
        using var doc = new Document();
        doc.Tagged = true;
        Assert.True(doc.Tagged);
        doc.Tagged = false;
        Assert.False(doc.Tagged);
    }

    [Fact]
    public void Document_pdfA2a_impliesTagged()
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2a;
        Assert.True(doc.Tagged);
    }

    [Fact]
    public void Document_tagged_true_withParentTree()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Paragraph("Parent tree test"));

        var content = SaveToString(doc);

        Assert.Contains("/ParentTree", content);
    }

    [Fact]
    public void Document_untagged_existingTests_backwardCompat()
    {
        // Smoke test: untagged document still produces a valid PDF.
        using var doc = new Document();
        doc.Info.Title = "Compat test";
        doc.Add(new Paragraph("Some text."));
        doc.Add(new Heading("A heading") { Level = 0 });

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
        Assert.True(bytes.Length > 100);
    }
}
