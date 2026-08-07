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

    // ── Async I/O surface (#54) ────────────────────────────────────────────────

    [Fact]
    public async Task SignAsync_producesSameBytesAsSign()
    {
        using var cert = CreateTestCertificate();
        var timestamp = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var settingsTemplate = new PdfSignatureSettings { Certificate = cert, SigningTime = timestamp };

        var syncBytes = SignOnePageDoc(cert, "VELLUM_ASYNC_PARITY", settingsTemplate, timestamp);
        var asyncBytes = await SignOnePageDocAsync(cert, "VELLUM_ASYNC_PARITY", settingsTemplate, timestamp);

        Assert.Equal(syncBytes, asyncBytes);
    }

    [Fact]
    public async Task SignAsync_passes_BCL_CheckSignature()
    {
        using var cert = CreateTestCertificate();
        var signedBytes = await SignOnePageDocAsync(cert, "VELLUM_ASYNC_VERIFY");

        VerifySignatureOrThrow(signedBytes);
        // Reaching here means CheckSignature did not throw.
    }

    [Fact]
    public async Task SignAsync_certificateWithoutPrivateKey_throwsArgumentException()
    {
        using var cert = CreateTestCertificate();
        var certWithoutKey = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        using var doc = new PdfDocument();
        doc.AddPage();

        var settings = new PdfSignatureSettings { Certificate = certWithoutKey };
        await Assert.ThrowsAsync<ArgumentException>(
            () => doc.SignAsync(new MemoryStream(), settings, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignAsync_sizeExceeded_throwsInvalidOperationException()
    {
        using var cert = CreateTestCertificate();
        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            EstimatedSignatureSizeBytes = 1,
        };

        using var doc = new PdfDocument();
        doc.AddPage();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => doc.SignAsync(new MemoryStream(), settings, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignAsync_preCancelledToken_throwsOperationCanceled()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        doc.AddPage();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var settings = new PdfSignatureSettings { Certificate = cert };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => doc.SignAsync(new MemoryStream(), settings, cts.Token));
    }

    // ── ExternalPrivateKey (HSM/PKCS#11/cloud-KMS certificates) ───────────────
    //
    // Real hardware/cloud key stores aren't reachable in a unit test, so these tests use two
    // stand-ins for "the certificate has no usable attached private key":
    //   1. A certificate re-loaded from its public-only DER bytes (X509CertificateLoader,
    //      the same shape a cloud key vault's certificate endpoint returns).
    //   2. SimulatedHsmKey below, a real in-memory RSA key wrapped so that only signing is
    //      exposed — export/import throw, matching how an HSM-backed RSA object behaves.
    // Neither test sets PdfSignatureSettings.SigningTime, so both also exercise the
    // ResolveSigningTime code path that copies ExternalPrivateKey onto the resolved settings.

    [Fact]
    public void Sign_withExternalPrivateKey_certificateWithoutOwnKey_producesValidSignature()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalPrivateKey = rsa,
        };

        var bytes = SignOnePageDoc(publicOnlyCert, "VELLUM_EXTERNAL_KEY_TEST", settings);
        VerifySignatureOrThrow(bytes);
    }

    [Fact]
    public void Sign_withSimulatedHsmKey_producesValidSignature()
    {
        using var cert = CreateTestCertificate();
        using var hsmKey = new SimulatedHsmKey(cert.GetRSAPrivateKey()!);
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalPrivateKey = hsmKey,
        };

        var bytes = SignOnePageDoc(publicOnlyCert, "VELLUM_HSM_KEY_TEST", settings);
        VerifySignatureOrThrow(bytes);

        Assert.Throws<NotSupportedException>(() => hsmKey.ExportParameters(includePrivateParameters: false));
    }

    [Fact]
    public async Task SignAsync_withExternalPrivateKey_producesValidSignature()
    {
        using var cert = CreateTestCertificate();
        using var hsmKey = new SimulatedHsmKey(cert.GetRSAPrivateKey()!);
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalPrivateKey = hsmKey,
        };

        var bytes = await SignOnePageDocAsync(publicOnlyCert, "VELLUM_ASYNC_HSM_KEY_TEST", settings);
        VerifySignatureOrThrow(bytes);
    }

    [Fact]
    public void Sign_certificateWithoutPrivateKeyAndNoExternalPrivateKey_throwsArgumentException()
    {
        using var cert = CreateTestCertificate();
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        using var doc = new PdfDocument();
        doc.AddPage();

        var settings = new PdfSignatureSettings { Certificate = publicOnlyCert };
        var ex = Assert.Throws<ArgumentException>(() => doc.Sign(new MemoryStream(), settings));
        Assert.Contains("ExternalPrivateKey", ex.Message);
    }

    /// <summary>
    /// Wraps a real RSA key but exposes only signing, matching how an HSM/PKCS#11-backed
    /// <see cref="RSA"/> object behaves: export and import always throw.
    /// </summary>
    private sealed class SimulatedHsmKey(RSA inner) : RSA
    {
        public override int KeySize
        {
            get => inner.KeySize;
            set => throw new NotSupportedException("Simulated HSM key does not support resizing.");
        }

        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
            => inner.SignHash(hash, hashAlgorithm, padding);

        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
            => inner.VerifyHash(hash, signature, hashAlgorithm, padding);

        public override RSAParameters ExportParameters(bool includePrivateParameters)
            => throw new NotSupportedException("Simulated HSM key does not support export.");

        public override void ImportParameters(RSAParameters parameters)
            => throw new NotSupportedException("Simulated HSM key does not support import.");

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] SignOnePageDoc(
        X509Certificate2 cert,
        string markerText = "VELLUM_SIG_TEST",
        PdfSignatureSettings? settings = null,
        DateTimeOffset? timestamp = null)
    {
        using var doc = new PdfDocument();
        if (timestamp is { } ts) doc.Timestamp = ts;
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

    /// <summary>Asynchronous counterpart of <see cref="SignOnePageDoc"/>, using <see cref="SigningExtensions.SignAsync(PdfDocument, Stream, PdfSignatureSettings, CancellationToken)"/>.</summary>
    private static async Task<byte[]> SignOnePageDocAsync(
        X509Certificate2 cert,
        string markerText = "VELLUM_SIG_TEST",
        PdfSignatureSettings? settings = null,
        DateTimeOffset? timestamp = null)
    {
        using var doc = new PdfDocument();
        if (timestamp is { } ts) doc.Timestamp = ts;
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
        await doc.SignAsync(ms, sigSettings);
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
        // Locate the '<' of the /Contents hex string by anchoring on /ByteRange:
        // the first '<' after the ByteRange ']' is the /Contents opening angle bracket.
        var posLt = text.IndexOf('<', brEnd);
        Assert.True(posLt >= 0, "/Contents '<' not found after /ByteRange in signed PDF");
        var cEnd = text.IndexOf('>', posLt);
        Assert.True(cEnd >= 0, "/Contents closing '>' not found");
        var hexContent = text[(posLt + 1)..cEnd];
        var tokenLen = 1 + hexContent.Length + 1; // '<' + hex + '>'

        return (byteRange, new ContentsInfo(posLt, tokenLen, hexContent));
    }
}
