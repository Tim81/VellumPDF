// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Signing;
using static VellumPdf.Kernel.Tests.SignatureTestHelpers;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Serialises tests that install a certificate into the physical <c>CurrentUser\CA</c> Windows
/// store: concurrent Add/Remove against that same store from two test classes races (see
/// <see cref="ExternalSignerChainTests"/> and <see cref="DssBuilderTests"/>).
/// </summary>
[CollectionDefinition("CertificateAuthorityStore", DisableParallelization = true)]
public sealed class CertificateAuthorityStoreCollection;

/// <summary>
/// Certificate-chain test for <see cref="IExternalSigner"/> signing. In the
/// <see cref="CertificateAuthorityStoreCollection"/> collection so it never runs in parallel
/// with <see cref="DssBuilderTests"/>.
/// </summary>
[Collection("CertificateAuthorityStore")]
public sealed class ExternalSignerChainTests
{
    [Fact]
    public async Task SignAsync_withExternalSigner_multiCertChain_embedsWholeChain()
    {
        // X509Chain (used by both CmsSigner's WholeChain and ExternalSignerCms) only finds an
        // issuer certificate via a system store, not from an in-memory reference alone — install
        // the CA into CurrentUser\CA for the duration of the test, mirroring
        // DssBuilderTests.Vri_entry_has_cert_ocsp_crl_arrays_with_cert_chain.
        using var ca = CreateCaCertificate();
        using var leaf = CreateLeafCertificate(ca);
        using var leafKey = leaf.GetRSAPrivateKey()!;
        using var publicOnlyLeaf = X509CertificateLoader.LoadCertificate(leaf.Export(X509ContentType.Cert));

        using var caStore = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        var caInstalled = false;
        try
        {
            caStore.Open(OpenFlags.ReadWrite);
            caStore.Add(ca);
            caInstalled = true;
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"CurrentUser CA store is not writable in this environment: {ex.Message}");
        }

        try
        {
            var settings = new PdfSignatureSettings
            {
                Certificate = publicOnlyLeaf,
                ExternalSigner = new SimulatedAsyncKmsSigner(leafKey),
            };

            var bytes = await SignOnePageDocAsync(publicOnlyLeaf, settings);
            VerifySignatureOrThrow(bytes);

            var (_, contents) = ParseSignatureFields(bytes);
            var cms = new SignedCms();
            cms.Decode(Convert.FromHexString(contents.HexContent));

            Assert.Equal(2, cms.Certificates.Count);
            var certBytes = cms.Certificates.Cast<X509Certificate2>().Select(c => c.RawData).ToList();
            Assert.Contains(certBytes, b => b.SequenceEqual(ca.RawData));
            Assert.Contains(certBytes, b => b.SequenceEqual(publicOnlyLeaf.RawData));
        }
        finally
        {
            if (caInstalled)
            {
                try { caStore.Remove(ca); }
                catch (CryptographicException) { /* best-effort cleanup */ }
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static X509Certificate2 CreateCaCertificate()
    {
        using var caKey = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Signature Test CA",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddYears(10));
    }

    private static X509Certificate2 CreateLeafCertificate(X509Certificate2 ca)
    {
        using var leafKey = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Signature Test Leaf",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var serial = new byte[8];
        Random.Shared.NextBytes(serial);
        var leafNoKey = req.Create(
            ca,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1),
            serial);
        return leafNoKey.CopyWithPrivateKey(leafKey);
    }

    private static async Task<byte[]> SignOnePageDocAsync(X509Certificate2 cert, PdfSignatureSettings settings)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText()
              .SetFont(font, 12)
              .SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("VELLUM_EXTERNAL_SIGNER_CHAIN")
              .EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        await doc.SignAsync(ms, settings);
        return ms.ToArray();
    }
}
