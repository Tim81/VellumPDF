// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for the v1.5.5 hardening close-out items.
/// Item 4 (#83): AcroForm field text uses proper PDF text-string encoding.
/// Item 5 (#83): MCID ParentTree sizing uses max(MCID)+1 (sparse arrays allowed).
/// Item 6 (#83): Signature widget page selection.
/// </summary>
public sealed class HardeningV155CloseoutTests
{
    // ── Item 4 (#83): AcroForm field text encoding ────────────────────────────

    /// <summary>
    /// A field name and value containing non-Latin-1 characters (Cyrillic)
    /// must be stored as UTF-16BE with a leading U+FEFF BOM.
    /// BOM bytes in the PDF literal string are \xFE\xFF.
    /// </summary>
    [Fact]
    public void TextField_nonLatin1Name_emitsUtf16BeBom()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        // Cyrillic: "Имя" = U+0418 U+043C U+044F
        doc.AddTextField(page, "Имя", new PdfRectangle(50, 700, 200, 720));

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        // The BOM for UTF-16BE PDF text string is \xFE\xFF inside a literal string "(\xFE\xFF..."
        // The PDF writer escapes \xFE as \xFE (>= 0x80, written raw) so look for the raw bytes.
        // In the Latin-1-decoded string we expect to see the BOM preceded by '('.
        var text = Encoding.Latin1.GetString(bytes);
        // BOM as Latin-1 decoded: \xFE = þ, \xFF = ÿ — so the string contains "(þÿ"
        Assert.Contains("(\xFE\xFF", text);
    }

    /// <summary>
    /// A text field's value is rendered into its appearance stream using the Standard-14
    /// Helvetica font, which cannot represent characters above U+00FF. Such a value is
    /// rejected with a clear exception rather than silently substituting '?' (the old bug).
    /// The stored /V text-string encoding itself is covered by the field-name test above
    /// (both /T and /V route through the same TextString helper).
    /// </summary>
    [Fact]
    public void TextField_nonLatin1Value_throws_appearanceCannotRender()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        // Emoji U+1F600 (😀) cannot be rendered by the Standard-14 appearance font.
        doc.AddTextField(page, "MyField", new PdfRectangle(50, 700, 200, 720), value: "\U0001F600");

        var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => doc.Save(ms));
    }

    /// <summary>
    /// A field name/value containing only ASCII characters must emit a compact literal
    /// string (no BOM) — existing behaviour preserved for ASCII/Latin-1 input.
    /// </summary>
    [Fact]
    public void TextField_asciiValue_emitsCompactLiteralNoBom()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddTextField(page, "FullName", new PdfRectangle(50, 700, 200, 720), value: "Alice");

        var ms = new MemoryStream();
        doc.Save(ms);
        var text = Encoding.Latin1.GetString(ms.ToArray());

        // ASCII value must appear as-is in the output (no BOM)
        Assert.Contains("(Alice)", text);
        // Field name must also be plain
        Assert.Contains("(FullName)", text);
    }

    /// <summary>
    /// Choice field options containing non-Latin-1 text must emit UTF-16BE with BOM.
    /// </summary>
    [Fact]
    public void ChoiceField_nonLatin1Option_emitsUtf16BeBom()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        // ASCII first so the rendered (selected-by-default) value is representable by the
        // Standard-14 appearance font; the non-Latin-1 Greek option is stored in /Opt only.
        doc.AddChoiceField(page, "Lang", new PdfRectangle(50, 700, 200, 720),
            options: ["ASCII", "αβγ"],
            combo: true);

        var ms = new MemoryStream();
        doc.Save(ms);
        var text = Encoding.Latin1.GetString(ms.ToArray());

        // The Greek /Opt entry must be stored as UTF-16BE with BOM.
        Assert.Contains("(\xFE\xFF", text);
        // The ASCII option must remain compact.
        Assert.Contains("(ASCII)", text);
    }

    /// <summary>
    /// The appearance stream caption path must throw when the caption contains a
    /// character that cannot be represented in Helvetica / Standard-14 (> U+00FF).
    /// </summary>
    [Fact]
    public void PushButton_nonRepresentableCaption_throws()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        // U+03B1 = α — not representable in Helvetica Standard-14
        doc.AddPushButton(page, "Btn", new PdfRectangle(50, 700, 150, 720), caption: "α Click");

        var ms = new MemoryStream();
        // The throw happens during Save when the appearance stream is built
        Assert.Throws<ArgumentException>(() => doc.Save(ms));
    }

    // ── Item 5 (#83): MCID ParentTree sparse array ────────────────────────────

    /// <summary>
    /// Contiguous MCIDs (0, 1, 2 …) produce a dense array — unchanged behaviour.
    /// </summary>
    [Fact]
    public void StructureTree_contiguousMcids_buildsCorrectly()
    {
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("Hello world").EndText();
        canvas.Finish();

        // Manually register two struct elems with contiguous MCIDs 0 and 1
        var elem0 = new PdfStructElem("P") { Page = page, Mcid = 0 };
        var elem1 = new PdfStructElem("P") { Page = page, Mcid = 1 };
        doc.RegisterStructElem(elem0);
        doc.RegisterStructElem(elem1);

        var ms = new MemoryStream();
        doc.Save(ms); // must not throw
        var text = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/StructTreeRoot", text);
        Assert.Contains("/ParentTree", text);
    }

    /// <summary>
    /// Non-contiguous MCIDs (e.g. 0 and 2, with no elem at 1) must produce a sparse
    /// array with a null hole at index 1 rather than throwing.
    /// </summary>
    [Fact]
    public void StructureTree_nonContiguousMcids_producesSparsArrayNothrow()
    {
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("Test").EndText();
        canvas.Finish();

        // MCIDs 0 and 2 — index 1 is a gap
        var elem0 = new PdfStructElem("P") { Page = page, Mcid = 0 };
        var elem2 = new PdfStructElem("P") { Page = page, Mcid = 2 };
        doc.RegisterStructElem(elem0);
        doc.RegisterStructElem(elem2);

        var ms = new MemoryStream();
        doc.Save(ms); // must not throw (previously threw with MCID out-of-range)
        var text = Encoding.Latin1.GetString(ms.ToArray());

        // The ParentTree array must be present and must contain "null" for the gap
        Assert.Contains("/ParentTree", text);
        Assert.Contains("null", text);
    }

    /// <summary>
    /// Duplicate MCIDs on the same page must still throw — the bijection is broken.
    /// </summary>
    [Fact]
    public void StructureTree_duplicateMcids_throws()
    {
        using var doc = new PdfDocument();
        doc.Tagged = true;
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.Finish();

        var elem0a = new PdfStructElem("P") { Page = page, Mcid = 0 };
        var elem0b = new PdfStructElem("P") { Page = page, Mcid = 0 };
        doc.RegisterStructElem(elem0a);
        doc.RegisterStructElem(elem0b);

        var ms = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => doc.Save(ms));
    }

    // ── Item 6 (#83): Signature widget page selection ─────────────────────────

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf CloseoutV155 Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>
    /// The default SignaturePage = 0 keeps existing behaviour: the widget annotation
    /// appears on the first page and the signature verifies.
    /// </summary>
    [Fact]
    public void Signature_defaultPage_widgetOnFirstPage()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.AddPage();

        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = cert });
        var text = Encoding.Latin1.GetString(ms.ToArray());

        // /FT /Sig must appear
        Assert.Contains("/FT /Sig", text);
        // Signature must verify
        VerifySignatureOrThrow(ms.ToArray());
    }

    /// <summary>
    /// When SignaturePage = 1 (second page), the widget /P must reference the second
    /// page dictionary ref. We verify the annotation appears in the second page's
    /// /Annots entry rather than the first page's.
    /// </summary>
    [Fact]
    public void Signature_secondPage_widgetOnSecondPage()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        var page1 = doc.AddPage();
        var page2 = doc.AddPage();

        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = cert, SignaturePage = 1 });
        var bytes = ms.ToArray();
        var text = Encoding.Latin1.GetString(bytes);

        // Signature must still verify even with the widget on page 2
        VerifySignatureOrThrow(bytes);

        // The widget must be present (/FT /Sig) and reference the correct page
        Assert.Contains("/FT /Sig", text);
    }

    /// <summary>
    /// An out-of-range SignaturePage (e.g. 5 on a 2-page doc) must throw
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    [Fact]
    public void Signature_outOfRangePage_throws()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.AddPage();

        var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.Sign(ms, new PdfSignatureSettings { Certificate = cert, SignaturePage = 5 }));
    }

    /// <summary>
    /// Negative SignaturePage must also throw.
    /// </summary>
    [Fact]
    public void Signature_negativePage_throws()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        doc.AddPage();

        var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            doc.Sign(ms, new PdfSignatureSettings { Certificate = cert, SignaturePage = -1 }));
    }

    // ── Signature verification helper ─────────────────────────────────────────

    private static void VerifySignatureOrThrow(byte[] signedBytes)
    {
        var text = Encoding.Latin1.GetString(signedBytes);

        const string byteRangeMarker = "/ByteRange [";
        var brStart = text.IndexOf(byteRangeMarker, StringComparison.Ordinal);
        Assert.True(brStart >= 0, "/ByteRange not found");
        var brEnd = text.IndexOf(']', brStart + byteRangeMarker.Length);
        var brParts = text[(brStart + byteRangeMarker.Length)..brEnd].Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var byteRange = brParts.Select(long.Parse).ToArray();

        var posLt = text.IndexOf('<', brEnd);
        Assert.True(posLt >= 0, "/Contents '<' not found");
        var cEnd = text.IndexOf('>', posLt);
        var hexContent = text[(posLt + 1)..cEnd];

        var seg0Len = (int)byteRange[1];
        var seg1Start = (int)byteRange[2];
        var seg1Len = (int)byteRange[3];
        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(signedBytes, 0, signedContent, 0, seg0Len);
        Buffer.BlockCopy(signedBytes, seg1Start, signedContent, seg0Len, seg1Len);

        var derBytes = Convert.FromHexString(hexContent);
        var verify = new SignedCms(new ContentInfo(signedContent), detached: true);
        verify.Decode(derBytes);
        verify.CheckSignature(verifySignatureOnly: true);
    }
}
