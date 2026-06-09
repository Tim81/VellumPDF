// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Golden / snapshot tests using Verify.XunitV3 (issue #5).
/// Raw-byte snapshots: font-free deterministic docs (tests 1–3).
/// Structural projections: cases where output isn't byte-stable cross-platform (tests 4–5).
/// </summary>
public sealed class GoldenTests
{
    private static readonly DateTimeOffset PinnedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] PinnedId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    // ── 1. StandardFont_rawBytes ──────────────────────────────────────────────

    [Fact]
    public async Task StandardFont_rawBytes()
    {
        var b1 = BuildStandardFontDoc();
        var b2 = BuildStandardFontDoc();
        Assert.True(b1.SequenceEqual(b2), "StandardFont doc must be byte-identical across two builds");

        await Verify(new MemoryStream(b1), "pdf");
    }

    private static byte[] BuildStandardFontDoc()
    {
        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
        };
        doc.Info.Title = "GoldenStandardFont";

        var page = doc.AddPage(PageSize.A4);
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas
            .BeginText()
            .SetFont(font, 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowText("Hello, VellumPdf golden test!")
            .EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── 2. PdfA2b_rawBytes ───────────────────────────────────────────────────

    [Fact]
    public async Task PdfA2b_rawBytes()
    {
        var b1 = BuildPdfA2bDoc();
        var b2 = BuildPdfA2bDoc();
        Assert.True(b1.SequenceEqual(b2), "PDF/A-2b doc must be byte-identical across two builds");

        await Verify(new MemoryStream(b1), "pdf");
    }

    private static byte[] BuildPdfA2bDoc()
    {
        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
            Conformance = PdfConformance.PdfA2b,
        };
        doc.Info.Title = "GoldenPdfA2b";

        var page = doc.AddPage(PageSize.A4);
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas
            .BeginText()
            .SetFont(font, 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowText("PDF/A-2b golden snapshot")
            .EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── 3. Signed_prepareForSigningPlaceholder ───────────────────────────────

    [Fact]
    public async Task Signed_prepareForSigningPlaceholder()
    {
        var b1 = BuildSignedPlaceholderDoc();
        var b2 = BuildSignedPlaceholderDoc();
        Assert.True(b1.SequenceEqual(b2), "Signing placeholder doc must be byte-identical across two builds");

        await Verify(new MemoryStream(b1), "pdf");
    }

    private static byte[] BuildSignedPlaceholderDoc()
    {
        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
        };
        doc.Info.Title = "GoldenSigningPlaceholder";

        var page = doc.AddPage(PageSize.A4);
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas
            .BeginText()
            .SetFont(font, 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowText("Signing placeholder golden test")
            .EndText();
        canvas.Finish();

        return doc.PrepareForSigning(new SignaturePlaceholderOptions
        {
            SigningTime = PinnedTime,
            SignerName = "GoldenTest",
        });
    }

    // ── 4. EmbeddedFont_projection ───────────────────────────────────────────

    [Fact]
    public async Task EmbeddedFont_projection()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null)
            return; // skip — no platform font available

        var fontBytes = File.ReadAllBytes(fontPath);

        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
        };
        doc.Info.Title = "GoldenEmbeddedFont";

        var page = doc.AddPage(PageSize.A4);
        var handle = doc.UseTrueTypeFont(fontBytes);
        doc.RegisterEmbeddedFontUsage(page, handle);

        var canvas = new PdfCanvas(page);
        canvas.BeginText()
              .SetFontByName(handle.ResourceName, 12)
              .SetTextMatrix(1, 0, 0, 1, 72, 720);

        var text = "Golden embedded font test";
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        var pdfText = Encoding.Latin1.GetString(bytes);

        var hasType0 = pdfText.Contains("/Type0");
        var hasCIDFontType2 = pdfText.Contains("/CIDFontType2");
        var hasIdentityH = pdfText.Contains("/Identity-H");
        var hasFontFile2 = pdfText.Contains("/FontFile2");
        var hasToUnicode = pdfText.Contains("/ToUnicode");
        var hasSubsetTag = Regex.IsMatch(pdfText, @"/BaseFont\s*/[A-Z]{6}\+");

        var projection = $"""
            HasType0: {hasType0}
            HasCIDFontType2: {hasCIDFontType2}
            HasIdentityH: {hasIdentityH}
            HasFontFile2: {hasFontFile2}
            HasToUnicode: {hasToUnicode}
            HasSubsetTag: {hasSubsetTag}
            """;

        await Verify(projection);
    }

    // ── 5. Encrypted_projection ───────────────────────────────────────────────

    [Fact]
    public async Task Encrypted_projection()
    {
        // AES uses a random file key — two saves must differ (non-deterministic)
        var bytes1 = BuildEncryptedDoc();
        var bytes2 = BuildEncryptedDoc();
        Assert.False(bytes1.SequenceEqual(bytes2), "Encrypted output must differ across two saves (random AES key)");

        var pdfText = Encoding.Latin1.GetString(bytes1);
        var hasEncrypt = pdfText.Contains("/Encrypt");
        var filterStandard = pdfText.Contains("/Filter /Standard");
        var v5 = pdfText.Contains("/V 5");
        var r6 = pdfText.Contains("/R 6");
        var hasAESV3 = pdfText.Contains("/AESV3");
        var hasStmF = pdfText.Contains("/StmF");
        var hasStrF = pdfText.Contains("/StrF");

        // Extract /P value (permissions integer)
        var pMatch = Regex.Match(pdfText, @"/P (-?\d+)");
        var permissionsValue = pMatch.Success ? pMatch.Value : "/P <not found>";

        // Count indirect objects (lines matching "N 0 obj")
        var objectCount = Regex.Matches(pdfText, @"\d+ 0 obj").Count;

        var projection = $"""
            HasEncrypt: {hasEncrypt}
            FilterStandard: {filterStandard}
            V5: {v5}
            R6: {r6}
            HasAESV3: {hasAESV3}
            HasStmF: {hasStmF}
            HasStrF: {hasStrF}
            Permissions: {permissionsValue}
            IndirectObjectCount: {objectCount}
            """;

        await Verify(projection);
    }

    private static byte[] BuildEncryptedDoc()
    {
        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
        };
        doc.Info.Title = "GoldenEncrypted";

        var page = doc.AddPage(PageSize.A4);
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas
            .BeginText()
            .SetFont(font, 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowText("Encrypted golden test")
            .EndText();
        canvas.Finish();

        doc.Encrypt(new PdfEncryptionSettings
        {
            UserPassword = "golden",
            OwnerPassword = "owner",
            Permissions = PdfPermissions.All,
        });

        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? FindPlatformFont()
    {
        string[] candidates =
        [
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }
}
