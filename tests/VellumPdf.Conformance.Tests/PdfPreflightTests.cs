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
    /// Hand-builds a single-revision PDF whose document catalog (object 1) is a dictionary
    /// but is missing the required <c>/Type /Catalog</c> entry.
    /// </summary>
    private static byte[] BuildCatalogMissingTypePdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        // Header + binary marker + trailer /ID are all present and valid, so the only
        // violation this document carries is the catalog missing its /Type entry.
        Write("%PDF-1.7\n");
        ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        var o1 = (int)ms.Position;
        Write("1 0 obj\n<< /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n");
        Write("0 3\n");
        // Each xref entry is exactly 20 bytes: 10-digit-offset SP 5-digit-gen SP type SP LF
        Write($"{0:D10} 65535 f \n");
        Write($"{o1:D10} 00000 n \n");
        Write($"{o2:D10} 00000 n \n");
        Write("trailer\n<< /Size 3 /Root 1 0 R /ID [<00112233445566778899AABBCCDDEEFF> "
            + "<00112233445566778899AABBCCDDEEFF>] >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// Hand-builds a structurally valid single-page PDF with a <c>/Type /Catalog</c> document
    /// catalog, optionally including the §6.1.2 binary marker and the §6.1.3 trailer <c>/ID</c>.
    /// Used to isolate each file-structure rule.
    /// </summary>
    private static byte[] BuildMinimalPdf(bool binaryMarker, bool trailerId)
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.7\n");
        if (binaryMarker)
            ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        var o1 = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n0 3\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{o1:D10} 00000 n \n");
        Write($"{o2:D10} 00000 n \n");
        var id = trailerId
            ? " /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]"
            : string.Empty;
        Write($"trailer\n<< /Size 3 /Root 1 0 R{id} >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// A single indirect object for <see cref="AssemblePdf"/>. For a non-stream object,
    /// <see cref="Dict"/> is the complete object text (e.g. <c>"&lt;&lt; /Type /Catalog &gt;&gt;"</c>).
    /// For a stream object, <see cref="Dict"/> is the dictionary's inner entries only (e.g.
    /// <c>"/N 3"</c>); the assembler wraps it and appends the correct <c>/Length</c>.
    /// </summary>
    private sealed record PdfObj(string Dict, byte[]? Stream = null);

    /// <summary>
    /// Assembles a classic-xref PDF/A-shaped file (header + binary marker + trailer /ID) from a
    /// 1-indexed list of objects, so individual colour/transparency rules can be isolated.
    /// Object 1 is always the document catalog (/Root).
    /// </summary>
    private static byte[] AssemblePdf(IReadOnlyList<PdfObj> objects)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        var offsets = new int[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = (int)ms.Position;
            var n = i + 1;
            if (objects[i].Stream is { } body)
            {
                W($"{n} 0 obj\n<< {objects[i].Dict} /Length {body.Length} >>\nstream\n");
                ms.Write(body);
                W("\nendstream\nendobj\n");
            }
            else
            {
                W($"{n} 0 obj\n{objects[i].Dict}\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        var size = objects.Count + 1;
        W($"xref\n0 {size}\n");
        W($"{0:D10} 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R /ID [<00112233445566778899AABBCCDDEEFF> "
            + "<00112233445566778899AABBCCDDEEFF>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
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

    [Theory]
    [InlineData(PdfConformance.PdfA2U)]
    [InlineData(PdfConformance.PdfA2A)]
    [InlineData(PdfConformance.PdfUA1)]
    public void Validate_UnregisteredLevel_Throws(PdfConformance level)
    {
        var bytes = BuildOnePagePdf();
        Assert.Throws<NotSupportedException>(() => PdfPreflight.Validate(bytes, level));
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

        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.4-blend-mode", assertion.RuleId);
        Assert.Contains("/FooBar", assertion.Message);
    }

    [Fact]
    public void Validate_BlendModeArrayWithInvalidEntry_ReportsError()
    {
        var result = PdfPreflight.Validate(BuildBlendModePdf("[/Multiply /Bogus]"), PdfConformance.PdfA2B);

        var assertion = Assert.Single(result.Assertions);
        Assert.Equal("ISO19005-2:6.4-blend-mode", assertion.RuleId);
        Assert.Contains("/Bogus", assertion.Message);
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
