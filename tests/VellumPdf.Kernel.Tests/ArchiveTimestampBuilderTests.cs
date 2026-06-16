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
/// Tests for <see cref="ArchiveTimestampBuilder.AddArchiveTimestamp"/>: PAdES B-LTA /DocTimeStamp.
/// All tests are fully offline and deterministic.
/// </summary>
public sealed class ArchiveTimestampBuilderTests
{
    private static readonly DateTimeOffset s_pinnedTime =
        new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static readonly byte[] s_cannedOcsp = [0x30, 0x03, 0x0A, 0x01, 0x00];
    private static readonly byte[] s_cannedCrl = [0x30, 0x04, 0x02, 0x01, 0x2A];

    // ── Certificate helpers ──────────────────────────────────────────────────────

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Archive TS Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>
    /// Produces a B-LT PDF: sign → DSS. Returns the LTV bytes.
    /// </summary>
    private static byte[] BuildBLtPdf(X509Certificate2 cert)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText()
              .SetFont(font, 12)
              .SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("Archive timestamp test")
              .EndText();
        canvas.Finish();

        var tsaClient = new TestTimestampClient(s_pinnedTime);
        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            TimestampClient = tsaClient,
        };
        var ms = new MemoryStream();
        doc.Sign(ms, settings);
        var signedBytes = ms.ToArray();

        return DssBuilder.AddLongTermValidation(signedBytes, new CannedRevocationClient());
    }

    // ── Test: null arguments ──────────────────────────────────────────────────────

    [Fact]
    public void AddArchiveTimestamp_throws_on_null_pdf()
    {
        var tsaClient = new TestTimestampClient(s_pinnedTime);
        Assert.Throws<ArgumentNullException>(
            () => ArchiveTimestampBuilder.AddArchiveTimestamp(null!, tsaClient));
    }

    [Fact]
    public void AddArchiveTimestamp_throws_on_null_client()
    {
        Assert.Throws<ArgumentNullException>(
            () => ArchiveTimestampBuilder.AddArchiveTimestamp([], null!));
    }

    // ── Test: two signatures after B-LTA ─────────────────────────────────────────

    [Fact]
    public void BLta_pdf_contains_two_signatures()
    {
        using var cert = CreateSelfSignedCertificate();
        var ltvBytes = BuildBLtPdf(cert);
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var bltaBytes = ArchiveTimestampBuilder.AddArchiveTimestamp(ltvBytes, tsaClient);

        using var reader = PdfReader.Open(bltaBytes);
        Assert.Equal(2, reader.Signatures.Count);
    }

    // ── Test: DocTimeStamp /SubFilter is /ETSI.RFC3161 ────────────────────────────

    [Fact]
    public void DocTimeStamp_has_ETSI_RFC3161_subfilter()
    {
        using var cert = CreateSelfSignedCertificate();
        var ltvBytes = BuildBLtPdf(cert);
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var bltaBytes = ArchiveTimestampBuilder.AddArchiveTimestamp(ltvBytes, tsaClient);

        using var reader = PdfReader.Open(bltaBytes);
        var docTs = reader.Signatures.FirstOrDefault(
            s => s.SubFilter?.Value == "ETSI.RFC3161");
        Assert.NotNull(docTs);
    }

    // ── Test: DocTimeStamp /Contents decodes as valid RFC 3161 token ─────────────

    [Fact]
    public void DocTimeStamp_contents_decodes_as_valid_rfc3161_token()
    {
        using var cert = CreateSelfSignedCertificate();
        var ltvBytes = BuildBLtPdf(cert);
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var bltaBytes = ArchiveTimestampBuilder.AddArchiveTimestamp(ltvBytes, tsaClient);

        using var reader = PdfReader.Open(bltaBytes);
        var docTs = reader.Signatures.First(s => s.SubFilter?.Value == "ETSI.RFC3161");

        var tokenDer = docTs.Contents.ToArray();
        Assert.True(Rfc3161TimestampToken.TryDecode(tokenDer, out var token, out _));
        Assert.NotNull(token);
        Assert.Equal("2.16.840.1.101.3.4.2.1", token.TokenInfo.HashAlgorithmId.Value); // SHA-256
    }

    // ── Test: DocTimeStamp token covers exactly the signed content ────────────────

    [Fact]
    public void DocTimeStamp_token_covers_correct_digest_of_final_file()
    {
        using var cert = CreateSelfSignedCertificate();
        var ltvBytes = BuildBLtPdf(cert);
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var bltaBytes = ArchiveTimestampBuilder.AddArchiveTimestamp(ltvBytes, tsaClient);

        using var reader = PdfReader.Open(bltaBytes);
        var docTs = reader.Signatures.First(s => s.SubFilter?.Value == "ETSI.RFC3161");

        // Reconstruct signed content from the DocTimeStamp's ByteRange over the final file.
        var br = docTs.ByteRange;
        Assert.Equal(4, br.Length);

        var seg0Start = br[0];
        var seg0Len = br[1];
        var seg1Start = br[2];
        var seg1Len = br[3];

        Assert.True(seg0Start + seg0Len <= bltaBytes.Length);
        Assert.True(seg1Start + seg1Len <= bltaBytes.Length);

        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(bltaBytes, seg0Start, signedContent, 0, seg0Len);
        Buffer.BlockCopy(bltaBytes, seg1Start, signedContent, seg0Len, seg1Len);

        var expectedDigest = SHA256.HashData(signedContent);

        var tokenDer = docTs.Contents.ToArray();
        Assert.True(Rfc3161TimestampToken.TryDecode(tokenDer, out var token, out _));
        Assert.True(token!.TokenInfo.GetMessageHash().Span.SequenceEqual(expectedDigest),
            "DocTimeStamp token message hash does not match SHA-256 of signed content.");
    }

    // ── Test: ByteRange structure is correct ──────────────────────────────────────

    [Fact]
    public void DocTimeStamp_byterange_starts_at_zero_and_reaches_eof()
    {
        using var cert = CreateSelfSignedCertificate();
        var ltvBytes = BuildBLtPdf(cert);
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var bltaBytes = ArchiveTimestampBuilder.AddArchiveTimestamp(ltvBytes, tsaClient);

        using var reader = PdfReader.Open(bltaBytes);
        var docTs = reader.Signatures.First(s => s.SubFilter?.Value == "ETSI.RFC3161");

        var br = docTs.ByteRange;
        Assert.Equal(4, br.Length);
        Assert.Equal(0, br[0]); // must start at 0
        // Last segment reaches EOF: br[2] + br[3] == file length
        Assert.Equal(bltaBytes.Length, br[2] + br[3]);
        // br[1] = posLt, br[2] > br[1]
        Assert.True(br[2] > br[1], "Second segment must start after first segment.");
    }

    // ── Test: original signature still validates ──────────────────────────────────

    [Fact]
    public void Original_signature_still_valid_after_archive_timestamp()
    {
        using var cert = CreateSelfSignedCertificate();
        var ltvBytes = BuildBLtPdf(cert);
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var bltaBytes = ArchiveTimestampBuilder.AddArchiveTimestamp(ltvBytes, tsaClient);

        using var reader = PdfReader.Open(bltaBytes);
        // The original CMS signature is the one WITHOUT /ETSI.RFC3161 SubFilter.
        var origSig = reader.Signatures.First(s => s.SubFilter?.Value != "ETSI.RFC3161");

        var br = origSig.ByteRange;
        Assert.Equal(4, br.Length);

        var seg0Start = br[0];
        var seg0Len = br[1];
        var seg1Start = br[2];
        var seg1Len = br[3];

        Assert.True(seg0Start + seg0Len <= bltaBytes.Length);
        Assert.True(seg1Start + seg1Len <= bltaBytes.Length);

        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(bltaBytes, seg0Start, signedContent, 0, seg0Len);
        Buffer.BlockCopy(bltaBytes, seg1Start, signedContent, seg0Len, seg1Len);

        var contentsBytes = origSig.Contents.ToArray();
        var verify = new SignedCms(new ContentInfo(signedContent), detached: true);
        verify.Decode(contentsBytes);
        verify.CheckSignature(verifySignatureOnly: true);
        // Reaching here means CheckSignature did not throw — original signature is intact.
    }

    // ── Test: /DSS still present after archive timestamp append ──────────────────

    [Fact]
    public void Dss_still_present_after_archive_timestamp()
    {
        using var cert = CreateSelfSignedCertificate();
        var ltvBytes = BuildBLtPdf(cert);
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var bltaBytes = ArchiveTimestampBuilder.AddArchiveTimestamp(ltvBytes, tsaClient);

        using var reader = PdfReader.Open(bltaBytes);
        var dssRaw = reader.Catalog.Get(new PdfName("DSS"));
        Assert.NotNull(dssRaw);
    }

    // ── Test: /DocTimeStamp1 field is present in /AcroForm /Fields ───────────────

    [Fact]
    public void DocTimeStamp_field_present_in_acroform_fields()
    {
        using var cert = CreateSelfSignedCertificate();
        var ltvBytes = BuildBLtPdf(cert);
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var bltaBytes = ArchiveTimestampBuilder.AddArchiveTimestamp(ltvBytes, tsaClient);

        using var reader = PdfReader.Open(bltaBytes);
        var acroFormRaw = reader.Catalog.Get(new PdfName("AcroForm"));
        Assert.NotNull(acroFormRaw);

        var acroForm = (acroFormRaw is PdfIndirectReference ar
            ? reader.Resolve(ar.ObjectNumber)
            : acroFormRaw) as PdfDictionary;
        Assert.NotNull(acroForm);

        var fieldsRaw = acroForm.Get(new PdfName("Fields"));
        Assert.NotNull(fieldsRaw);
        var fields = fieldsRaw as PdfArray;
        Assert.NotNull(fields);
        Assert.True(fields.Count >= 2, "AcroForm /Fields must have at least 2 entries after DocTimeStamp append.");

        // Verify that a /SigFlags 3 is still present.
        var sigFlagsRaw = acroForm.Get(new PdfName("SigFlags"));
        Assert.NotNull(sigFlagsRaw);
        var sigFlags = sigFlagsRaw as PdfInteger;
        Assert.NotNull(sigFlags);
        Assert.Equal(3L, sigFlags.Value);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

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
