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
/// Regression tests for the v1.5.3 crypto hardening findings (#81b, #81c).
/// All inputs are synthesised in-memory; no external resources are required.
/// </summary>
public sealed class HardeningV153Tests
{
    // ── #81b: /Contents located unambiguously via sentinel ──────────────────

    /// <summary>
    /// A document whose Reason and Location contain the literal text
    /// "/Contents &lt;0000&gt;" must sign correctly — the decoy must not
    /// derail the /Contents placeholder patch.
    /// </summary>
    [Fact]
    public void Signing_reason_containing_contents_decoy_still_produces_valid_signature()
    {
        using var cert = CreateTestCertificate();

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            Reason = "Approved /Contents <0000> for distribution",
            Location = "Dept /Contents <FFFF> HQ",
            SignerName = "Tester /Contents <AAAA>",
        };

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText()
              .SetFont(font, 12)
              .SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("decoy-test")
              .EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Sign(ms, settings);
        var signedBytes = ms.ToArray();

        // BCL CheckSignature must pass — if the wrong /Contents offset was patched
        // the signature would be over the wrong byte range and verification would fail.
        VerifySignatureOrThrow(signedBytes);
    }

    /// <summary>
    /// Verifies the /Contents size guard fires when the computed CMS signature exceeds
    /// the reserved space. A tiny <see cref="PdfSignatureSettings.EstimatedSignatureSizeBytes"/>
    /// (1 byte) reaches the size-exceeded guard, which confirms the /Contents placeholder
    /// was located (via the /ByteRange anchor) before the size was validated.
    /// </summary>
    [Fact]
    public void Sign_throws_when_signature_exceeds_reserved_contents_space()
    {
        using var cert = CreateTestCertificate();
        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            EstimatedSignatureSizeBytes = 1, // forces the size-exceeded guard
        };

        using var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();
        // Must throw about the reserved signature SIZE — reaching that guard confirms the
        // /Contents placeholder was found first via the /ByteRange anchor.
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Sign(ms, settings));
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── #81c: signing time is unified across /M and CMS Pkcs9SigningTime ────

    /// <summary>
    /// When <see cref="PdfSignatureSettings.SigningTime"/> is not set, the /M value
    /// written to the signature dictionary and the Pkcs9SigningTime attribute embedded
    /// in the CMS structure must represent the same second.
    ///
    /// Previously each called <see cref="DateTimeOffset.UtcNow"/> independently, so
    /// they could differ by up to a second under load. Now the time is resolved once
    /// in <see cref="SigningExtensions.Sign"/> before both downstream calls.
    /// </summary>
    [Fact]
    public void Signing_without_explicit_time_M_and_CmsSigningTime_match_to_the_second()
    {
        using var cert = CreateTestCertificate();

        // Do not set SigningTime — the fix should resolve it once.
        var settings = new PdfSignatureSettings { Certificate = cert };

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Sign(ms, settings);
        var signedBytes = ms.ToArray();

        // ── Extract /M value from the raw bytes ──────────────────────────────
        var text = Encoding.Latin1.GetString(signedBytes);
        const string mKey = "/M (D:";
        var mStart = text.IndexOf(mKey, StringComparison.Ordinal);
        Assert.True(mStart >= 0, "/M not found in signed PDF");
        // PDF date format: D:YYYYMMDDHHmmSS+00'00'
        // Extract just YYYYMMDDHHmmSS (14 digits after "D:")
        var dateStart = mStart + "/M (D:".Length;
        var dateStr = text.Substring(dateStart, 14); // YYYYMMDDHHmmSS
        var mTime = DateTime.ParseExact(dateStr, "yyyyMMddHHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

        // ── Extract Pkcs9SigningTime from the CMS structure ──────────────────
        var (byteRange, contentsHex) = ParseContentsHex(signedBytes);
        var seg0Len = (int)byteRange[1];
        var seg1Start = (int)byteRange[2];
        var seg1Len = (int)byteRange[3];
        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(signedBytes, 0, signedContent, 0, seg0Len);
        Buffer.BlockCopy(signedBytes, seg1Start, signedContent, seg0Len, seg1Len);

        var derBytes = Convert.FromHexString(contentsHex);
        var cms = new SignedCms(new ContentInfo(signedContent), detached: true);
        cms.Decode(derBytes);

        DateTime? cmsTime = null;
        foreach (var attr in cms.SignerInfos[0].SignedAttributes)
        {
            if (attr.Oid?.Value == "1.2.840.113549.1.9.5") // id-signingTime
            {
                var pkcs9 = new Pkcs9SigningTime(attr.Values[0].RawData);
                cmsTime = pkcs9.SigningTime.ToUniversalTime();
                break;
            }
        }

        Assert.True(cmsTime.HasValue, "Pkcs9SigningTime attribute not found in CMS SignerInfo");

        // Both times must match to the second.
        // PDF date has second precision; CMS signing time also has second precision.
        Assert.Equal(mTime, cmsTime!.Value.TruncateToSecond());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf HardeningV153 Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>
    /// Parses /ByteRange and /Contents from signed PDF bytes.
    /// Returns the four ByteRange values and the raw hex string of /Contents.
    /// </summary>
    private static (long[] ByteRange, string ContentsHex) ParseContentsHex(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);

        const string byteRangeMarker = "/ByteRange [";
        var brStart = text.IndexOf(byteRangeMarker, StringComparison.Ordinal);
        Assert.True(brStart >= 0, "/ByteRange not found in signed PDF");
        var brBracket = brStart + byteRangeMarker.Length - 1;
        var brEnd = text.IndexOf(']', brBracket);
        Assert.True(brEnd >= 0, "/ByteRange closing ']' not found");
        var brParts = text[(brBracket + 1)..brEnd].Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, brParts.Length);
        var byteRange = brParts.Select(long.Parse).ToArray();

        // Locate the '<' of the /Contents hex string by anchoring on /ByteRange:
        // the first '<' after the ByteRange ']' is the /Contents opening angle bracket.
        var posLt = text.IndexOf('<', brEnd);
        Assert.True(posLt >= 0, "/Contents '<' not found after /ByteRange");
        var cEnd = text.IndexOf('>', posLt);
        Assert.True(cEnd >= 0, "/Contents closing '>' not found");
        var hexContent = text[(posLt + 1)..cEnd];

        return (byteRange, hexContent);
    }

    private static void VerifySignatureOrThrow(byte[] signedBytes)
    {
        var (byteRange, hexContent) = ParseContentsHex(signedBytes);

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

file static class DateTimeExtensions
{
    internal static DateTime TruncateToSecond(this DateTime dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, DateTimeKind.Utc);
}
