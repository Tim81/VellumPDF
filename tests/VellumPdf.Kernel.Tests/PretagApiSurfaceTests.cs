// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Annotations;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Covers the two public-surface changes made before the v2.0 tag, while they were still free to
/// make: <see cref="PdfLinkAnnotation.Flags"/> becoming a typed flags enum, and
/// <see cref="PdfSignatureSettings.SubFilter"/> gaining validation.
/// </summary>
public sealed class PretagApiSurfaceTests
{
    [Fact]
    public void LinkAnnotationFlags_defaultStillEmitsPrintOnly()
    {
        // The enum change must not alter emitted bytes: /F 4 is what ISO 19005-2 §6.3.2 requires and
        // what every existing PDF/A fixture expects. This is the regression guard for the rewrite,
        // since PdfAnnotationFlags.Print is only conformant if it really is bit 3.
        Assert.Equal(4, (int)PdfAnnotationFlags.Print);

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Rect = new PdfRectangle(72, 700, 200, 714),
            Uri = "https://example.com",
        });

        var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Contains("/F 4", Encoding.Latin1.GetString(ms.ToArray()));
    }

    [Fact]
    public void LinkAnnotationFlags_nonDefaultRoundTripsToTheRightBits()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Rect = new PdfRectangle(72, 700, 200, 714),
            Uri = "https://example.com",
            Flags = PdfAnnotationFlags.Print | PdfAnnotationFlags.ReadOnly,
        });

        var ms = new MemoryStream();
        doc.Save(ms);

        // 4 | 64 — proves the enum is written as its numeric value, not its name.
        Assert.Contains("/F 68", Encoding.Latin1.GetString(ms.ToArray()));
    }

    [Fact]
    public void LinkAnnotationFlags_combinesAsBits()
    {
        // The point of [Flags]: a caller can express a non-printing hidden link without knowing the
        // numbering, which is what the old `int Flags` forced them to do.
        var combined = PdfAnnotationFlags.Hidden | PdfAnnotationFlags.NoView;

        Assert.Equal(2 | 32, (int)combined);
        Assert.True(combined.HasFlag(PdfAnnotationFlags.Hidden));
        Assert.False(combined.HasFlag(PdfAnnotationFlags.Print));
    }

    [Theory]
    [InlineData(PdfSignatureSettings.SubFilterEtsiCAdESDetached)]
    [InlineData(PdfSignatureSettings.SubFilterAdbePkcs7Detached)]
    public void SubFilter_acceptsTheSupportedValues(string subFilter)
    {
        using var certificate = CreateCertificate();

        var settings = new PdfSignatureSettings { Certificate = certificate, SubFilter = subFilter };

        Assert.Equal(subFilter, settings.SubFilter);
    }

    [Fact]
    public void SubFilter_defaultsToPades()
    {
        using var certificate = CreateCertificate();

        var settings = new PdfSignatureSettings { Certificate = certificate };

        Assert.Equal(PdfSignatureSettings.SubFilterEtsiCAdESDetached, settings.SubFilter);
    }

    [Theory]
    [InlineData("adbe.x509.rsa_sha1")]  // a real PDF sub-filter this library does not produce
    [InlineData("ETSI.RFC3161")]        // a real sub-filter, but for document timestamps
    [InlineData("")]
    [InlineData("etsi.cades.detached")] // right value, wrong case: /SubFilter is case-sensitive
    public void SubFilter_rejectsAnythingElse(string subFilter)
    {
        using var certificate = CreateCertificate();

        var ex = Assert.Throws<ArgumentException>(() =>
            new PdfSignatureSettings { Certificate = certificate, SubFilter = subFilter });

        Assert.Equal(nameof(PdfSignatureSettings.SubFilter), ex.ParamName);
        // The message has to say what to use instead, not merely that the value is wrong.
        Assert.Contains(PdfSignatureSettings.SubFilterEtsiCAdESDetached, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubFilter_rejectsNull()
    {
        using var certificate = CreateCertificate();

        Assert.Throws<ArgumentException>(() =>
            new PdfSignatureSettings { Certificate = certificate, SubFilter = null! });
    }

    [Theory]
    [InlineData(SignaturePlaceholderOptions.SubFilterEtsiCAdESDetached)]
    [InlineData(SignaturePlaceholderOptions.SubFilterAdbePkcs7Detached)]
    public void PlaceholderSubFilter_acceptsTheSupportedValues(string subFilter)
        => Assert.Equal(subFilter, new SignaturePlaceholderOptions { SubFilter = subFilter }.SubFilter);

    [Theory]
    [InlineData("adbe.x509.rsa_sha1")]
    [InlineData("ETSI.RFC3161")]
    [InlineData("")]
    [InlineData("etsi.cades.detached")]
    public void PlaceholderSubFilter_rejectsAnythingElse(string subFilter)
    {
        // The second public path to /SubFilter, reachable via PdfDocument.PrepareForSigning. It was
        // left unvalidated when PdfSignatureSettings.SubFilter gained validation, so the stated
        // rationale — an arbitrary string yields a signature claiming a format its CMS content does
        // not match — held on only one of the two ways to reach the same dictionary entry.
        var ex = Assert.Throws<ArgumentException>(
            () => new SignaturePlaceholderOptions { SubFilter = subFilter });

        Assert.Equal(nameof(SignaturePlaceholderOptions.SubFilter), ex.ParamName);
    }

    [Fact]
    public void PlaceholderSubFilter_defaultMatchesTheSigningDefault()
    {
        // The two paths must agree on the default, or a placeholder prepared by one and signed
        // through the other would disagree about the format it claims.
        Assert.Equal(
            PdfSignatureSettings.SubFilterEtsiCAdESDetached,
            new SignaturePlaceholderOptions().SubFilter);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=VellumPdf Pretag Api Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
