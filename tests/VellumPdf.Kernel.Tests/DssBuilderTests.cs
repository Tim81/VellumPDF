// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Reader;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for <see cref="DssBuilder.AddLongTermValidation"/>: PAdES B-LT /DSS construction.
/// All tests are fully offline and deterministic.
/// </summary>
public sealed class DssBuilderTests
{
    // Canned DER payloads — minimal well-formed SEQUENCE bytes; DssBuilder embeds verbatim.
    private static readonly byte[] s_cannedOcsp = [0x30, 0x03, 0x0A, 0x01, 0x00];
    private static readonly byte[] s_cannedCrl = [0x30, 0x04, 0x02, 0x01, 0x2A];

    private static readonly DateTimeOffset s_pinnedTime =
        new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    // ── Certificate helpers ──────────────────────────────────────────────────────

    /// <summary>Creates a self-signed RSA-2048 leaf certificate for simple signing tests.</summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf DSS Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>
    /// Creates a CA + leaf certificate chain.
    /// Returns (caCert, leafCert). The leaf is signed by the CA, so the CA is the issuer.
    /// Both carry their private keys.
    /// </summary>
    private static (X509Certificate2 Ca, X509Certificate2 Leaf) CreateCertChain()
    {
        // CA
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest(
            "CN=VellumPdf DSS Test CA",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        var caCert = caReq.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddYears(10));

        // Leaf signed by CA
        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest(
            "CN=VellumPdf DSS Test Leaf",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var serial = new byte[8];
        Random.Shared.NextBytes(serial);
        var leafCertNoKey = leafReq.Create(
            caCert,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            serial);

        // Re-attach the private key so CmsSigner can sign
        var leafCert = leafCertNoKey.CopyWithPrivateKey(leafKey);

        return (caCert, leafCert);
    }

    private static byte[] SignOnePage(X509Certificate2 cert, ITimestampClient? tsaClient = null)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText()
              .SetFont(font, 12)
              .SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("DSS test page")
              .EndText();
        canvas.Finish();

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            TimestampClient = tsaClient,
        };
        var ms = new MemoryStream();
        doc.Sign(ms, settings);
        return ms.ToArray();
    }

    // ── Test: unsigned doc throws ─────────────────────────────────────────────────

    [Fact]
    public void AddLongTermValidation_throws_when_no_signatures()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms);
        var unsignedBytes = ms.ToArray();

        var ex = Assert.Throws<InvalidOperationException>(
            () => DssBuilder.AddLongTermValidation(unsignedBytes, new CannedRevocationClient()));
        Assert.Contains("no signatures", ex.Message);
    }

    // ── Test: /DSS is present in the catalog ──────────────────────────────────────

    [Fact]
    public void Dss_catalog_entry_present_after_ltv()
    {
        using var cert = CreateSelfSignedCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var signedBytes = SignOnePage(cert, tsaClient);
        var ltvBytes = DssBuilder.AddLongTermValidation(signedBytes, new CannedRevocationClient());

        using var reader = PdfReader.Open(ltvBytes);
        var dssRaw = reader.Catalog.Get(new PdfName("DSS"));
        Assert.NotNull(dssRaw);
    }

    // ── Test: /DSS has non-empty /Certs and correct /VRI key ─────────────────────

    [Fact]
    public void Dss_has_certs_and_vri_with_correct_key()
    {
        using var cert = CreateSelfSignedCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var signedBytes = SignOnePage(cert, tsaClient);

        // Compute expected VRI key: SHA-1 of the /Contents DER of the signature.
        using var sigReader = PdfReader.Open(signedBytes);
        var sig = sigReader.Signatures[0];
        var contentsDer = sig.Contents.ToArray();
        var expectedVriKey = Convert.ToHexString(SHA1.HashData(contentsDer));

        var ltvBytes = DssBuilder.AddLongTermValidation(signedBytes, new CannedRevocationClient());

        using var reader = PdfReader.Open(ltvBytes);

        // Resolve /DSS
        var dssRef = reader.Catalog.Get(new PdfName("DSS")) as PdfIndirectReference;
        Assert.NotNull(dssRef);
        var dssDict = reader.Resolve(dssRef.ObjectNumber) as PdfDictionary;
        Assert.NotNull(dssDict);

        // /Certs must be a non-empty array
        var certsRaw = dssDict.Get(new PdfName("Certs"));
        Assert.NotNull(certsRaw);
        var certsArr = certsRaw as PdfArray;
        Assert.NotNull(certsArr);
        Assert.True(certsArr.Count > 0, "/DSS /Certs array must be non-empty");

        // /VRI must be a dictionary with the expected key
        var vriRaw = dssDict.Get(new PdfName("VRI"));
        Assert.NotNull(vriRaw);
        var vriDict = vriRaw as PdfDictionary;
        Assert.NotNull(vriDict);

        var vriEntry = vriDict.Get(new PdfName(expectedVriKey));
        Assert.NotNull(vriEntry);
    }

    // ── Test: /VRI entry has /Cert, /OCSP, /CRL arrays (with a CA+leaf chain) ────

    [Fact]
    public void Vri_entry_has_cert_ocsp_crl_arrays_with_cert_chain()
    {
        // Use a CA + leaf chain so revocation is fetched for the leaf (leaf is not self-issued).
        // Install the CA cert into the CurrentUser\CA store so X509Chain can find it when
        // PdfCmsSigner signs with WholeChain — we remove it in the finally block.
        var (caCert, leafCert) = CreateCertChain();
        using (caCert)
        using (leafCert)
        {
            using var caStore = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
            var caInstalled = false;
            try
            {
                caStore.Open(OpenFlags.ReadWrite);
                caStore.Add(caCert);
                caInstalled = true;
            }
            catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip($"CurrentUser CA store is not writable in this environment: {ex.Message}");
            }

            try
            {
                var tsaClient = new TestTimestampClient(s_pinnedTime);
                var signedBytes = SignOnePage(leafCert, tsaClient);

                using var sigReader = PdfReader.Open(signedBytes);
                var sig = sigReader.Signatures[0];
                var expectedVriKey = Convert.ToHexString(SHA1.HashData(sig.Contents.ToArray()));

                var ltvBytes = DssBuilder.AddLongTermValidation(signedBytes, new CannedRevocationClient());

                using var reader = PdfReader.Open(ltvBytes);

                var dssRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("DSS"))!;
                var dssDict = (PdfDictionary)reader.Resolve(dssRef.ObjectNumber)!;
                var vriDict = (PdfDictionary)dssDict.Get(new PdfName("VRI"))!;
                var vriEntry = (PdfDictionary)vriDict.Get(new PdfName(expectedVriKey))!;

                // /Cert array — must be present; leaf and CA should be in /DSS /Certs
                var certArr = vriEntry.Get(new PdfName("Cert")) as PdfArray;
                Assert.NotNull(certArr);
                Assert.True(certArr.Count > 0);

                // /OCSP and /CRL — canned client supplies data for the leaf (non-self-issued)
                var ocspArr = vriEntry.Get(new PdfName("OCSP")) as PdfArray;
                Assert.NotNull(ocspArr);
                Assert.True(ocspArr.Count > 0);

                var crlArr = vriEntry.Get(new PdfName("CRL")) as PdfArray;
                Assert.NotNull(crlArr);
                Assert.True(crlArr.Count > 0);
            }
            finally
            {
                if (caInstalled)
                {
                    try { caStore.Remove(caCert); }
                    catch (CryptographicException) { /* best-effort cleanup */ }
                }
            }
        }
    }

    // ── Test: cert stream decodes as X509Certificate2 equal to signer cert ────────

    [Fact]
    public void Cert_stream_content_parses_as_x509_signer_cert()
    {
        using var cert = CreateSelfSignedCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var signedBytes = SignOnePage(cert, tsaClient);

        var ltvBytes = DssBuilder.AddLongTermValidation(signedBytes, new CannedRevocationClient());

        using var reader = PdfReader.Open(ltvBytes);

        var dssRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("DSS"))!;
        var dssDict = (PdfDictionary)reader.Resolve(dssRef.ObjectNumber)!;
        var certsArr = (PdfArray)dssDict.Get(new PdfName("Certs"))!;

        // At least one cert stream must parse as an X509Certificate2 equal to the signer cert.
        var found = false;
        for (var i = 0; i < certsArr.Count; i++)
        {
            var certRef = certsArr[i] as PdfIndirectReference;
            Assert.NotNull(certRef);

            var streamDict = reader.Resolve(certRef.ObjectNumber) as PdfDictionary;
            Assert.NotNull(streamDict);

            var certDer = ReadStreamBody(ltvBytes, certRef.ObjectNumber);
            if (certDer is null)
                continue;

            using var parsedCert = X509CertificateLoader.LoadCertificate(certDer);
            if (parsedCert.RawData.AsSpan().SequenceEqual(cert.RawData.AsSpan()))
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "Signer certificate was not found among /DSS /Certs stream objects.");
    }

    // ── Test: signature is still valid after DSS append ───────────────────────────

    [Fact]
    public void Original_signature_still_valid_after_ltv_append()
    {
        using var cert = CreateSelfSignedCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var signedBytes = SignOnePage(cert, tsaClient);
        var ltvBytes = DssBuilder.AddLongTermValidation(signedBytes, new CannedRevocationClient());

        // The signature's ByteRange was written relative to revision-1 bytes.
        // Re-open the LTV document and use the unchanged ByteRange to reconstruct signed content.
        using var reader = PdfReader.Open(ltvBytes);
        var sig = reader.Signatures[0];
        Assert.Equal(4, sig.ByteRange.Length);

        var br = sig.ByteRange;
        var seg0Start = br[0];
        var seg0Len = br[1];
        var seg1Start = br[2];
        var seg1Len = br[3];

        // Verify that the ByteRange offsets still address bytes within the LTV buffer.
        Assert.True(seg0Start + seg0Len <= ltvBytes.Length);
        Assert.True(seg1Start + seg1Len <= ltvBytes.Length);

        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(ltvBytes, seg0Start, signedContent, 0, seg0Len);
        Buffer.BlockCopy(ltvBytes, seg1Start, signedContent, seg0Len, seg1Len);

        var contentsBytes = sig.Contents.ToArray();
        var verify = new SignedCms(new ContentInfo(signedContent), detached: true);
        verify.Decode(contentsBytes);
        // verifySignatureOnly=true skips chain/trust validation — appropriate for self-signed test certs.
        verify.CheckSignature(verifySignatureOnly: true);
        // Reaching here means CheckSignature did not throw — original signature remains valid.
    }

    // ── Test: double-append (idempotent LTV) ──────────────────────────────────────

    [Fact]
    public void Double_ltv_append_does_not_throw_and_dss_still_resolves()
    {
        using var cert = CreateSelfSignedCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var signedBytes = SignOnePage(cert, tsaClient);
        var ltvOnce = DssBuilder.AddLongTermValidation(signedBytes, new CannedRevocationClient());
        var ltvTwice = DssBuilder.AddLongTermValidation(ltvOnce, new CannedRevocationClient());

        using var reader = PdfReader.Open(ltvTwice);
        var dssRaw = reader.Catalog.Get(new PdfName("DSS"));
        Assert.NotNull(dssRaw);

        var dssRef = dssRaw as PdfIndirectReference;
        Assert.NotNull(dssRef);
        var dssDict = reader.Resolve(dssRef.ObjectNumber);
        Assert.NotNull(dssDict);
    }

    // ── Test: DSS works for B-B (no timestamp) ───────────────────────────────────

    [Fact]
    public void Dss_works_without_timestamp_attribute()
    {
        using var cert = CreateSelfSignedCertificate();

        // B-B: no timestamp client
        var signedBytes = SignOnePage(cert, tsaClient: null);
        var ltvBytes = DssBuilder.AddLongTermValidation(signedBytes, new CannedRevocationClient());

        using var reader = PdfReader.Open(ltvBytes);
        var dssRaw = reader.Catalog.Get(new PdfName("DSS"));
        Assert.NotNull(dssRaw);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the raw stream body for the given object number from a PDF byte array.
    /// Returns null if the stream cannot be located.
    /// </summary>
    private static byte[]? ReadStreamBody(byte[] pdfBytes, int objectNumber)
    {
        var marker = System.Text.Encoding.Latin1.GetBytes($"{objectNumber} 0 obj");
        var span = pdfBytes.AsSpan();

        var pos = -1;
        for (var i = 0; i <= pdfBytes.Length - marker.Length; i++)
        {
            if (span[i..].StartsWith(marker.AsSpan()))
            {
                pos = i;
                break;
            }
        }
        if (pos < 0) return null;

        var streamMarker = "stream\n"u8;
        var endstreamMarker = "\nendstream"u8;

        var streamStart = -1;
        for (var i = pos; i <= pdfBytes.Length - streamMarker.Length; i++)
        {
            if (span[i..].StartsWith(streamMarker))
            {
                streamStart = i + streamMarker.Length;
                break;
            }
        }
        if (streamStart < 0) return null;

        var streamEnd = -1;
        for (var i = streamStart; i <= pdfBytes.Length - endstreamMarker.Length; i++)
        {
            if (span[i..].StartsWith(endstreamMarker))
            {
                streamEnd = i;
                break;
            }
        }
        if (streamEnd < 0) return null;

        return pdfBytes[streamStart..streamEnd];
    }

    // ── In-process fake revocation client ─────────────────────────────────────────

    /// <summary>
    /// Returns canned OCSP and CRL DER for every (cert, issuer) pair.
    /// Phase 5 embeds these verbatim without validating them.
    /// </summary>
    private sealed class CannedRevocationClient : IRevocationClient
    {
        public RevocationData GetRevocationData(X509Certificate2 certificate, X509Certificate2 issuer)
            => new()
            {
                Ocsp = new ReadOnlyMemory<byte>(s_cannedOcsp),
                Crl = new ReadOnlyMemory<byte>(s_cannedCrl),
            };
    }
}
