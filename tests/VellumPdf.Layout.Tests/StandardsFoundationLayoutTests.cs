// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

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

    // ── Tagged table struct elems ─────────────────────────────────────────────

    [Fact]
    public void Document_tagged_tableEmitsTableStructElem()
    {
        using var doc = new Document();
        doc.Tagged = true;

        var table = new TableElement();
        table.SetColumnWidths(200, 200);
        var row = table.AddRow();
        row.AddCell("A").AddCell("B");
        doc.Add(table);

        var content = SaveToString(doc);

        Assert.Contains("/S /Table", content);
    }

    [Fact]
    public void Document_tagged_tableEmitsTrStructElem()
    {
        using var doc = new Document();
        doc.Tagged = true;

        var table = new TableElement();
        table.SetColumnWidths(200, 200);
        var row = table.AddRow();
        row.AddCell("A").AddCell("B");
        doc.Add(table);

        var content = SaveToString(doc);

        Assert.Contains("/S /TR", content);
    }

    [Fact]
    public void Document_tagged_tableEmitsTdStructElem()
    {
        using var doc = new Document();
        doc.Tagged = true;

        var table = new TableElement();
        table.SetColumnWidths(200, 200);
        var row = table.AddRow();
        row.AddCell("CellA").AddCell("CellB");
        doc.Add(table);

        var content = SaveToString(doc);

        Assert.Contains("/S /TD", content);
    }

    [Fact]
    public void Document_tagged_headerRowEmitsThStructElem()
    {
        using var doc = new Document();
        doc.Tagged = true;

        var table = new TableElement();
        table.SetColumnWidths(200, 200);
        var header = table.AddHeaderRow();
        header.AddCell("Col1").AddCell("Col2");
        var row = table.AddRow();
        row.AddCell("A").AddCell("B");
        doc.Add(table);

        var content = SaveToString(doc);

        Assert.Contains("/S /TH", content);
    }

    [Fact]
    public void Document_untagged_tableNoStructTreeRoot()
    {
        // Untagged documents must NOT emit /StructTreeRoot.
        using var doc = new Document();
        var table = new TableElement();
        table.SetColumnWidths(200, 200);
        table.AddRow().AddCell("A").AddCell("B");
        doc.Add(table);

        var content = SaveToString(doc);

        Assert.DoesNotContain("/StructTreeRoot", content);
    }

    // ── Tagged list struct elems ──────────────────────────────────────────────

    [Fact]
    public void Document_tagged_listEmitsLStructElem()
    {
        using var doc = new Document();
        doc.Tagged = true;

        var list = new ListElement(ListStyle.OrderedDecimal);
        list.Add("First");
        list.Add("Second");
        doc.Add(list);

        var content = SaveToString(doc);

        Assert.Contains("/S /L", content);
    }

    [Fact]
    public void Document_tagged_listEmitsLiStructElem()
    {
        using var doc = new Document();
        doc.Tagged = true;

        var list = new ListElement(ListStyle.Unordered);
        list.Add("Item one");
        doc.Add(list);

        var content = SaveToString(doc);

        Assert.Contains("/S /LI", content);
    }

    [Fact]
    public void Document_tagged_listEmitsLbodyStructElem()
    {
        using var doc = new Document();
        doc.Tagged = true;

        var list = new ListElement(ListStyle.Unordered);
        list.Add("Body text");
        doc.Add(list);

        var content = SaveToString(doc);

        Assert.Contains("/S /LBody", content);
    }

    // ── Tagged figure struct elems ────────────────────────────────────────────

    [Fact]
    public void Document_tagged_imageEmitsFigureStructElem()
    {
        using var doc = new Document();
        doc.Tagged = true;

        // Use a minimal 1×1 pixel PNG (8-byte IDAT, valid PNG structure).
        var imgBytes = CreateMinimalPng();
        var imgXObj = VellumPdf.Images.PngImageLoader.Load(imgBytes);
        var img = new LayoutImage(imgXObj) { Width = 100, AltText = "Test figure" };
        doc.Add(img);

        var content = SaveToString(doc);

        Assert.Contains("/S /Figure", content);
    }

    // ── PDF/A OutputIntents at layout layer ───────────────────────────────────

    [Fact]
    public void Document_PdfA2b_emitsOutputIntents()
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Add(new Paragraph("PDF/A-2b layout test"));

        var content = SaveToString(doc);

        Assert.Contains("/OutputIntents", content);
        Assert.Contains("/GTS_PDFA1", content);
        Assert.Contains("/DestOutputProfile", content);
    }

    [Fact]
    public void Document_PdfA2b_iccStreamHasN3()
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Add(new Paragraph("ICC N3 test"));

        var content = SaveToString(doc);

        Assert.Contains("/N 3", content);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a minimal valid 2×2 white RGB PNG for image tagging tests.</summary>
    private static byte[] CreateMinimalPng()
    {
        using var ms = new MemoryStream();
        // PNG signature
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR: 2×2, 8-bit, RGB (colorType=2)
        var ihdr = new byte[13];
        ihdr[0] = 0; ihdr[1] = 0; ihdr[2] = 0; ihdr[3] = 2; // width=2
        ihdr[4] = 0; ihdr[5] = 0; ihdr[6] = 0; ihdr[7] = 2; // height=2
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // colour type: RGB
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace: none
        WritePngChunk(ms, "IHDR", ihdr);

        // IDAT: 2 rows of 2 white pixels (filter=0, then R G B for each pixel)
        // Raw data per row: [filter_byte=0] [R G B] [R G B]
        var rawScanlines = new byte[]
        {
            0, 255, 255, 255, 255, 255, 255,  // row 0
            0, 255, 255, 255, 255, 255, 255,  // row 1
        };
        WritePngChunk(ms, "IDAT", ZlibCompressPng(rawScanlines));
        WritePngChunk(ms, "IEND", []);

        return ms.ToArray();
    }

    private static void WritePngChunk(MemoryStream s, string type, byte[] data)
    {
        // Length (big-endian u32)
        s.WriteByte((byte)(data.Length >> 24));
        s.WriteByte((byte)(data.Length >> 16));
        s.WriteByte((byte)(data.Length >> 8));
        s.WriteByte((byte)(data.Length));
        // Type (4 ASCII bytes)
        foreach (var c in type) s.WriteByte((byte)c);
        // Data
        s.Write(data);
        // CRC32 over type + data
        var crcInput = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) crcInput[i] = (byte)type[i];
        data.CopyTo(crcInput, 4);
        var crc = ComputePngCrc32(crcInput);
        s.WriteByte((byte)(crc >> 24));
        s.WriteByte((byte)((crc >> 16) & 0xFF));
        s.WriteByte((byte)((crc >> 8) & 0xFF));
        s.WriteByte((byte)(crc & 0xFF));
    }

    private static byte[] ZlibCompressPng(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest))
            z.Write(data);
        return output.ToArray();
    }

    private static uint ComputePngCrc32(byte[] data)
    {
        // Standard CRC-32 used by PNG (polynomial 0xEDB88320, reflected)
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}

/// <summary>
/// Layout-layer tests for the document language channel (#37):
/// document /Lang in catalog, dc:language in XMP, and per-element /Lang on struct elements.
/// </summary>
public sealed class LanguageChannelTests
{
    private static string SaveToString(Document doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    // ── Document.Language → catalog /Lang ────────────────────────────────────

    [Fact]
    public void Document_Language_set_emitsCatalogLang()
    {
        using var doc = new Document();
        doc.Language = "en-US";
        doc.Add(new Paragraph("Hello"));

        var content = SaveToString(doc);

        Assert.Contains("/Lang", content);
        Assert.Contains("en-US", content);
    }

    [Fact]
    public void Document_Language_null_doesNotEmitCatalogLang()
    {
        using var doc = new Document();
        doc.Language = null;
        doc.Add(new Paragraph("Hello"));

        var content = SaveToString(doc);

        Assert.DoesNotContain("/Lang", content);
    }

    // ── Document.Language → XMP dc:language ──────────────────────────────────

    [Fact]
    public void Document_Language_set_emitsDcLanguageInXmp()
    {
        using var doc = new Document();
        doc.Language = "en-GB";
        doc.Add(new Paragraph("Hello"));

        var content = SaveToString(doc);

        Assert.Contains("dc:language", content);
        Assert.Contains("en-GB", content);
    }

    [Fact]
    public void Document_Language_null_doesNotEmitDcLanguageInXmp()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Hello"));

        var content = SaveToString(doc);

        Assert.DoesNotContain("dc:language", content);
    }

    // ── Paragraph.Language → per-element struct elem /Lang ───────────────────

    [Fact]
    public void Paragraph_Language_set_emitsStructElemLang()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Paragraph("Bonjour") { Language = "fr-CA" });

        var content = SaveToString(doc);

        Assert.Contains("/Lang", content);
        Assert.Contains("fr-CA", content);
    }

    [Fact]
    public void Paragraph_Language_null_doesNotEmitStructElemLang()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Paragraph("Hello") { Language = null });

        var content = SaveToString(doc);

        // No document or element language set — /Lang should be absent
        Assert.DoesNotContain("/Lang", content);
    }

    // ── Heading.Language → per-element struct elem /Lang ─────────────────────

    [Fact]
    public void Heading_Language_set_emitsStructElemLang()
    {
        using var doc = new Document();
        doc.Tagged = true;
        doc.Add(new Heading("Hola") { Level = 0, Language = "es-MX" });

        var content = SaveToString(doc);

        Assert.Contains("/Lang", content);
        Assert.Contains("es-MX", content);
    }

    // ── Language roundtrip on Document ───────────────────────────────────────

    [Fact]
    public void Document_Language_property_roundtrips()
    {
        using var doc = new Document();
        doc.Language = "zh-CN";
        Assert.Equal("zh-CN", doc.Language);
    }

    // ── Dual-level precedence: document /Lang + element /Lang ────────────────

    [Fact]
    public void DualLevel_DocumentLanguage_and_ParagraphLanguage_bothPresent()
    {
        // Document-level default and per-element override must both be written.
        using var doc = new Document();
        doc.Tagged = true;
        doc.Language = "en-US";
        doc.Add(new Paragraph("Bonjour") { Language = "fr-CA" });

        var content = SaveToString(doc);

        Assert.Contains("en-US", content);
        Assert.Contains("fr-CA", content);
    }

    // ── ListItem.Language → per-element struct elem /Lang ────────────────────

    [Fact]
    public void ListItem_Language_set_emitsStructElemLang()
    {
        using var doc = new Document();
        doc.Tagged = true;

        var list = new ListElement(ListStyle.Unordered);
        list.Add(new ListItem("Hallo") { Language = "de-DE" });
        doc.Add(list);

        var content = SaveToString(doc);

        Assert.Contains("/Lang", content);
        Assert.Contains("de-DE", content);
    }

    // ── TableCell.Language → per-element struct elem /Lang ───────────────────

    [Fact]
    public void TableCell_Language_set_emitsStructElemLang()
    {
        using var doc = new Document();
        doc.Tagged = true;

        var table = new TableElement();
        table.SetColumnWidths(200, 200);
        var row = table.AddRow();
        row.AddCell(new Cell("") { Language = "ja-JP" });
        row.AddCell("Other");
        doc.Add(table);

        var content = SaveToString(doc);

        Assert.Contains("/Lang", content);
        Assert.Contains("ja-JP", content);
    }
}
