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
/// Kernel-level tests for PAdES/PKCS#7 digital signature support.
///
/// The authoritative verification gate is the BCL <see cref="SignedCms.CheckSignature"/>:
/// it reconstructs the signed content from the /ByteRange, decodes the DER signature
/// from /Contents, and verifies the CMS envelope. Any offset or patching error will
/// cause this call to throw.
/// </summary>
public sealed class SignatureTests
{
    // ── Test certificate ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a self-signed RSA-2048 / SHA-256 certificate for testing.
    /// The returned certificate includes the private key.
    /// </summary>
    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        // CreateSelfSigned returns a cert with the private key attached.
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    // ── Structural assertions ────────────────────────────────────────────────

    [Fact]
    public void Signed_doc_contains_required_pdf_keywords()
    {
        using var cert = CreateTestCertificate();
        var bytes = SignOnePageDoc(cert, "SIGNING_MARKER_123");
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Type /Sig", text);
        Assert.Contains("/SubFilter /ETSI.CAdES.detached", text);
        Assert.Contains("/ByteRange [", text);
        // /Contents value is preceded by the unique sentinel comment on the same line,
        // with the '<hex>' on the following line — assert both the key and the sentinel.
        Assert.Contains("/Contents", text);
        Assert.Contains(PdfSignatureHelper.ContentsSentinel, text);
        Assert.Contains("/AcroForm", text);
        Assert.Contains("/FT /Sig", text);
        Assert.Contains("/SigFlags", text);
    }

    [Fact]
    public void Signed_doc_acroform_has_SigFlags_3()
    {
        using var cert = CreateTestCertificate();
        var bytes = SignOnePageDoc(cert);
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/SigFlags 3", text);
    }

    [Fact]
    public void Signed_doc_contains_Sig_type_in_sig_dict()
    {
        using var cert = CreateTestCertificate();
        var bytes = SignOnePageDoc(cert);
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Type /Sig", text);
    }

    // ── BCL cryptographic verification (authoritative gate) ─────────────────

    [Fact]
    public void BCL_CheckSignature_passes_for_valid_signature()
    {
        using var cert = CreateTestCertificate();
        var signedBytes = SignOnePageDoc(cert, "VELLUM_BCL_VERIFY");

        // Parse /ByteRange and /Contents from the signed bytes, then verify.
        VerifySignatureOrThrow(signedBytes);
        // If we reach here, CheckSignature did not throw — signature is valid.
    }

    [Fact]
    public void BCL_CheckSignature_throws_when_content_is_tampered()
    {
        using var cert = CreateTestCertificate();
        var signedBytes = SignOnePageDoc(cert, "TAMPER_TEST_456");

        // Parse ByteRange so we know which bytes are "signed content".
        var (br, _) = ParseSignatureFields(signedBytes);
        // Flip a byte inside the first signed segment (not inside /Contents).
        // Choose a byte well before the sig dict to affect the digest.
        var flipPos = (int)(br[0] + br[1] / 2); // middle of first segment
        // Avoid flipping inside the /ByteRange placeholder or /Contents token —
        // pick a byte in the content stream area (early in the file).
        flipPos = Math.Min(flipPos, 200);
        signedBytes[flipPos] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => VerifySignatureOrThrow(signedBytes));
    }

    [Fact]
    public void Signing_and_encryption_together_throws_NotSupportedException()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Encrypt(new VellumPdf.Encryption.PdfEncryptionSettings { UserPassword = "pw" });

        var settings = new PdfSignatureSettings { Certificate = cert };
        Assert.Throws<NotSupportedException>(() =>
        {
            var ms = new MemoryStream();
            doc.Sign(ms, settings);
        });
    }

    [Fact]
    public void Sign_throws_when_certificate_has_no_private_key()
    {
        using var cert = CreateTestCertificate();
        // Export and re-import WITHOUT private key.
        var certWithoutKey = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        using var doc = new PdfDocument();
        doc.AddPage();

        var settings = new PdfSignatureSettings { Certificate = certWithoutKey };
        Assert.Throws<ArgumentException>(() =>
        {
            var ms = new MemoryStream();
            doc.Sign(ms, settings);
        });
    }

    [Fact]
    public void ByteRange_covers_entire_file_except_contents_token()
    {
        using var cert = CreateTestCertificate();
        var signedBytes = SignOnePageDoc(cert);

        var (br, contentsInfo) = ParseSignatureFields(signedBytes);

        // br[0] = 0, br[1] = posLt, br[2] = posLt + contentsTokenLen, br[3] = remaining
        Assert.Equal(0L, br[0]);
        Assert.Equal(contentsInfo.PosLt, br[1]);
        Assert.Equal(contentsInfo.PosLt + contentsInfo.TokenLen, br[2]);
        Assert.Equal(signedBytes.Length, br[1] + contentsInfo.TokenLen + br[3]);
    }

    [Fact]
    public void Signed_doc_optional_fields_are_written_when_set()
    {
        using var cert = CreateTestCertificate();
        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            SignerName = "Alice Tester",
            Reason = "Approval",
            Location = "TestLand",
            ContactInfo = "alice@example.com",
            SigningTime = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
        };

        var bytes = SignOnePageDoc(cert, settings: settings);
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Name", text);
        Assert.Contains("Alice Tester", text);
        Assert.Contains("/Reason", text);
        Assert.Contains("Approval", text);
        Assert.Contains("/Location", text);
        Assert.Contains("TestLand", text);
        Assert.Contains("D:20260115120000+00'00'", text);
    }

    [Fact]
    public void Signature_size_exceeded_throws_InvalidOperationException()
    {
        using var cert = CreateTestCertificate();

        // Set an absurdly small EstimatedSignatureSizeBytes (1 byte = 2 hex chars)
        // so the actual DER signature (which is several KB) cannot fit.
        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            EstimatedSignatureSizeBytes = 1,
        };

        using var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();

        Assert.Throws<InvalidOperationException>(() => doc.Sign(ms, settings));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] SignOnePageDoc(
        X509Certificate2 cert,
        string markerText = "VELLUM_SIG_TEST",
        PdfSignatureSettings? settings = null)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText()
              .SetFont(font, 12)
              .SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText(markerText)
              .EndText();
        canvas.Finish();

        var sigSettings = settings ?? new PdfSignatureSettings { Certificate = cert };
        var ms = new MemoryStream();
        doc.Sign(ms, sigSettings);
        return ms.ToArray();
    }

    /// <summary>
    /// Parses /ByteRange and /Contents from signed PDF bytes and performs BCL
    /// <see cref="SignedCms.CheckSignature"/> verification. Throws on any error.
    /// </summary>
    private static void VerifySignatureOrThrow(byte[] signedBytes)
    {
        var (byteRange, contentsInfo) = ParseSignatureFields(signedBytes);

        // Reconstruct the signed content from the two ByteRange segments.
        var seg0Len = (int)byteRange[1];
        var seg1Start = (int)byteRange[2];
        var seg1Len = (int)byteRange[3];
        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(signedBytes, 0, signedContent, 0, seg0Len);
        Buffer.BlockCopy(signedBytes, seg1Start, signedContent, seg0Len, seg1Len);

        // Decode the /Contents hex string to raw DER bytes.
        // The hex content includes the actual DER bytes followed by zero-padding.
        // SignedCms.Decode uses the DER length field to determine the actual size,
        // so passing the full padded buffer (including trailing zero bytes) is correct.
        var contentsBytes = Convert.FromHexString(contentsInfo.HexContent);

        // BCL verification: detached CMS, verify-signature-only (no chain).
        var verify = new SignedCms(new ContentInfo(signedContent), detached: true);
        verify.Decode(contentsBytes);
        // verifySignatureOnly=true skips certificate chain/trust validation —
        // appropriate for self-signed test certs.
        verify.CheckSignature(verifySignatureOnly: true);
    }

    private record ContentsInfo(long PosLt, int TokenLen, string HexContent);

    /// <summary>
    /// Parses the /ByteRange array and /Contents hex string from the signed PDF bytes.
    /// Returns the four ByteRange values and the contents token info.
    /// </summary>
    private static (long[] ByteRange, ContentsInfo Contents) ParseSignatureFields(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);

        // ── Parse /ByteRange [n0 n1 n2 n3] ─────────────────────────────────
        const string byteRangeMarker = "/ByteRange [";
        var brStart = text.IndexOf(byteRangeMarker, StringComparison.Ordinal);
        Assert.True(brStart >= 0, "/ByteRange not found in signed PDF");
        var brBracket = brStart + byteRangeMarker.Length - 1; // index of '['
        var brEnd = text.IndexOf(']', brBracket);
        Assert.True(brEnd >= 0, "/ByteRange closing ']' not found");
        var brContent = text[(brBracket + 1)..brEnd].Trim();
        var brParts = brContent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, brParts.Length);
        var byteRange = brParts.Select(long.Parse).ToArray();

        // ── Parse /Contents <hex…> ──────────────────────────────────────────
        // The sentinel comment is emitted immediately before the '<' of the hex value
        // (between the /Contents key token and its hex-string value). Search for the
        // sentinel + newline + '<' to locate the hex string unambiguously.
        var sentinelMarker = VellumPdf.Document.PdfSignatureHelper.ContentsSentinel + "\n<";
        var sStart = text.IndexOf(sentinelMarker, StringComparison.Ordinal);
        Assert.True(sStart >= 0, "/Contents sentinel not found in signed PDF");
        var posLt = sStart + sentinelMarker.Length - 1; // index of '<' in text (= in bytes for Latin-1)
        var cEnd = text.IndexOf('>', posLt);
        Assert.True(cEnd >= 0, "/Contents closing '>' not found");
        var hexContent = text[(posLt + 1)..cEnd];
        var tokenLen = 1 + hexContent.Length + 1; // '<' + hex + '>'

        return (byteRange, new ContentsInfo(posLt, tokenLen, hexContent));
    }
}
