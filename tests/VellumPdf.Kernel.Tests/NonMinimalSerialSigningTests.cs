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
/// <see cref="Asn1SerialNumberTests"/> covers the normalization in isolation. These tests cover the
/// part that was wrong for longer: the in-process path does not normalize the serial at all — it
/// cannot, because <see cref="System.Security.Cryptography.Pkcs.SignedCms"/> encodes the
/// <c>SignerInfo</c> from the certificate itself — so it has to reject the certificate up front
/// with an actionable message instead. The first fix for #167 addressed only the external-signer
/// path and left this one throwing an opaque exception from inside the BCL.
/// </remarks>
public sealed class NonMinimalSerialSigningTests
{
    [Fact]
    public void NonMinimalSerialCertificate_isAcceptedByTheX509Parser()
    {
        // The premise of every test below: this encoding is one .NET reads happily and every DER
        // encoder refuses to write. If X509CertificateLoader ever started rejecting it, these tests
        // would be vacuous rather than failing, so the premise is asserted explicitly.
        using var certificate = NonMinimalSerialCertificate.Create();

        Assert.Equal([0x00, 0x01, 0x02, 0x03, 0x04], certificate.SerialNumberBytes.ToArray());
        Assert.False(Asn1SerialNumber.IsMinimal(certificate.SerialNumberBytes.Span));
    }

    [Fact]
    public void Sign_withNonMinimalSerial_throwsWithAnActionableMessage()
    {
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
        // The ExternalPrivateKey path is still a CmsSigner path, so it fails identically. Covered
        // separately because it constructs the CmsSigner through a different overload.
        using var certificate = NonMinimalSerialCertificate.Create();
        using var rsa = RSA.Create(2048);
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
