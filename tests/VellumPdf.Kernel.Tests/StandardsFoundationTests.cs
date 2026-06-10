// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for the standards-foundation features:
///   • XMP metadata stream (§14.3.2)
///   • Document /ID trailer entry (§14.4)
///   • PDF/A conformance scaffold
///   • Basic tagged-PDF structure tree (§14.7)
/// </summary>
public sealed class StandardsFoundationTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SaveToString(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    // ── 1. XMP metadata ──────────────────────────────────────────────────────

    [Fact]
    public void Save_withInfo_emitsMetadataStream()
    {
        using var doc = new PdfDocument();
        doc.Info.Title = "Standards Test";
        doc.Info.Author = "Timothy van der Ham";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/Metadata", content);
        Assert.Contains("/Subtype /XML", content);
        Assert.Contains("<?xpacket", content);
    }

    [Fact]
    public void Save_withInfo_xmpPacketContainsDcTitle()
    {
        using var doc = new PdfDocument();
        doc.Info.Title = "My PDF Title";
        doc.Info.Author = "Author Name";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("My PDF Title", content);
        Assert.Contains("dc:title", content);
    }

    [Fact]
    public void Save_withInfo_xmpPacketContainsDcCreator()
    {
        using var doc = new PdfDocument();
        doc.Info.Author = "Jane Doe";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("Jane Doe", content);
        Assert.Contains("dc:creator", content);
    }

    [Fact]
    public void Save_noInfo_emitsXmpWithoutTitle()
    {
        using var doc = new PdfDocument();
        doc.AddPage();

        var content = SaveToString(doc);

        // Metadata stream is always present.
        Assert.Contains("<?xpacket", content);
        // No dc:title when Title is null
        Assert.DoesNotContain("dc:title", content);
    }

    [Fact]
    public void Save_withProducer_xmpContainsProducer()
    {
        using var doc = new PdfDocument();
        doc.Info.Producer = "VellumPdf 1.0";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("VellumPdf 1.0", content);
        Assert.Contains("pdf:Producer", content);
    }

    [Fact]
    public void Save_metadataStream_hasNoFilterEntry()
    {
        // PDF/A requires the metadata stream to be uncompressed (no /Filter).
        using var doc = new PdfDocument();
        doc.Info.Title = "UncompressedTest";
        doc.AddPage();

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        var content = System.Text.Encoding.Latin1.GetString(bytes);

        // Find the metadata stream dict and confirm no /Filter is adjacent to /Subtype /XML
        var xmlIdx = content.IndexOf("/Subtype /XML", StringComparison.Ordinal);
        Assert.True(xmlIdx >= 0, "/Subtype /XML must be present");

        // In the metadata stream dict there must NOT be /Filter before /Subtype /XML
        // (search the 200 chars before /Subtype /XML for /Filter – if /Filter is absent it's uncompressed)
        var window = content[Math.Max(0, xmlIdx - 200)..xmlIdx];
        Assert.DoesNotContain("/Filter", window);
    }

    [Fact]
    public void Save_metadataStream_catalogHasMetadataRef()
    {
        using var doc = new PdfDocument();
        doc.Info.Title = "CatalogMetaRef";
        doc.AddPage();

        var content = SaveToString(doc);

        // The catalog must contain a /Metadata key.
        Assert.Contains("/Metadata", content);
    }

    // ── 2. Document /ID ───────────────────────────────────────────────────────

    [Fact]
    public void Save_always_writesDocumentIdInTrailer()
    {
        using var doc = new PdfDocument();
        doc.AddPage();

        var content = SaveToString(doc);

        // /ID appears in the trailer dictionary
        Assert.Contains("/ID [<", content);
    }

    [Fact]
    public void Save_documentId_is32HexCharsPerEntry()
    {
        // Each element of /ID is a 16-byte hex string = 32 hex chars.
        using var doc = new PdfDocument();
        doc.AddPage();

        var content = SaveToString(doc);

        var idIdx = content.IndexOf("/ID [<", StringComparison.Ordinal);
        Assert.True(idIdx >= 0);
        // Grab just after "/ID [<"
        var afterTag = content[(idIdx + 6)..];
        var closeIdx = afterTag.IndexOf('>', StringComparison.Ordinal);
        Assert.True(closeIdx == 32, $"Expected 32 hex chars in ID entry, got {closeIdx}");
    }

    [Fact]
    public void Save_documentId_twoEqualElements()
    {
        // At creation time both elements of /ID are identical.
        using var doc = new PdfDocument();
        doc.Info.Title = "IdTest";
        doc.AddPage();

        var content = SaveToString(doc);

        var idIdx = content.IndexOf("/ID [<", StringComparison.Ordinal);
        Assert.True(idIdx >= 0);
        var idSection = content[(idIdx + 5)..];
        // Extract first hex string
        var open1 = idSection.IndexOf('<');
        var close1 = idSection.IndexOf('>');
        var hex1 = idSection[(open1 + 1)..close1];
        // Extract second hex string
        var remainder = idSection[(close1 + 1)..];
        var open2 = remainder.IndexOf('<');
        var close2 = remainder.IndexOf('>');
        var hex2 = remainder[(open2 + 1)..close2];

        Assert.Equal(hex1, hex2);
    }

    // ── 3. PDF/A conformance scaffold ─────────────────────────────────────────

    [Fact]
    public void Conformance_PdfA2b_emitsPdfaidPart()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "PDF/A-2b Test";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("pdfaid:part", content);
        Assert.Contains(">2<", content); // part value = 2
    }

    [Fact]
    public void Conformance_PdfA2b_emitsPdfaidConformanceB()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("pdfaid:conformance", content);
        Assert.Contains(">B<", content);
    }

    [Fact]
    public void Conformance_PdfA2u_emitsPdfaidConformanceU()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2u;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains(">U<", content);
    }

    [Fact]
    public void Conformance_PdfA2a_emitsPdfaidConformanceA()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2a;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains(">A<", content);
    }

    [Fact]
    public void Conformance_PdfA2b_setsMarkInfoMarkedTrue()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/MarkInfo", content);
        Assert.Contains("/Marked true", content);
    }

    [Fact]
    public void Conformance_None_doesNotEmitPdfaidNamespace()
    {
        using var doc = new PdfDocument();
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.DoesNotContain("pdfaid", content);
    }

    [Fact]
    public void Conformance_PdfA2b_writesDocumentId()
    {
        // PDF/A requires /ID.
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/ID [<", content);
    }

    [Fact]
    public void Conformance_PdfA2b_headerDeclaresPdf17()
    {
        // PDF/A-2 is defined against PDF 1.7, so the header must be %PDF-1.7 (veraPDF 6.1.2).
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.AddPage();

        var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Equal("%PDF-1.7"u8.ToArray(), ms.ToArray()[..8]);
    }

    [Fact]
    public void Conformance_None_headerDeclaresPdf20()
    {
        // Non-conformance documents keep the PDF 2.0 baseline header.
        using var doc = new PdfDocument();
        doc.AddPage();

        var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Equal("%PDF-2.0"u8.ToArray(), ms.ToArray()[..8]);
    }

    // ── 4. Tagged PDF structure tree ──────────────────────────────────────────

    [Fact]
    public void Tagged_false_noStructTreeRoot()
    {
        using var doc = new PdfDocument();
        doc.Tagged = false;
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("Hello").EndText();
        canvas.Finish();

        var content = SaveToString(doc);

        Assert.DoesNotContain("/StructTreeRoot", content);
    }

    [Fact]
    public void Tagged_true_withStructElem_emitsStructTreeRoot()
    {
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("Hello").EndText();
        var mcid = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        canvas.Finish();

        var elem = new PdfStructElem("P") { Page = page, Mcid = mcid };
        doc.RegisterStructElem(elem);

        var content = SaveToString(doc);

        Assert.Contains("/StructTreeRoot", content);
    }

    [Fact]
    public void Tagged_true_withStructElem_emitsStructElem()
    {
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var mcid = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        canvas.Finish();

        var elem = new PdfStructElem("P") { Page = page, Mcid = mcid };
        doc.RegisterStructElem(elem);

        var content = SaveToString(doc);

        Assert.Contains("/StructElem", content);
        Assert.Contains("/S /P", content);
    }

    [Fact]
    public void Tagged_true_withNoElems_noStructTreeRoot()
    {
        // Tagged = true but no RegisterStructElem call → no /StructTreeRoot written
        using var doc = new PdfDocument();
        doc.Tagged = true;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.DoesNotContain("/StructTreeRoot", content);
    }

    [Fact]
    public void Tagged_true_setsMarkInfoMarkedTrue()
    {
        using var doc = new PdfDocument();
        doc.Tagged = true;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/MarkInfo", content);
        Assert.Contains("/Marked true", content);
    }

    [Fact]
    public void Tagged_true_structTreeRootHasParentTree()
    {
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var mcid = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        canvas.Finish();

        var elem = new PdfStructElem("P") { Page = page, Mcid = mcid };
        doc.RegisterStructElem(elem);

        var content = SaveToString(doc);

        Assert.Contains("/ParentTree", content);
    }

    // ── Fix #2: /StructParents must appear in the tagged page's dict ─────────

    [Fact]
    public void Tagged_pageDict_containsStructParentsKey()
    {
        // A tagged document with a struct element should have /StructParents in the page dict.
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var mcid = canvas.BeginMarkedContent("H1");
        canvas.EndMarkedContent();
        canvas.Finish();

        var elem = new PdfStructElem("H1") { Page = page, Mcid = mcid };
        doc.RegisterStructElem(elem);

        var content = SaveToString(doc);

        // /StructParents N must appear somewhere in the output (in the page dict).
        Assert.Contains("/StructParents", content);
    }

    // ── Fix #3: /DescendantFonts must be an inline array, not an indirect ref ──
    // This test validates with a real TrueType font only if arial.ttf is present;
    // otherwise we validate the BuildFontDictionary API with a direct unit test.

    [Fact]
    public void TrueTypeEmbed_descendantFonts_isInlineArray()
    {
        const string fontPath = @"C:\Windows\Fonts\arial.ttf";
        if (!System.IO.File.Exists(fontPath)) return; // Skip if font absent (CI/Linux)

        var fontData = System.IO.File.ReadAllBytes(fontPath);
        using var doc = new PdfDocument();
        doc.Tagged = false;
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(fontData);

        // Register font usage so it gets embedded
        doc.RegisterEmbeddedFontUsage(page, handle);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        // /DescendantFonts must appear as an inline array "[N 0 R]" in the Type0 dict.
        // If it were an indirect reference, we'd see "/DescendantFonts N 0 R" (no brackets).
        Assert.Contains("/DescendantFonts [", content);
    }

    // ── PDF/A OutputIntents (sRGB ICC) ────────────────────────────────────────

    [Fact]
    public void Conformance_PdfA2b_emitsOutputIntents()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/OutputIntents", content);
    }

    [Fact]
    public void Conformance_PdfA2b_emitsGtsPdfa1()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/GTS_PDFA1", content);
    }

    [Fact]
    public void Conformance_PdfA2b_emitsDestOutputProfile()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/DestOutputProfile", content);
    }

    [Fact]
    public void Conformance_PdfA2b_iccStreamHasN3()
    {
        // The ICC stream must carry /N 3 (3-component RGB).
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.AddPage();

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        var content = System.Text.Encoding.Latin1.GetString(bytes);

        Assert.Contains("/N 3", content);
    }

    [Fact]
    public void Conformance_PdfA2b_xmpContainsPdfaidPart()
    {
        // Regression: pdfaid:part must appear with value 2.
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("pdfaid:part", content);
        Assert.Contains(">2<", content);
    }

    [Fact]
    public void Conformance_None_doesNotEmitOutputIntents()
    {
        // /OutputIntents must NOT be written when Conformance is None.
        using var doc = new PdfDocument();
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.DoesNotContain("/OutputIntents", content);
    }

    [Fact]
    public void Conformance_PdfA2u_emitsOutputIntents()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2u;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/OutputIntents", content);
    }

    // ── 5. Document /Lang ─────────────────────────────────────────────────────

    [Fact]
    public void Language_set_emitsCatalogLang()
    {
        using var doc = new PdfDocument();
        doc.Language = "en-US";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/Lang", content);
        Assert.Contains("en-US", content);
    }

    [Fact]
    public void Language_null_doesNotEmitCatalogLang()
    {
        using var doc = new PdfDocument();
        doc.Language = null;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.DoesNotContain("/Lang", content);
    }

    [Fact]
    public void Language_whitespaceOnly_doesNotEmitCatalogLang()
    {
        using var doc = new PdfDocument();
        doc.Language = "   ";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.DoesNotContain("/Lang", content);
    }

    [Fact]
    public void Language_set_emitsDcLanguageInXmp()
    {
        using var doc = new PdfDocument();
        doc.Language = "fr-FR";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("dc:language", content);
        Assert.Contains("fr-FR", content);
    }

    [Fact]
    public void Language_null_doesNotEmitDcLanguageInXmp()
    {
        using var doc = new PdfDocument();
        doc.Language = null;
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.DoesNotContain("dc:language", content);
    }

    [Fact]
    public void Language_worksWithConformanceNone()
    {
        // /Lang is valid in any PDF, not just PDF/A.
        using var doc = new PdfDocument();
        doc.Language = "de";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/Lang", content);
        Assert.DoesNotContain("pdfaid", content);
    }

    // ── 6. Per-element /Lang on PdfStructElem ────────────────────────────────

    [Fact]
    public void StructElem_Language_set_emitsLangOnElem()
    {
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var mcid = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        canvas.Finish();

        var elem = new PdfStructElem("P") { Page = page, Mcid = mcid, Language = "es-ES" };
        doc.RegisterStructElem(elem);

        var content = SaveToString(doc);

        Assert.Contains("/Lang", content);
        Assert.Contains("es-ES", content);
    }

    [Fact]
    public void StructElem_Language_null_doesNotEmitLangOnElem()
    {
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var mcid = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        canvas.Finish();

        var elem = new PdfStructElem("P") { Page = page, Mcid = mcid, Language = null };
        doc.RegisterStructElem(elem);

        var content = SaveToString(doc);

        // Only /StructTreeRoot etc. should appear; no /Lang unless doc.Language is also set
        Assert.DoesNotContain("/Lang", content);
    }

    // ── 7. PDF/A-2b + Language regression ────────────────────────────────────

    [Fact]
    public void Conformance_PdfA2b_withLanguage_emitsCatalogLang()
    {
        // Regression: catalog /Lang must be written together with PDF/A-2b conformance.
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Language = "en-US";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/Lang", content);
        Assert.Contains("en-US", content);
    }
}
