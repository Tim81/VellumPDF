// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Signing;

namespace VellumPdf.Layout.Tests;

public sealed class DocumentTests
{
    [Fact]
    public void Save_singleParagraph_producesValidPdf()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Hello, VellumPdf layout engine!"));

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Assert.True(bytes.Length > 100);
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void Save_withMetadata_includesInfoDict()
    {
        using var doc = new Document();
        doc.Info.Title = "Layout Test";
        doc.Info.Author = "Test";
        doc.Add("Some content");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        Assert.Contains("/Info", content);
    }

    [Fact]
    public void Save_longText_createsTwoPages()
    {
        using var doc = new Document();
        // ~40 paragraphs of text should overflow one A4 page at 12pt
        for (var i = 0; i < 50; i++)
            doc.Add($"Paragraph number {i + 1}: The quick brown fox jumps over the lazy dog.");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        // At least 2 pages → /Count must be >= 2
        Assert.DoesNotContain("/Count 1\n", content);
    }

    [Fact]
    public void Save_lineSeparator_succeeds()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Before"));
        doc.Add(new LineSeparator());
        doc.Add(new Paragraph("After"));

        var ms = new MemoryStream();
        doc.Save(ms); // just verifies no exception
        Assert.True(ms.Length > 0);
    }

    // ── TextEncodingWarnings ────────────────────────────────────────────────

    [Fact]
    public void Save_charOutsideWinAnsi_surfacesTextEncodingWarning()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Black star: ★")); // ★ is outside WinAnsiEncoding

        var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Single(doc.TextEncodingWarnings);
        Assert.Equal('★', doc.TextEncodingWarnings[0].Character);
    }

    [Fact]
    public void Save_winAnsiOnlyContent_hasNoTextEncodingWarnings()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Café • 15° – all in WinAnsi"));

        var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Empty(doc.TextEncodingWarnings);
    }

    [Fact]
    public void Save_outOfWinAnsiCharsOnTwoPages_aggregatesWarningsFromBothPages()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Page one marker: ★")); // ★ (U+2605) stays on page 1

        // Filler paragraphs to force a second page (mirrors Save_longText_createsTwoPages).
        for (var i = 0; i < 50; i++)
            doc.Add($"Paragraph number {i + 1}: The quick brown fox jumps over the lazy dog.");

        doc.Add(new Paragraph("Page two marker: ♥")); // ♥ (U+2665) is the last paragraph added

        var ms = new MemoryStream();
        doc.Save(ms);

        // Both characters must be reported — proves warnings accumulate across every page's
        // canvas rather than being overwritten by the last page finished.
        Assert.Equal(2, doc.TextEncodingWarnings.Count);
        Assert.Contains(doc.TextEncodingWarnings, w => w.Character == '★');
        Assert.Contains(doc.TextEncodingWarnings, w => w.Character == '♥');
    }

    [Fact]
    public void Sign_charOutsideWinAnsi_surfacesTextEncodingWarning()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        using var doc = new Document();
        doc.Add(new Paragraph("Black star: ★")); // ★ is outside WinAnsiEncoding

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            SignerName = "Tester",
            Reason = "Unit test",
        };

        var ms = new MemoryStream();
        doc.Sign(ms, settings);

        // The signing path (Document.PrepareForSigning) must surface the same warning
        // the plain Save(Stream) path does.
        Assert.Single(doc.TextEncodingWarnings);
        Assert.Equal('★', doc.TextEncodingWarnings[0].Character);
    }
}
