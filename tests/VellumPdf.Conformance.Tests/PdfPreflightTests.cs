// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Conformance;
using VellumPdf.Document;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Tests;

public sealed class PdfPreflightTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal PDF/A-2b document. The writer emits the %PDF-1.7 header, the binary
    /// marker comment, a trailer /ID, and a /Type /Catalog — i.e. everything the §6.1 file-structure
    /// rules require — so the result is compliant against the rules implemented so far.
    /// </summary>
    private static byte[] BuildOnePagePdf()
    {
        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfA2b };
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A plain (non-PDF/A) document. The writer emits a %PDF-2.0 header, which is not
    /// valid PDF/A-2 (§6.1.2).</summary>
    private static byte[] BuildPlainPdf20()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// A catalog (object 1) that is a dictionary but is missing the required <c>/Type /Catalog</c>
    /// entry; everything else (header, marker, /ID, XMP) is valid, so the catalog-type rule is the
    /// only violation.
    /// </summary>
    private static byte[] BuildCatalogMissingTypePdf()
        => AssemblePdf(
        [
            new("<< /Pages 2 0 R >>"),
            new("<< /Type /Pages /Kids [] /Count 0 >>"),
        ]);

    /// <summary>
    /// A structurally valid catalog, with the §6.1.2 binary marker and the §6.1.3 trailer
    /// <c>/ID</c> toggleable so each file-structure rule can be exercised in isolation.
    /// </summary>
    private static byte[] BuildMinimalPdf(bool binaryMarker, bool trailerId)
        => AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R >>"),
                new("<< /Type /Pages /Kids [] /Count 0 >>"),
            ],
            binaryMarker: binaryMarker,
            trailerId: trailerId);

    /// <summary>
    /// A single indirect object for <see cref="AssemblePdf"/>. For a non-stream object,
    /// <see cref="Dict"/> is the complete object text (e.g. <c>"&lt;&lt; /Type /Catalog &gt;&gt;"</c>).
    /// For a stream object, <see cref="Dict"/> is the dictionary's inner entries only (e.g.
    /// <c>"/N 3"</c>); the assembler wraps it and appends the correct <c>/Length</c>.
    /// </summary>
    private sealed record PdfObj(string Dict, byte[]? Stream = null);

    /// <summary>
    /// Assembles a classic-xref PDF/A-shaped file from a 1-indexed object list (object 1 is the
    /// document catalog). By default it satisfies every always-on structural rule — the %PDF-1.7
    /// header, binary marker, trailer <c>/ID</c>, and a conforming XMP <c>/Metadata</c> stream —
    /// so a fixture trips only the rule it is built to violate. Each flag drops the corresponding
    /// element so a single structural rule can be exercised in isolation.
    /// </summary>
    private static byte[] AssemblePdf(
        IReadOnlyList<PdfObj> objects,
        bool binaryMarker = true,
        bool trailerId = true,
        bool metadata = true,
        string xmpConformance = "B",
        byte[]? metadataOverride = null)
    {
        var all = new List<PdfObj>(objects);

        // Append an XMP metadata stream and reference it from the catalog (object 1).
        if (metadata)
        {
            var metaObjNum = all.Count + 1;
            var xmp = metadataOverride ?? XmpBytes("2", xmpConformance);
            all.Add(new PdfObj("/Type /Metadata /Subtype /XML", xmp));
            all[0] = all[0] with { Dict = InjectIntoDict(all[0].Dict, $"/Metadata {metaObjNum} 0 R") };
        }

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        if (binaryMarker)
            ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        var offsets = new int[all.Count + 1];
        for (var i = 0; i < all.Count; i++)
        {
            offsets[i + 1] = (int)ms.Position;
            var n = i + 1;
            if (all[i].Stream is { } body)
            {
                W($"{n} 0 obj\n<< {all[i].Dict} /Length {body.Length} >>\nstream\n");
                ms.Write(body);
                W("\nendstream\nendobj\n");
            }
            else
            {
                W($"{n} 0 obj\n{all[i].Dict}\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        var size = all.Count + 1;
        W($"xref\n0 {size}\n");
        W($"{0:D10} 65535 f \n");
        for (var i = 1; i <= all.Count; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        var id = trailerId
            ? " /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]"
            : string.Empty;
        W($"trailer\n<< /Size {size} /Root 1 0 R{id} >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>Inserts <paramref name="entry"/> into a <c>&lt;&lt; … &gt;&gt;</c> dictionary literal, before the closing delimiter.</summary>
    private static string InjectIntoDict(string dict, string entry)
    {
        var i = dict.LastIndexOf(">>", StringComparison.Ordinal);
        return i < 0 ? dict : string.Concat(dict[..i], entry, " ", dict[i..]);
    }

    /// <summary>A minimal conforming XMP packet carrying the given pdfaid part and conformance.</summary>
    private static byte[] XmpBytes(string part, string conformance)
    {
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + $"<pdfaid:part>{part}</pdfaid:part>"
            + $"<pdfaid:conformance>{conformance}</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }

    /// <summary>A minimal ICC profile body: zero-filled, with the 'acsp' signature at offset 36.</summary>
    private static byte[] FakeIccProfile()
    {
        var icc = new byte[128];
        icc[36] = (byte)'a';
        icc[37] = (byte)'c';
        icc[38] = (byte)'s';
        icc[39] = (byte)'p';
        return icc;
    }

    private static readonly PdfObj _pagesObj = new("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    private static readonly PdfObj _pageObj = new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

    /// <summary>Builds a doc whose catalog /OutputIntents references the given extra object bodies.</summary>
    private static byte[] BuildOutputIntentPdf(string catalogIntents, params PdfObj[] extra)
    {
        var objects = new List<PdfObj>
        {
            new($"<< /Type /Catalog /Pages 2 0 R /OutputIntents {catalogIntents} >>"),
            _pagesObj,
            _pageObj,
        };
        objects.AddRange(extra);
        return AssemblePdf(objects);
    }

    /// <summary>Builds a one-page doc whose /Resources /ExtGState /GS0 has the given /BM body.</summary>
    private static byte[] BuildBlendModePdf(string bmEntry)
    {
        return AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R >>"),
            new("<< /ExtGState 5 0 R >>"),
            new("<< /GS0 6 0 R >>"),
            new($"<< /Type /ExtGState /BM {bmEntry} >>"),
        ]);
    }

    /// <summary>
    /// Builds a one-page doc whose /Resources /Font /F0 references object 6, with
    /// <paramref name="fontObjects"/> supplying objects 6..N (the font dict and any descriptor /
    /// font-program streams it points to).
    /// </summary>
    private static byte[] BuildFontPdf(params PdfObj[] fontObjects)
    {
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R >>"),
            new("<< /Font 5 0 R >>"),
            new("<< /F0 6 0 R >>"),
        };
        objects.AddRange(fontObjects);
        return AssemblePdf(objects);
    }

    /// <summary>
    /// Builds a one-page doc where the page dictionary carries <paramref name="pageExtra"/> (e.g.
    /// <c>"/Annots [4 0 R]"</c>) and <paramref name="extra"/> supplies objects 4..N.
    /// </summary>
    private static byte[] BuildPagePdf(string pageExtra, params PdfObj[] extra)
    {
        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {pageExtra} >>"),
        };
        objects.AddRange(extra);
        return AssemblePdf(objects);
    }

    /// <summary>Builds a one-page doc with a single embedded Identity-H Type0 font, optionally with /ToUnicode.</summary>
    private static byte[] BuildType0FontPdf(bool withToUnicode, string xmpConformance)
    {
        var fontDict = withToUnicode
            ? "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] /ToUnicode 10 0 R >>"
            : "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [7 0 R] >>";

        var objects = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources 4 0 R >>"),
            new("<< /Font 5 0 R >>"),
            new("<< /F0 6 0 R >>"),
            new(fontDict),
            new("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /FontDescriptor 8 0 R >>"),
            new("<< /Type /FontDescriptor /FontName /X /FontFile2 9 0 R >>"),
            new("/Length1 4", [1, 2, 3, 4]),
        };
        if (withToUnicode)
            objects.Add(new PdfObj("/Type /CMap", Encoding.ASCII.GetBytes("/CIDInit")));

        return AssemblePdf(objects, xmpConformance: xmpConformance);
    }

    /// <summary>A minimal PDF/UA-1 XMP packet (pdfuaid:part + optional dc:title).</summary>
    private static byte[] UaXmpBytes(string part = "1", bool withTitle = true)
    {
        var title = withTitle
            ? "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Title</rdf:li></rdf:Alt></dc:title>"
            : string.Empty;
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" "
            + "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + $"<pdfuaid:part>{part}</pdfuaid:part>"
            + title
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }

    /// <summary>
    /// Builds a PDF/UA-1 fixture that is compliant by default; each flag drops one requirement so a
    /// single UA rule can be exercised in isolation. Object 4 is always a /StructTreeRoot.
    /// </summary>
    private static byte[] BuildUaPdf(
        bool lang = true,
        bool marked = true,
        bool structTreeRoot = true,
        bool displayDocTitle = true,
        byte[]? xmpOverride = null,
        string pageExtra = "")
    {
        var catalog = "<< /Type /Catalog /Pages 2 0 R"
            + (lang ? " /Lang (en-US)" : string.Empty)
            + (marked ? " /MarkInfo << /Marked true >>" : " /MarkInfo << /Marked false >>")
            + (structTreeRoot ? " /StructTreeRoot 4 0 R" : string.Empty)
            + (displayDocTitle
                ? " /ViewerPreferences << /DisplayDocTitle true >>"
                : " /ViewerPreferences << /DisplayDocTitle false >>")
            + " >>";

        return AssemblePdf(
            [
                new(catalog),
                _pagesObj,
                new($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {pageExtra} >>"),
                new("<< /Type /StructTreeRoot >>"),
            ],
            metadataOverride: xmpOverride ?? UaXmpBytes());
    }

    /// <summary>Builds a tagged-document fixture (XMP conformance A) with the given catalog/extra objects.</summary>
    private static byte[] Build2aPdf(string catalogExtra, params PdfObj[] extra)
    {
        var objects = new List<PdfObj>
        {
            new($"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>"),
            _pagesObj,
            _pageObj,
        };
        objects.AddRange(extra);
        return AssemblePdf(objects, xmpConformance: "A");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_WellFormedDocument_IsCompliant()
    {
        var result = PdfPreflight.Validate(BuildOnePagePdf(), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
        Assert.Equal(PdfConformance.PdfA2B, result.Conformance);
    }

    [Fact]
    public void Validate_CatalogMissingType_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildCatalogMissingTypePdf(), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO32000-2:7.7.2-catalog-type", assertion.RuleId);
        Assert.Equal(PreflightSeverity.Error, assertion.Severity);
        Assert.Contains("/Catalog", assertion.Message);
    }

    [Fact]
    public void Validate_UsesOpenedReader_WithoutDisposingIt()
    {
        using var reader = PdfReader.Open(BuildOnePagePdf());

        var result = PdfPreflight.Validate(reader, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        // Reader is still usable: validation did not dispose it.
        Assert.NotNull(reader.Catalog);
    }

    [Fact]
    public void Validate_UnregisteredLevel_Throws()
    {
        // All defined levels are registered; an out-of-range value exercises the unsupported path.
        var bytes = BuildOnePagePdf();
        Assert.Throws<NotSupportedException>(() => PdfPreflight.Validate(bytes, (PdfConformance)999));
    }

    [Fact]
    public void Validate_NullBytes_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfPreflight.Validate((byte[])null!, PdfConformance.PdfA2B));
    }

    // ── §6.1 file-structure rules ──────────────────────────────────────────────

    [Fact]
    public void Validate_Pdf20Header_ReportsHeaderError()
    {
        // A plain document declares %PDF-2.0, which is not valid PDF/A-2 (§6.1.2).
        var result = PdfPreflight.Validate(BuildPlainPdf20(), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions, a => a.RuleId == "ISO19005-2:6.1.2-file-header");
    }

    [Fact]
    public void Validate_MissingBinaryMarker_ReportsError()
    {
        var bytes = BuildMinimalPdf(binaryMarker: false, trailerId: true);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.1.2-binary-marker", assertion.RuleId);
        Assert.Equal(PreflightSeverity.Error, assertion.Severity);
    }

    [Fact]
    public void Validate_MissingTrailerId_ReportsError()
    {
        var bytes = BuildMinimalPdf(binaryMarker: true, trailerId: false);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.1.3-id-present", assertion.RuleId);
        Assert.Equal("ISO 19005-2:2011, 6.1.3", assertion.Clause);
    }

    [Fact]
    public void Validate_ValidHeaderMarkerAndId_NoFileStructureFindings()
    {
        var bytes = BuildMinimalPdf(binaryMarker: true, trailerId: true);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.2.2 output-intent rules ──────────────────────────────────────────────

    [Fact]
    public void Validate_PdfAOutputIntent_MissingDestProfile_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.2-output-intent", assertion.RuleId);
        Assert.Contains("DestOutputProfile", assertion.Message);
    }

    [Fact]
    public void Validate_OutputIntent_InvalidIccProfile_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 3", Encoding.ASCII.GetBytes("this is not an ICC profile")));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.2-output-intent", assertion.RuleId);
        Assert.Contains("acsp", assertion.Message);
    }

    [Fact]
    public void Validate_OutputIntent_BadComponentCount_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 2", FakeIccProfile()));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.2-output-intent", assertion.RuleId);
        Assert.Contains("/N", assertion.Message);
    }

    [Fact]
    public void Validate_OutputIntents_DifferentProfiles_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R 6 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 3", FakeIccProfile()),
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFX /DestOutputProfile 7 0 R >>"),
            new PdfObj("/N 3", FakeIccProfile()));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions,
            a => a.RuleId == "ISO19005-2:6.2.2-output-intent" && a.Message.Contains("same ICC profile"));
    }

    [Fact]
    public void Validate_ValidOutputIntent_NoFindings()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 3", FakeIccProfile()));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.4 transparency (blend mode) rules ────────────────────────────────────

    [Theory]
    [InlineData("/Normal")]
    [InlineData("/Multiply")]
    [InlineData("/Luminosity")]
    [InlineData("[/Multiply /Screen]")]
    public void Validate_StandardBlendMode_NoFindings(string bm)
    {
        var result = PdfPreflight.Validate(BuildBlendModePdf(bm), PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_NonStandardBlendMode_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildBlendModePdf("/FooBar"), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.4-blend-mode", assertion.RuleId);
        Assert.Contains("/FooBar", assertion.Message);
    }

    [Fact]
    public void Validate_BlendModeArrayWithInvalidEntry_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildBlendModePdf("[/Multiply /Bogus]"), PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.4-blend-mode", assertion.RuleId);
        Assert.Contains("/Bogus", assertion.Message);
    }

    // ── §6.3 font embedding rules ───────────────────────────────────────────────

    [Fact]
    public void Validate_UnembeddedStandard14Font_ReportsError()
    {
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3.4-font-embedding", assertion.RuleId);
        Assert.Contains("Helvetica", assertion.Message);
    }

    [Fact]
    public void Validate_EmbeddedTrueTypeFont_NoFindings()
    {
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Arial /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /ABCDEF+Arial /FontFile2 8 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_UnembeddedType0Font_ReportsError()
    {
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /XYZ+Sub /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /XYZ+Sub /FontDescriptor 8 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /XYZ+Sub >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3.4-font-embedding", assertion.RuleId);
    }

    [Fact]
    public void Validate_EmbeddedType0Font_NoFindings()
    {
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type0 /BaseFont /XYZ+Sub /DescendantFonts [7 0 R] >>"),
            new PdfObj("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /XYZ+Sub /FontDescriptor 8 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /XYZ+Sub /FontFile2 9 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_Type3Font_IsExemptFromEmbedding()
    {
        // Type3 glyphs are content streams — no font program is expected.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /CharProcs 7 0 R >>"),
            new PdfObj("<< >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.7.2 XMP metadata rules ───────────────────────────────────────────────

    [Fact]
    public void Validate_MissingXmpMetadata_ReportsError()
    {
        var bytes = AssemblePdf([new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj], metadata: false);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.7.2-pdfaid", assertion.RuleId);
    }

    [Fact]
    public void Validate_XmpConformanceMismatch_ReportsError()
    {
        // The XMP claims conformance U but it is being validated as PDF/A-2b.
        var bytes = AssemblePdf(
            [new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj],
            xmpConformance: "U");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.7.2-pdfaid", assertion.RuleId);
        Assert.Contains("conformance", assertion.Message);
    }

    // ── §6.5.3 annotation rules ─────────────────────────────────────────────────

    [Fact]
    public void Validate_AnnotationMissingPrintFlagAndAppearance_ReportsErrors()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /F 2 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        // /F 2 sets Hidden and leaves Print clear; the /Stamp has no /AP — exactly three findings.
        Assert.False(result.IsCompliant);
        Assert.Equal(3, result.Assertions.Count);
        Assert.All(result.Assertions, a => Assert.Equal("ISO19005-2:6.5.3-annotation", a.RuleId));
        Assert.Contains(result.Assertions, a => a.Message.Contains("Print"));
        Assert.Contains(result.Assertions, a => a.Message.Contains("Hidden"));
        Assert.Contains(result.Assertions, a => a.Message.Contains("appearance"));
    }

    [Fact]
    public void Validate_WellFormedAnnotation_NoFindings()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /F 4 /AP << /N 5 0 R >> >>"),
            new PdfObj("/Subtype /Form /BBox [0 0 1 1]", []));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_LinkAnnotation_ExemptFromAppearance()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── §6.6.1 action rules ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_JavaScriptOpenAction_ReportsError()
    {
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>"),
            _pagesObj,
            _pageObj,
            new("<< /S /JavaScript /JS (noop) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.6.1-action", assertion.RuleId);
        Assert.Contains("/JavaScript", assertion.Message);
    }

    [Fact]
    public void Validate_LaunchActionOnAnnotation_ReportsError()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 /A 5 0 R >>"),
            new PdfObj("<< /S /Launch >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.6.1-action", assertion.RuleId);
        Assert.Contains("/Launch", assertion.Message);
    }

    [Fact]
    public void Validate_PermittedGoToAction_NoFindings()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 /A 5 0 R >>"),
            new PdfObj("<< /S /GoTo /D [3 0 R /Fit] >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── PDF/A-2u §6.2.11.7 ToUnicode ────────────────────────────────────────────

    [Fact]
    public void Validate2u_IdentityType0FontWithoutToUnicode_ReportsError()
    {
        var bytes = BuildType0FontPdf(withToUnicode: false, xmpConformance: "U");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2U);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.11.7-tounicode", assertion.RuleId);
    }

    [Fact]
    public void Validate2u_IdentityType0FontWithToUnicode_NoFindings()
    {
        var bytes = BuildType0FontPdf(withToUnicode: true, xmpConformance: "U");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2U);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate2b_IdentityType0FontWithoutToUnicode_IsAllowed()
    {
        // ToUnicode is a 2u requirement; the same font is acceptable at level B.
        var bytes = BuildType0FontPdf(withToUnicode: false, xmpConformance: "B");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    // ── PDF/A-2a §6.8 logical structure ─────────────────────────────────────────

    [Fact]
    public void Validate2a_TaggedDocument_NoFindings()
    {
        var bytes = Build2aPdf(
            "/MarkInfo << /Marked true >> /StructTreeRoot 4 0 R",
            new PdfObj("<< /Type /StructTreeRoot >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate2a_MissingStructTreeRoot_ReportsError()
    {
        var bytes = Build2aPdf("/MarkInfo << /Marked true >>");

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.8-logical-structure", assertion.RuleId);
        Assert.Contains("StructTreeRoot", assertion.Message);
    }

    [Fact]
    public void Validate2a_NotMarked_ReportsError()
    {
        var bytes = Build2aPdf(
            "/MarkInfo << /Marked false >> /StructTreeRoot 4 0 R",
            new PdfObj("<< /Type /StructTreeRoot >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.8-logical-structure", assertion.RuleId);
        Assert.Contains("Marked", assertion.Message);
    }

    [Fact]
    public void Validate2a_CircularRoleMap_ReportsError()
    {
        var bytes = Build2aPdf(
            "/MarkInfo << /Marked true >> /StructTreeRoot 4 0 R",
            new PdfObj("<< /Type /StructTreeRoot /RoleMap << /Foo /Bar /Bar /Foo >> >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2A);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.8-logical-structure", assertion.RuleId);
        Assert.Contains("circular", assertion.Message);
    }

    // ── PDF/UA-1 (ISO 14289-1) ──────────────────────────────────────────────────

    [Fact]
    public void ValidateUa_CompliantDocument_NoFindings()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(), PdfConformance.PdfUA1);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void ValidateUa_MissingLang_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(lang: false), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.2-lang", assertion.RuleId);
    }

    [Fact]
    public void ValidateUa_NotMarked_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(marked: false), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-tagged", assertion.RuleId);
        Assert.Contains("Marked", assertion.Message);
    }

    [Fact]
    public void ValidateUa_MissingStructTreeRoot_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(structTreeRoot: false), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-tagged", assertion.RuleId);
        Assert.Contains("StructTreeRoot", assertion.Message);
    }

    [Fact]
    public void ValidateUa_DisplayDocTitleFalse_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildUaPdf(displayDocTitle: false), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-title", assertion.RuleId);
    }

    [Fact]
    public void ValidateUa_MissingDcTitle_ReportsError()
    {
        var result = PdfPreflight.Validate(
            BuildUaPdf(xmpOverride: UaXmpBytes(withTitle: false)), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-title", assertion.RuleId);
    }

    [Fact]
    public void ValidateUa_WrongPdfuaidPart_ReportsError()
    {
        var result = PdfPreflight.Validate(
            BuildUaPdf(xmpOverride: UaXmpBytes(part: "2")), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:5-pdfuaid", assertion.RuleId);
    }

    [Fact]
    public void ValidateUa_AnnotatedPageWithoutTabsS_ReportsError()
    {
        var bytes = AssemblePdf(
            [
                new("<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                    + "/StructTreeRoot 4 0 R /ViewerPreferences << /DisplayDocTitle true >> >>"),
                _pagesObj,
                new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>"),
                new("<< /Type /StructTreeRoot >>"),
                new("<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /F 4 >>"),
            ],
            metadataOverride: UaXmpBytes());

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.18.3-tabs", assertion.RuleId);
    }

    [Fact]
    public void Validate_ForbiddenAnnotationSubtype_ReportsError()
    {
        var bytes = BuildPagePdf(
            "/Annots [4 0 R]",
            new PdfObj("<< /Type /Annot /Subtype /Movie /Rect [0 0 1 1] /F 4 >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.5.3-annotation", assertion.RuleId);
        Assert.Contains("/Movie", assertion.Message);
    }

    [Fact]
    public void Validate_JavaScriptInAdditionalActions_ReportsError()
    {
        var bytes = AssemblePdf(
        [
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            _pagesObj,
            new("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /AA << /O 4 0 R >> >>"),
            new("<< /S /JavaScript /JS (noop) >>"),
        ]);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        Assert.Contains(result.Assertions,
            a => a.RuleId == "ISO19005-2:6.6.1-action" && a.Message.Contains("/JavaScript"));
    }

    [Fact]
    public void Validate_FontWithWrongFontFileVariant_ReportsError()
    {
        // A Type1 font embedded with /FontFile2 (a TrueType program) is not correctly embedded.
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /Type1 /BaseFont /Foo /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /FontFile2 8 0 R >>"),
            new PdfObj("/Length1 4", [1, 2, 3, 4]));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3.4-font-embedding", assertion.RuleId);
    }

    [Fact]
    public void Validate_FontFileDanglingReference_ReportsUnembedded()
    {
        // /FontFile2 points at a nonexistent object — resolving to no stream means "not embedded".
        var bytes = BuildFontPdf(
            new PdfObj("<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FontDescriptor 7 0 R >>"),
            new PdfObj("<< /Type /FontDescriptor /FontName /Foo /FontFile2 99 0 R >>"));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.3.4-font-embedding", assertion.RuleId);
    }

    [Fact]
    public void Validate_Utf16XmpPacket_IsAccepted()
    {
        // A spec-valid UTF-16 XMP packet must be recognised (the old UTF-8-only decode broke it).
        var xmpText =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta>";
        var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var bom = enc.GetPreamble();
        var body = enc.GetBytes(xmpText);
        var xmp = new byte[bom.Length + body.Length];
        bom.CopyTo(xmp, 0);
        body.CopyTo(xmp, bom.Length);

        var bytes = AssemblePdf(
            [new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj],
            metadataOverride: xmp);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void Validate_OutputIntent_NonIntegralN_ReportsError()
    {
        var bytes = BuildOutputIntentPdf(
            "[4 0 R]",
            new PdfObj("<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 5 0 R >>"),
            new PdfObj("/N 3.9", FakeIccProfile()));

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.2.2-output-intent", assertion.RuleId);
    }

    [Fact]
    public void Validate_XmpWithAlternatePdfaidPrefix_IsAccepted()
    {
        // The pdfaid namespace URI is bound to a non-default prefix 'aid'. Resolution is by URI,
        // so this valid PDF/A must still be recognised (the old literal-prefix scan would not).
        var xmp = Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:aid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<aid:part>2</aid:part><aid:conformance>B</aid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");

        var bytes = AssemblePdf(
            [new("<< /Type /Catalog /Pages 2 0 R >>"), _pagesObj, _pageObj],
            metadataOverride: xmp);

        var result = PdfPreflight.Validate(bytes, PdfConformance.PdfA2B);

        Assert.True(result.IsCompliant);
        Assert.Empty(result.Assertions);
    }

    [Fact]
    public void ValidateUa_EmptyDcTitle_ReportsError()
    {
        // dc:title is present but its rdf:li value is empty — not a real title.
        var xmp = Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" "
            + "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + "<pdfuaid:part>1</pdfuaid:part>"
            + "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\"></rdf:li></rdf:Alt></dc:title>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");

        var result = PdfPreflight.Validate(BuildUaPdf(xmpOverride: xmp), PdfConformance.PdfUA1);

        Assert.False(result.IsCompliant);
        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO14289-1:7.1-title", assertion.RuleId);
    }

    [Fact]
    public void Assertion_ToString_IncludesRuleAndSeverity()
    {
        var result = PdfPreflight.Validate(BuildCatalogMissingTypePdf(), PdfConformance.PdfA2B);
        var text = result.Assertions[0].ToString();

        Assert.Contains("Error", text);
        Assert.Contains("ISO32000-2:7.7.2-catalog-type", text);
    }
}
