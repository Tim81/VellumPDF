// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader;
using VellumPdf.Signing;

namespace VellumPdf.Reader.Tests;

public sealed class PdfReaderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Reader Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    private static byte[] SaveDocToBytes(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] SignDocToBytes(PdfDocument doc, X509Certificate2 cert)
    {
        var settings = new PdfSignatureSettings { Certificate = cert };
        var ms = new MemoryStream();
        doc.Sign(ms, settings);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a two-revision PDF in memory. Revision 1 defines obj 1 with /Rev /One and obj 2.
    /// Revision 2 overrides obj 1 with /Rev /Two. The /Prev chain links them.
    /// </summary>
    private static byte[] BuildTwoRevisionPdf()
    {
        var ms = new MemoryStream();

        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1v1Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Rev /One >>\nendobj\n");
        var obj2Offset = (int)ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        var xref1Offset = (int)ms.Position;
        Write("xref\n");
        Write("0 3\n");
        // Each xref entry is exactly 20 bytes: 10-digit-offset SP 5-digit-gen SP type SP LF
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1v1Offset:D10} 00000 n \n");
        Write($"{obj2Offset:D10} 00000 n \n");
        Write("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        Write($"startxref\n{xref1Offset}\n%%EOF\n");

        // Revision 2: override obj 1
        var obj1v2Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Rev /Two >>\nendobj\n");

        var xref2Offset = (int)ms.Position;
        Write("xref\n");
        Write("1 1\n");
        Write($"{obj1v2Offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size 3 /Root 1 0 R /Prev {xref1Offset} >>\n");
        Write($"startxref\n{xref2Offset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal PDF with /Encrypt in the trailer (not a real encrypted doc).
    /// </summary>
    private static byte[] BuildEncryptedTrailerPdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n");
        Write("0 2\n");
        // Each xref entry is exactly 20 bytes: 10-digit-offset SP 5-digit-gen SP type SP LF
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1Offset:D10} 00000 n \n");
        Write("trailer\n<< /Size 2 /Root 1 0 R /Encrypt 2 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal PDF where startxref points to an object (xref stream style, not 'xref' keyword).
    /// </summary>
    private static byte[] BuildXrefStreamStylePdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.5\n");
        // The object at this position will look like an xref stream object.
        var xrefObjOffset = (int)ms.Position;
        // Just digits at this offset triggers the xref-stream detection.
        Write("1 0 obj\n<< /Type /XRef /Size 2 >>\nstream\nendstream\nendobj\n");
        Write($"startxref\n{xrefObjOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Open_unsigned_doc_resolves_catalog()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var bytes = SaveDocToBytes(doc);

        using var reader = PdfReader.Open(bytes);

        Assert.NotNull(reader.Catalog);
        var typeObj = reader.Catalog.Get(PdfName.Type);
        var typeName = Assert.IsType<PdfName>(typeObj);
        Assert.Equal("Catalog", typeName.Value);
    }

    [Fact]
    public void Open_unsigned_doc_page_tree_reachable()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var bytes = SaveDocToBytes(doc);

        using var reader = PdfReader.Open(bytes);

        var pagesObj = reader.ResolveValue(reader.Catalog.Get(PdfName.Pages)!);
        var pagesDict = Assert.IsType<PdfDictionary>(pagesObj);
        var countObj = pagesDict.Get(PdfName.Count);
        var count = Assert.IsType<PdfInteger>(countObj);
        Assert.Equal(1, count.Value);
    }

    [Fact]
    public void Open_signed_doc_finds_one_signature()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        doc.AddPage();
        var bytes = SignDocToBytes(doc, cert);

        using var reader = PdfReader.Open(bytes);

        Assert.Single(reader.Signatures);
        var sig = reader.Signatures[0];

        Assert.NotNull(sig.SubFilter);
        Assert.Equal(4, sig.ByteRange.Length);
        Assert.True(sig.Contents.Length > 0);

        // Must be decodable as a CMS envelope (detached).
        var cms = new SignedCms(new ContentInfo(Array.Empty<byte>()), detached: true);
        cms.Decode(sig.Contents.ToArray()); // Should not throw.
    }

    [Fact]
    public void Open_signed_doc_subfilter_is_cadets_detached()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        doc.AddPage();
        var bytes = SignDocToBytes(doc, cert);

        using var reader = PdfReader.Open(bytes);

        var sig = reader.Signatures[0];
        Assert.Equal("ETSI.CAdES.detached", sig.SubFilter!.Value);
    }

    [Fact]
    public void Prev_chaining_newer_object_wins()
    {
        var bytes = BuildTwoRevisionPdf();
        using var reader = PdfReader.Open(bytes);

        // Obj 1 should resolve to the revision-2 version (/Rev /Two).
        var obj1 = reader.Resolve(1);
        var dict1 = Assert.IsType<PdfDictionary>(obj1);
        var revName = Assert.IsType<PdfName>(dict1.Get(new PdfName("Rev")));
        Assert.Equal("Two", revName.Value);

        // Obj 2 should still resolve from revision 1.
        var obj2 = reader.Resolve(2);
        var dict2 = Assert.IsType<PdfDictionary>(obj2);
        var typeObj = Assert.IsType<PdfName>(dict2.Get(PdfName.Type));
        Assert.Equal("Pages", typeObj.Value);
    }

    [Fact]
    public void Prev_chaining_rev1_object_resolves()
    {
        var bytes = BuildTwoRevisionPdf();
        using var reader = PdfReader.Open(bytes);

        // Obj 2 was only defined in revision 1 and should still resolve.
        var obj2 = reader.Resolve(2);
        Assert.NotNull(obj2);
    }

    [Fact]
    public void Open_encrypted_doc_throws_UnsupportedPdfFeatureException()
    {
        var bytes = BuildEncryptedTrailerPdf();

        Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(bytes));
    }

    [Fact]
    public void Open_xref_stream_throws_UnsupportedPdfFeatureException()
    {
        var bytes = BuildXrefStreamStylePdf();

        Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(bytes));
    }

    [Fact]
    public void Open_missing_startxref_throws_InvalidDataException()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%% no startxref here\n");

        Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes));
    }

    [Fact]
    public void Open_bad_xref_offset_throws_InvalidDataException()
    {
        // startxref points to 999999 but file is tiny.
        var bytes = Encoding.ASCII.GetBytes(
            "%PDF-1.4\nstartxref\n999999\n%%EOF\n");

        Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes));
    }

    [Fact]
    public void Open_returns_correct_total_length()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var bytes = SaveDocToBytes(doc);

        using var reader = PdfReader.Open(bytes);

        Assert.Equal(bytes.Length, reader.TotalLength);
    }

    [Fact]
    public void Open_startxref_offset_is_valid()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var bytes = SaveDocToBytes(doc);

        using var reader = PdfReader.Open(bytes);

        Assert.True(reader.StartXrefOffset > 0);
        Assert.True(reader.StartXrefOffset < bytes.Length);
    }
}
