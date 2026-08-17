// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Document;
using VellumPdf.Signing;
using static VellumPdf.Kernel.Tests.SignatureTestHelpers;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// End-to-end coverage for issue #167: how each signing path behaves when
/// <see cref="PdfSignatureSettings.Certificate"/> carries a serial number that is not minimally
/// DER-encoded.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Asn1SerialNumberTests"/> covers the normalization in isolation. These tests cover the
/// part that was wrong for longer: the in-process path does not normalize the serial at all — it
/// cannot, because <see cref="System.Security.Cryptography.Pkcs.SignedCms"/> encodes the
/// <c>SignerInfo</c> from the certificate itself — so it has to reject the certificate up front
/// with an actionable message instead. The first fix for #167 addressed only the external-signer
/// path and left this one throwing an opaque exception from inside the BCL.
/// </para>
/// <para>
/// <strong>Most of these only run on Windows</strong>, and not by choice: loading a certificate with
/// a non-minimally-encoded serial succeeds on Windows and fails on Linux, where OpenSSL rejects it
/// as <c>ASN1 corrupted data</c>. The condition under test therefore does not exist on Linux, and
/// <see cref="NonMinimalSerialCertificate.SkipIfUnsupported"/> skips rather than pretends. CI runs
/// on Linux, so the integration between the check and <c>CreateSigner</c> is covered by a developer
/// or CI leg on Windows only; the predicate it depends on, <c>Asn1SerialNumber.IsMinimal</c>, is
/// covered everywhere by <see cref="Asn1SerialNumberTests"/> against <c>AsnWriter</c>'s own
/// acceptance as the oracle.
/// </para>
/// </remarks>
public sealed class NonMinimalSerialSigningTests
{
    [Fact]
    public void NonMinimalSerialCertificate_isAcceptedByTheX509Parser()
    {
        // Deliberately NOT gated by SkipIfUnsupported: this is the test that detects the fixture
        // going vacuous, so gating it on the same probe it exists to validate would disable the
        // detector along with everything it protects. On Windows the encoding must load; elsewhere
        // it must not, and either way that is asserted rather than skipped.
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(
                NonMinimalSerialCertificate.IsSupportedByPlatform,
                "Only Windows is known to accept a non-minimally-encoded serial; if another platform "
                + "starts accepting it, the tests gated on this probe need revisiting.");
            return;
        }

        Assert.True(
            NonMinimalSerialCertificate.IsSupportedByPlatform,
            "Windows accepts a non-minimally-encoded serial. If this fails the fixture is broken, "
            + "and every test gated on the probe would otherwise skip silently.");
        // The premise of every test below: on this platform the encoding is one the X.509 parser
        // reads happily and every DER encoder refuses to write. Asserted explicitly, because if the
        // parser started normalizing the serial instead of preserving it these tests would become
        // vacuous rather than failing.
        using var certificate = NonMinimalSerialCertificate.Create();

        Assert.Equal([0x00, 0x01, 0x02, 0x03, 0x04], certificate.SerialNumberBytes.ToArray());
        Assert.False(Asn1SerialNumber.IsMinimal(certificate.SerialNumberBytes.Span));
    }

    [Fact]
    public void Sign_withNonMinimalSerial_throwsWithAnActionableMessage()
    {
        NonMinimalSerialCertificate.SkipIfUnsupported();
        using var certificate = NonMinimalSerialCertificate.Create();
        using var doc = new PdfDocument();
        doc.AddPage();

        var settings = new PdfSignatureSettings { Certificate = certificate };
        var ex = Assert.Throws<ArgumentException>(() => doc.Sign(new MemoryStream(), settings));

        // The defect in #167 was misdirection, not a bad signature: the old message came from
        // AsnWriter deep inside the BCL and named neither the certificate nor a remedy.
        Assert.Contains("serial number", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x0001020304", ex.Message);
        Assert.Contains("re-issuing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("settings", ex.ParamName);
    }

    [Fact]
    public async Task SignAsync_withNonMinimalSerial_throwsWithAnActionableMessage()
    {
        NonMinimalSerialCertificate.SkipIfUnsupported();
        using var certificate = NonMinimalSerialCertificate.Create();
        using var doc = new PdfDocument();
        doc.AddPage();

        var settings = new PdfSignatureSettings { Certificate = certificate };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => doc.SignAsync(new MemoryStream(), settings, TestContext.Current.CancellationToken));
        Assert.Contains("0x0001020304", ex.Message);
    }

    [Fact]
    public void Sign_withExternalPrivateKey_andNonMinimalSerial_throwsTheSameWay()
    {
        NonMinimalSerialCertificate.SkipIfUnsupported();
        // The ExternalPrivateKey path is still a CmsSigner path, so it fails identically. Covered
        // separately because it constructs the CmsSigner through a different overload.
        using var certificate = NonMinimalSerialCertificate.Create();
        // The certificate's OWN key, not an unrelated one. With an unrelated key this test passed
        // for the wrong reason: removing the guard produced "Could not determine signature algorithm
        // for the signer certificate" rather than the serial error, so a regression in the guard
        // would have surfaced here as a misleading key-mismatch diagnosis.
        using var rsa = certificate.GetRSAPrivateKey()!;
        using var doc = new PdfDocument();
        doc.AddPage();

        var settings = new PdfSignatureSettings
        {
            Certificate = certificate,
            ExternalPrivateKey = rsa,
        };

        var ex = Assert.Throws<ArgumentException>(() => doc.Sign(new MemoryStream(), settings));
        Assert.Contains("0x0001020304", ex.Message);
    }

    [Fact]
    public async Task SignAsync_withExternalSigner_andNonMinimalSerial_isAlsoRejected()
    {
        NonMinimalSerialCertificate.SkipIfUnsupported();

        // The external-signer path used to be allowed through, on the reasoning that
        // ExternalSignerCms writes the SignerInfo itself and normalizes the serial, and the result
        // does verify under SignedCms.CheckSignature. Submitting such a signature to the EU DSS
        // validator returns noSignatureFound: the normalized SignerInfo.IssuerAndSerialNumber no
        // longer matches the raw serial of the certificate in SignedData.certificates, so a
        // verifier resolving the signer by those bytes cannot locate it. An identical document
        // signed the same way with a minimal serial is found and reported as PAdES-BES.
        using var certificate = NonMinimalSerialCertificate.Create();
        using var rsa = certificate.GetRSAPrivateKey()!;
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));

        using var doc = new PdfDocument();
        doc.AddPage();

        var settings = new PdfSignatureSettings
        {
            Certificate = publicOnly,
            ExternalSigner = new SimulatedAsyncKmsSigner(rsa),
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => doc.SignAsync(new MemoryStream(), settings, TestContext.Current.CancellationToken));
        Assert.Contains("0x0001020304", ex.Message);
        Assert.DoesNotContain("ExternalSigner does work", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejection_leavesTheDocumentReusable()
    {
        NonMinimalSerialCertificate.SkipIfUnsupported();

        // PdfDocument.Save sets _written only after its preconditions pass, explicitly "so a
        // recoverable precondition failure leaves the document usable for a retry". This check has
        // to honour that: it is a settings precondition, so it belongs beside the other settings
        // preconditions in SigningExtensions rather than deeper in the pipeline. Placed inside the
        // CMS computation it fired only after the whole document had been built and consumed, so a
        // caller who did exactly what the message told them — swap in a re-issued certificate and
        // sign again — got "This document has already been written" instead of a signature.
        using var badCertificate = NonMinimalSerialCertificate.Create();
        using var goodCertificate = NonMinimalSerialCertificate.Create([0x01, 0x02, 0x03, 0x04]);

        using var doc = new PdfDocument();
        doc.AddPage();

        Assert.Throws<ArgumentException>(
            () => doc.Sign(new MemoryStream(), new PdfSignatureSettings { Certificate = badCertificate }));

        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = goodCertificate });
        VerifySignatureOrThrow(ms.ToArray());
    }

    [Theory]
    // An ordinary positive serial, as CertificateRequest would issue.
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    // 0x00 ahead of a byte whose high bit IS set: required by DER, not redundant. Roughly half of
    // all real CA serials look like this, so rejecting it would break signing for a large share of
    // genuine certificates — the most damaging false positive this check could have.
    [InlineData(new byte[] { 0x00, 0x80, 0x01 })]
    // A genuinely negative serial: mis-issued by convention (RFC 5280 requires positive) but
    // legally encoded, so the DER encoders accept it and this check must not intervene.
    [InlineData(new byte[] { 0x80, 0x01 })]
    public void MinimallyEncodedSerial_signsSuccessfully(byte[] serial)
    {
        using var certificate = NonMinimalSerialCertificate.Create(serial);
        using var doc = new PdfDocument();
        doc.AddPage();

        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = certificate });

        // Signing all the way through is the real assertion: the serial reached both this library's
        // ESSCertIDv2 writer and the BCL's SignerInfo encoder without either objecting.
        Assert.Equal(serial, certificate.SerialNumberBytes.ToArray());
        VerifySignatureOrThrow(ms.ToArray());
    }
}
