// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Encryption;
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
        Assert.Equal(new Rune('★'), doc.TextEncodingWarnings[0].Character);
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
        Assert.Contains(doc.TextEncodingWarnings, w => w.Character == new Rune('★'));
        Assert.Contains(doc.TextEncodingWarnings, w => w.Character == new Rune('♥'));
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
        Assert.Equal(new Rune('★'), doc.TextEncodingWarnings[0].Character);
    }

    // ── Async I/O surface (#54) ────────────────────────────────────────────────
    //
    // With Timestamp and DocumentId pinned, the async overloads write the same bytes as the
    // synchronous one (SaveAsync_pinned_allSavePathsGiveSameBytes); the remaining tests check
    // that the output is a valid PDF.

    [Fact]
    public async Task SaveAsync_pinned_allSavePathsGiveSameBytes()
    {
        static Document Build()
        {
            var doc = new Document
            {
                Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
                DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            };
            doc.Add(new Paragraph("Same bytes on every save path"));
            return doc;
        }

        var sync = new MemoryStream();
        using (var doc = Build())
            doc.Save(sync);

        var viaStream = new MemoryStream();
        using (var doc = Build())
            await doc.SaveAsync(viaStream, TestContext.Current.CancellationToken);

        var path = Path.GetTempFileName();
        try
        {
            using (var doc = Build())
                await doc.SaveAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(sync.ToArray(), viaStream.ToArray());
            Assert.Equal(sync.ToArray(), await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_stream_producesValidPdf()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Async save via stream"));

        var ms = new MemoryStream();
        await doc.SaveAsync(ms, TestContext.Current.CancellationToken);
        var bytes = ms.ToArray();

        Assert.True(bytes.Length > 100);
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public async Task SaveAsync_stringPath_writesReadableFile()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Async save via file path"));

        var path = Path.GetTempFileName();
        try
        {
            await doc.SaveAsync(path, TestContext.Current.CancellationToken);
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

            Assert.True(bytes.Length > 100);
            Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SignAsync_charOutsideWinAnsi_surfacesTextEncodingWarning()
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
        await doc.SignAsync(ms, settings, TestContext.Current.CancellationToken);

        Assert.Single(doc.TextEncodingWarnings);
        Assert.Equal(new Rune('★'), doc.TextEncodingWarnings[0].Character);
        Assert.True(ms.Length > 100);
    }

    /// <summary>
    /// <see cref="Document.Encrypt"/> delegates to <c>PdfDocument.Encrypt</c>, which carries the
    /// #211 guard against an empty <c>OwnerPassword</c> beside a real <c>UserPassword</c> — the
    /// shape that would seal <c>/O</c> under nothing and open the document to anyone at owner
    /// privilege. The guard itself is pinned at the two lower-level entry points in
    /// <c>VellumPdf.Kernel.Tests.EncryptionTests</c>; this only confirms the Stable, most-used
    /// entry point actually reaches it rather than swallowing or bypassing it on the way through.
    /// </summary>
    [Fact]
    public void Encrypt_withEmptyOwnerPassword_besideARealUserPassword_throws()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Hello"));

        var settings = new PdfEncryptionSettings
        {
            UserPassword = "hunter2",
            OwnerPassword = string.Empty,
        };

        var exception = Assert.Throws<ArgumentException>(() => doc.Encrypt(settings));
        Assert.Equal("settings", exception.ParamName);
    }
}
