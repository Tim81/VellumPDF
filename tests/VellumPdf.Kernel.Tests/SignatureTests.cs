// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Signing;
using static VellumPdf.Kernel.Tests.SignatureTestHelpers;

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
    // rsaEncryption (RFC 8017 Appendix C) — the hash-agnostic OID the BCL's own CmsSigner
    // emits for SignerInfo.signatureAlgorithm (verified empirically; CmsSigner itself
    // decides this, this codebase does not).
    private const string RsaEncryptionOid = "1.2.840.113549.1.1.1";

    // sha256WithRSAEncryption (RFC 8017 Appendix C) — one of the hash-specific OIDs
    // RFC 5754 §3.2 permits for SignerInfo.signatureAlgorithm (RFC 3370 §3.2 makes the
    // hash-agnostic rsaEncryption above the MUST-support form and this one a MAY — both
    // are legal). ExternalSignerCms emits this form when signing with SHA-256.
    private const string Sha256WithRsaEncryptionOid = "1.2.840.113549.1.1.11";

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

    /// <summary>
    /// Creates a self-signed P-256/SHA-256 certificate for testing.
    /// The returned certificate includes the private key.
    /// </summary>
    private static X509Certificate2 CreateEcTestCertificate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=VellumPdf EC Test", ecdsa, HashAlgorithmName.SHA256);
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

    // ── ExternalSigner (async cloud KMS / remote HSM) ─────────────────────────
    //
    // A real cloud KMS/HSM call can't be reached in a unit test, so SimulatedAsyncKmsSigner
    // wraps a real in-memory RSA or ECDsa key behind IExternalSigner's async surface,
    // simulating the network round-trip these providers make.

    [Fact]
    public async Task SignAsync_withExternalSigner_rsa_producesValidSignature()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa),
        };

        var bytes = await SignOnePageDocAsync(publicOnlyCert, "VELLUM_EXTERNAL_SIGNER_RSA", settings);
        VerifySignatureOrThrow(bytes);
    }

    [Fact]
    public async Task SignAsync_withExternalSigner_ecdsa_producesValidSignature()
    {
        using var cert = CreateEcTestCertificate();
        using var ecdsa = cert.GetECDsaPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(ecdsa),
        };

        var bytes = await SignOnePageDocAsync(publicOnlyCert, "VELLUM_EXTERNAL_SIGNER_ECDSA", settings);
        VerifySignatureOrThrow(bytes);
    }

    [Theory]
    [InlineData("SHA256", "2.16.840.1.101.3.4.2.1", "1.2.840.113549.1.1.11")]
    [InlineData("SHA384", "2.16.840.1.101.3.4.2.2", "1.2.840.113549.1.1.12")]
    [InlineData("SHA512", "2.16.840.1.101.3.4.2.3", "1.2.840.113549.1.1.13")]
    public async Task SignAsync_withExternalSigner_hashAlgorithm_emitsExpectedAlgorithmIdentifiers(
        string hashAlgorithmName, string expectedDigestOid, string expectedSignatureOid)
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var hashAlgorithm = new HashAlgorithmName(hashAlgorithmName);

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa, hashAlgorithm: hashAlgorithm),
        };

        var bytes = await SignOnePageDocAsync(publicOnlyCert, $"VELLUM_EXTERNAL_SIGNER_{hashAlgorithmName}", settings);
        VerifySignatureOrThrow(bytes);

        // Pins the exact digest AND signature OID per hash algorithm: DigestAlgorithmOid
        // and SignatureAlgorithmOid each have three near-identical OID constants that
        // differ only in their last arc, and VerifySignatureOrThrow alone would still pass
        // with any of them transposed — SignedCms doesn't check that the claimed algorithm
        // is the one actually used, only that some algorithm's digest/signature matches.
        var algIds = ExtractSignerInfoAlgorithmIdentifiers(bytes);
        Assert.Equal(expectedDigestOid, algIds.SignedDataDigestOid);
        Assert.False(algIds.SignedDataDigestHasParameters, "SignedData.digestAlgorithms parameters should be absent (RFC 5754 §2).");
        Assert.Equal(expectedDigestOid, algIds.DigestOid);
        Assert.Equal(expectedSignatureOid, algIds.SignatureOid);

        // The signing-certificate-v2 attribute must hash the certificate with the same
        // algorithm the signature uses, so it varies per case and belongs in this theory
        // rather than in a SHA-256-only test of its own (RFC 5035 §4, issue #168).
        AssertSigningCertificateV2(bytes, publicOnlyCert, hashAlgorithm);
    }

    [Fact]
    public void Sign_emitsSigningCertificateV2Attribute()
    {
        using var cert = CreateTestCertificate();

        var settings = new PdfSignatureSettings { Certificate = cert };
        var bytes = SignOnePageDoc(cert, "VELLUM_SIGNING_CERT_V2", settings);

        VerifySignatureOrThrow(bytes);

        // The in-process CmsSigner path signs with SHA-256, so hashAlgorithm must be absent:
        // DER forbids encoding a value equal to the field's DEFAULT of id-sha256.
        AssertSigningCertificateV2(bytes, cert, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Asserts the signature carries an ESS <c>signing-certificate-v2</c> that actually
    /// identifies <paramref name="certificate"/>, field by field.
    /// </summary>
    /// <remarks>
    /// Every value here is checked against an independently computed expectation rather than
    /// against whatever the encoder produced. A test that only asserted the attribute exists
    /// would pass just as happily on a certHash over the wrong bytes — which is the failure
    /// that matters, since the attribute's entire purpose is binding the signature to one
    /// specific certificate.
    /// </remarks>
    private static void AssertSigningCertificateV2(
        byte[] signedBytes, X509Certificate2 certificate, HashAlgorithmName hashAlgorithm)
    {
        var essCertId = ExtractSigningCertificateV2(signedBytes);
        Assert.NotNull(essCertId);

        if (hashAlgorithm == HashAlgorithmName.SHA256)
        {
            Assert.Null(essCertId.HashAlgorithmOid);
        }
        else
        {
            Assert.Equal(DigestOidFor(hashAlgorithm), essCertId.HashAlgorithmOid);
            Assert.False(
                essCertId.HashAlgorithmHasParameters,
                "ESSCertIDv2.hashAlgorithm parameters should be absent (RFC 5754 §2).");
        }

        // certHash is over the whole DER-encoded certificate including its signature, not the
        // TBS portion — a distinction no structural check would catch.
        Assert.Equal(HashFor(hashAlgorithm, certificate.RawData), essCertId.CertHash);

        // issuerSerial must name this certificate. Compared as raw DER against the
        // certificate's own encoding, so a mismatch cannot hide behind string normalization.
        Assert.Equal(certificate.IssuerName.RawData, essCertId.IssuerNameDer);

        var expectedSerial = new AsnWriter(AsnEncodingRules.DER);
        expectedSerial.WriteInteger(certificate.SerialNumberBytes.Span);
        Assert.Equal(expectedSerial.Encode(), essCertId.SerialNumberDer);
    }

    private static string DigestOidFor(HashAlgorithmName hashAlgorithm) => hashAlgorithm.Name switch
    {
        "SHA256" => "2.16.840.1.101.3.4.2.1",
        "SHA384" => "2.16.840.1.101.3.4.2.2",
        "SHA512" => "2.16.840.1.101.3.4.2.3",
        _ => throw new ArgumentOutOfRangeException(nameof(hashAlgorithm), hashAlgorithm, null),
    };

    private static byte[] HashFor(HashAlgorithmName hashAlgorithm, byte[] data) => hashAlgorithm.Name switch
    {
        "SHA256" => SHA256.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        _ => throw new ArgumentOutOfRangeException(nameof(hashAlgorithm), hashAlgorithm, null),
    };

    [Fact]
    public async Task SignAsync_withExternalSigner_unsupportedHashAlgorithm_throwsNotSupportedException()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa, hashAlgorithm: HashAlgorithmName.SHA1),
        };

        using var doc = new PdfDocument();
        doc.AddPage();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => doc.SignAsync(new MemoryStream(), settings, TestContext.Current.CancellationToken));
        Assert.Contains("SHA1", ex.Message);
        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignAsync_withExternalSigner_bLTA_addsArchiveTimestamp()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var timestamp = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa),
            TimestampClient = new TestTimestampClient(timestamp),
            RevocationClient = new NoOpRevocationClient(),
            Level = PadesLevel.B_LTA,
            SigningTime = timestamp,
        };

        var bytes = await SignOnePageDocAsync(publicOnlyCert, "VELLUM_EXTERNAL_SIGNER_BLTA", settings, timestamp);

        using var reader = VellumPdf.Reader.PdfReader.Open(bytes);
        // Two signatures: the original CMS signature and the archive /DocTimeStamp.
        Assert.Equal(2, reader.Signatures.Count);
        Assert.Contains(reader.Signatures, s => s.SubFilter?.Value == "ETSI.RFC3161");

        // The original signature (first in the file, unaffected by the appended
        // DocTimeStamp/DSS revisions) still verifies.
        VerifySignatureOrThrow(bytes);
    }

    [Fact]
    public async Task SignAsync_withExternalSigner_bLT_addsDss()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var timestamp = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa),
            TimestampClient = new TestTimestampClient(timestamp),
            RevocationClient = new NoOpRevocationClient(),
            Level = PadesLevel.B_LT,
            SigningTime = timestamp,
        };

        var bytes = await SignOnePageDocAsync(publicOnlyCert, "VELLUM_EXTERNAL_SIGNER_BLT", settings, timestamp);

        Assert.Contains("/DSS", Encoding.Latin1.GetString(bytes));
        // The DSS revision is appended after the signed content; the original /ByteRange
        // still points at unchanged bytes, so the base signature still verifies.
        VerifySignatureOrThrow(bytes);
    }

    [Fact]
    public async Task SignAsync_withExternalSigner_awaitsSignerWithoutBlocking()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var delay = TimeSpan.FromMilliseconds(200);
        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa, delay: delay),
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = await SignOnePageDocAsync(publicOnlyCert, "VELLUM_EXTERNAL_SIGNER_DELAY", settings);
        stopwatch.Stop();

        VerifySignatureOrThrow(bytes);
        Assert.True(
            stopwatch.Elapsed >= delay,
            $"Expected SignAsync to genuinely await the external signer's {delay} delay, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void Sign_withExternalSigner_throwsNotSupportedException()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa),
        };

        using var doc = new PdfDocument();
        doc.AddPage();

        Assert.Throws<NotSupportedException>(() => doc.Sign(new MemoryStream(), settings));
    }

    [Fact]
    public async Task SignAsync_withExternalSigner_invalidSignature_throwsInvalidOperationException()
    {
        using var cert = CreateTestCertificate();
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new GarbageSigner(),
        };

        using var doc = new PdfDocument();
        doc.AddPage();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => doc.SignAsync(new MemoryStream(), settings, TestContext.Current.CancellationToken));
        Assert.Contains("external signer", ex.Message, StringComparison.OrdinalIgnoreCase);

        // #167: a failed CheckSignature has three realistic causes in production — RSASSA-PSS
        // (unsupported), a malformed signature, or a KMS key ID pointing at the wrong key — and
        // the message should name all three rather than assert only one.
        Assert.Contains("PSS", ex.Message, StringComparison.Ordinal);
        Assert.Contains("different key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignAsync_withExternalSigner_thatThrows_propagatesException()
    {
        using var cert = CreateTestCertificate();
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new ThrowingSigner(),
        };

        using var doc = new PdfDocument();
        doc.AddPage();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => doc.SignAsync(new MemoryStream(), settings, TestContext.Current.CancellationToken));
        Assert.Equal("KMS unreachable", ex.Message);
    }

    [Fact]
    public async Task SignAsync_withExternalSigner_cancellationDuringSign_throwsOperationCanceled()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        using var cts = new CancellationTokenSource();

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa, beforeSign: cts.Cancel),
        };

        using var doc = new PdfDocument();
        doc.AddPage();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => doc.SignAsync(new MemoryStream(), settings, cts.Token));
    }

    [Fact]
    public async Task SignAsync_withExternalSignerAndExternalPrivateKeyBothSet_externalSignerTakesPrecedence()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        // ExternalPrivateKey is a key that would throw if actually used to sign; if
        // ExternalSigner did not take precedence, signing would fail.
        using var poisonKey = new ThrowingRsa();
        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalPrivateKey = poisonKey,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa),
        };

        var bytes = await SignOnePageDocAsync(publicOnlyCert, "VELLUM_PRECEDENCE_TEST", settings);
        VerifySignatureOrThrow(bytes);
    }

    [Fact]
    public async Task SignAsync_withExternalSigner_matchesExternalPrivateKey_forSameKey()
    {
        using var cert = CreateTestCertificate();
        using var rsa = cert.GetRSAPrivateKey()!;
        using var publicOnlyCert = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        var timestamp = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var viaExternalPrivateKey = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalPrivateKey = rsa,
            SigningTime = timestamp,
        };
        var viaExternalSigner = new PdfSignatureSettings
        {
            Certificate = publicOnlyCert,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa),
            SigningTime = timestamp,
        };

        var bytesA = SignOnePageDoc(publicOnlyCert, "VELLUM_CROSS_CHECK", viaExternalPrivateKey, timestamp);
        var bytesB = await SignOnePageDocAsync(publicOnlyCert, "VELLUM_CROSS_CHECK", viaExternalSigner, timestamp);

        // RSA PKCS#1 v1.5 is deterministic: if ExternalSignerCms's hand-rolled signedAttrs
        // encoding matches CmsSigner's byte-for-byte, signing the same digest with the same
        // key produces an identical signature. This is a sharp check that the hand-rolled
        // CMS assembly matches what CmsSigner itself builds.
        Assert.Equal(ExtractSignatureBytes(bytesA), ExtractSignatureBytes(bytesB));

        // The two paths are not fully byte-identical, and diverge on signatureAlgorithm
        // specifically: CmsSigner (path A) emits the hash-agnostic rsaEncryption OID with
        // absent parameters, while ExternalSignerCms (path B) emits the hash-specific
        // sha256WithRSAEncryption OID with an explicit NULL, per RFC 5754 §3.2 ("the
        // parameters MUST be NULL" for these OIDs). Both digestAlgorithm fields correctly
        // have absent parameters either way, per RFC 5754 §2.
        var algIdsA = ExtractSignerInfoAlgorithmIdentifiers(bytesA);
        var algIdsB = ExtractSignerInfoAlgorithmIdentifiers(bytesB);

        Assert.Equal(algIdsA.DigestOid, algIdsB.DigestOid);
        Assert.False(algIdsA.DigestHasParameters, "CmsSigner's digestAlgorithm parameters should be absent (RFC 5754 §2).");
        Assert.False(algIdsB.DigestHasParameters, "ExternalSignerCms's digestAlgorithm parameters should be absent (RFC 5754 §2).");

        Assert.Equal(RsaEncryptionOid, algIdsA.SignatureOid);
        Assert.False(algIdsA.SignatureHasParameters, "CmsSigner is expected to emit rsaEncryption with absent signatureAlgorithm parameters.");

        Assert.Equal(Sha256WithRsaEncryptionOid, algIdsB.SignatureOid);
        Assert.True(algIdsB.SignatureHasParameters, "ExternalSignerCms must emit NULL signatureAlgorithm parameters for sha256WithRSAEncryption (RFC 5754 §3.2).");
    }

    private static byte[] ExtractSignatureBytes(byte[] signedBytes)
    {
        var (_, contents) = ParseSignatureFields(signedBytes);
        var cms = new SignedCms();
        cms.Decode(Convert.FromHexString(contents.HexContent));
        return cms.SignerInfos[0].GetSignature();
    }

    /// <summary>An <see cref="IExternalSigner"/> that always returns fixed, invalid signature bytes.</summary>
    private sealed class GarbageSigner : IExternalSigner
    {
        public HashAlgorithmName HashAlgorithm => HashAlgorithmName.SHA256;

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> signedAttributesDigest, CancellationToken cancellationToken = default)
            => Task.FromResult(new byte[] { 1, 2, 3, 4 });
    }

    private sealed class ThrowingSigner : IExternalSigner
    {
        public HashAlgorithmName HashAlgorithm => HashAlgorithmName.SHA256;

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> signedAttributesDigest, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("KMS unreachable");
    }

    /// <summary>An <see cref="RSA"/> that throws if actually used, for precedence tests.</summary>
    private sealed class ThrowingRsa : RSA
    {
        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
            => throw new InvalidOperationException("This key must not be used when ExternalSigner is set.");

        public override RSAParameters ExportParameters(bool includePrivateParameters)
            => throw new NotSupportedException();

        public override void ImportParameters(RSAParameters parameters)
            => throw new NotSupportedException();
    }

    private sealed class NoOpRevocationClient : IRevocationClient
    {
        public RevocationData GetRevocationData(X509Certificate2 certificate, X509Certificate2 issuer) => new();
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
}
