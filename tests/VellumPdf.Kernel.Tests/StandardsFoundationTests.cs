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

    /// <summary>
    /// Saves and returns the raw PDF text concatenated with all decompressed
    /// FlateDecode stream data, so tests can assert on content-stream operators
    /// that are compressed in the output.
    /// </summary>
    private static string SaveToStringWithDecompressed(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        var raw = System.Text.Encoding.Latin1.GetString(bytes);
        var sb = new System.Text.StringBuilder(raw);
        // Decompress each FlateDecode stream and append.
        var pos = 0;
        while (pos < bytes.Length)
        {
            var streamKeyword = raw.IndexOf("\nstream\n", pos, StringComparison.Ordinal);
            if (streamKeyword < 0) break;
            var dataStart = streamKeyword + "\nstream\n".Length;
            var dictEnd = streamKeyword;
            var dictStart = raw.LastIndexOf("obj\n", dictEnd, StringComparison.Ordinal);
            if (dictStart < 0) { pos = dataStart; continue; }
            var lenIdx = raw.IndexOf("/Length ", dictStart, dictEnd - dictStart, StringComparison.Ordinal);
            if (lenIdx < 0) { pos = dataStart; continue; }
            var lenValStart = lenIdx + "/Length ".Length;
            var lenValEnd = lenValStart;
            while (lenValEnd < raw.Length && char.IsDigit(raw[lenValEnd])) lenValEnd++;
            if (!int.TryParse(raw[lenValStart..lenValEnd], out var streamLength)) { pos = dataStart; continue; }
            if (dataStart + streamLength > bytes.Length) { pos = dataStart; continue; }
            var rawBytes = bytes[dataStart..(dataStart + streamLength)];
            try
            {
                using var input = new MemoryStream(rawBytes);
                using var output = new MemoryStream();
                using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
                z.CopyTo(output);
                sb.Append(System.Text.Encoding.Latin1.GetString(output.ToArray()));
            }
            catch (InvalidDataException) { /* not a zlib stream — skip */ }
            pos = dataStart + streamLength;
        }
        return sb.ToString();
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
    public void Save_xmpBeginAttribute_isCanonicalUtf8Bom()
    {
        using var doc = new PdfDocument();
        doc.Info.Title = "BomTest";
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        // The xpacket begin attribute must hold the single character U+FEFF, which UTF-8 encodes to
        // the canonical 3-byte BOM EF BB BF — not the 6-byte mojibake (C3 AF C2 BB C2 BF) a literal
        // "\xEF\xBB\xBF" string would have produced.
        var marker = System.Text.Encoding.ASCII.GetBytes("<?xpacket begin=\"");
        var idx = IndexOf(bytes, marker);
        Assert.True(idx >= 0, "xpacket begin attribute not found");

        var valueStart = idx + marker.Length;
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'"' }, bytes[valueStart..(valueStart + 4)]);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
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

    // ── 8. No /RoleMap for standard types + MCID-ordered /ParentTree (issue #38) ─

    [Fact]
    public void Tagged_structTree_standardTypes_omitsRoleMap()
    {
        // A tagged doc using only standard structure types (P, H1, Figure) must NOT
        // emit /RoleMap. Mapping a standard type to itself is a circular mapping that
        // violates ISO 14289-1 (PDF/UA-1) clause 7.1. Standard types need no role mapping.
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        var mcid0 = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        var mcid1 = canvas.BeginMarkedContent("H1");
        canvas.EndMarkedContent();
        var mcid2 = canvas.BeginMarkedContent("Figure");
        canvas.EndMarkedContent();
        canvas.Finish();

        doc.RegisterStructElem(new PdfStructElem("P") { Page = page, Mcid = mcid0 });
        doc.RegisterStructElem(new PdfStructElem("H1") { Page = page, Mcid = mcid1 });
        doc.RegisterStructElem(new PdfStructElem("Figure") { Page = page, Mcid = mcid2 });

        var content = SaveToString(doc);

        Assert.DoesNotContain("/RoleMap", content);
    }

    [Fact]
    public void Tagged_thStructElem_emitsTableScope()
    {
        // A TH struct elem with TableHeaderScope = "Column" must emit
        // /A << /O /Table /Scope /Column >> (ISO 14289-1 clause 7.5).
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var mcid = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        canvas.Finish();

        var pElem = new PdfStructElem("P") { Page = page, Mcid = mcid };
        var thElem = new PdfStructElem("TH") { Page = page, TableHeaderScope = "Column" };
        thElem.AddChild(pElem);
        doc.RegisterStructElem(thElem);

        var content = SaveToString(doc);

        Assert.Contains("/O /Table", content);
        Assert.Contains("/Scope /Column", content);
    }

    [Fact]
    public void Tagged_structTree_parentTreePresentWithMultipleElems()
    {
        // A tagged doc with 3 leaf elems on a page → /ParentTree and /Nums are present.
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        var mcid0 = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        var mcid1 = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        var mcid2 = canvas.BeginMarkedContent("H1");
        canvas.EndMarkedContent();
        canvas.Finish();

        doc.RegisterStructElem(new PdfStructElem("P") { Page = page, Mcid = mcid0 });
        doc.RegisterStructElem(new PdfStructElem("P") { Page = page, Mcid = mcid1 });
        doc.RegisterStructElem(new PdfStructElem("H1") { Page = page, Mcid = mcid2 });

        var content = SaveToString(doc);

        Assert.Contains("/ParentTree", content);
        Assert.Contains("/Nums", content);
    }

    [Fact]
    public void Tagged_duplicateMcidOnPage_throwsInvalidOperationException()
    {
        // Two struct elems on the same page both claiming MCID 0 → Save must throw.
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        // Emit two BDC/EMC sequences; we'll lie and assign both struct elems MCID 0.
        var mcid0 = canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        canvas.BeginMarkedContent("P"); // returns mcid 1, but we override below
        canvas.EndMarkedContent();
        canvas.Finish();

        // Register both with Mcid = 0 to trigger the duplicate guard.
        doc.RegisterStructElem(new PdfStructElem("P") { Page = page, Mcid = 0 });
        doc.RegisterStructElem(new PdfStructElem("P") { Page = page, Mcid = 0 });

        var ms = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => { doc.Save(ms); });
    }

    [Fact]
    public void Tagged_sparseMcidOnPage_producesSparseParentTree_noThrow()
    {
        // One leaf elem on a page with Mcid = 5 (no elems at 0-4). Since v1.5.5 (#83) the
        // per-page ParentTree is sized by max(MCID)+1 = 6 with null holes for the gap, rather
        // than sized by leaf count and throwing. A sparse number-tree array is valid PDF.
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.BeginMarkedContent("P");
        canvas.EndMarkedContent();
        canvas.Finish();

        doc.RegisterStructElem(new PdfStructElem("P") { Page = page, Mcid = 5 });

        var ms = new MemoryStream();
        var ex = Record.Exception(() => doc.Save(ms));
        Assert.Null(ex); // must not throw on a non-contiguous / sparse MCID
        Assert.Contains("/ParentTree", System.Text.Encoding.Latin1.GetString(ms.ToArray()));
    }

    // ── 9. PDF/UA-1 conformance ───────────────────────────────────────────────

    [Fact]
    public void Conformance_PdfUA1_emitsViewerPreferencesDisplayDocTitle()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfUA1;
        doc.Info.Title = "UA-1 Test";
        doc.Language = "en-US";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("/ViewerPreferences", content);
        Assert.Contains("/DisplayDocTitle true", content);
    }

    [Fact]
    public void Conformance_PdfUA1_emitsPdfUaIdPart_notPdfaId()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfUA1;
        doc.Info.Title = "UA-1 XMP Test";
        doc.AddPage();

        var content = SaveToString(doc);

        Assert.Contains("pdfuaid", content);
        Assert.Contains("<pdfuaid:part>1", content);
        Assert.DoesNotContain("pdfaid:", content);
    }

    [Fact]
    public void Conformance_PdfUA1_impliesTagged()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfUA1;

        Assert.True(doc.Tagged);
    }

    [Fact]
    public void Canvas_BeginArtifactMarkedContent_emitsArtifactBmc()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.BeginArtifactMarkedContent();
        canvas
            .SetStrokeColorRgb(0, 0, 0)
            .MoveTo(10, 10)
            .LineTo(100, 10)
            .Stroke();
        canvas.EndMarkedContent();
        canvas.Finish();

        // Content stream is FlateDecode-compressed; use the decompressed helper.
        var content = SaveToStringWithDecompressed(doc);

        Assert.Contains("/Artifact BMC", content);
        Assert.Contains("EMC", content);
    }
}
