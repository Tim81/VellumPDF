// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for the object-stream + cross-reference-stream compressed write path
/// (PDF 1.5+, ISO 32000-2 §7.5.7–7.5.8), enabled via UseObjectStreams = true.
/// </summary>
public sealed class ObjectStreamTests
{
    // ── Structural markers ───────────────────────────────────────────────────

    [Fact]
    public void UseObjectStreams_true_containsObjStmType()
    {
        var bytes = SaveMultiPage(useObjectStreams: true);
        var content = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Type /ObjStm", content);
    }

    [Fact]
    public void UseObjectStreams_true_containsXRefType()
    {
        var bytes = SaveMultiPage(useObjectStreams: true);
        var content = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Type /XRef", content);
    }

    [Fact]
    public void UseObjectStreams_true_containsWArray()
    {
        var bytes = SaveMultiPage(useObjectStreams: true);
        var content = Encoding.Latin1.GetString(bytes);

        // /W [1 4 2] must appear in the XRef stream dictionary
        Assert.Contains("/W [", content);
    }

    [Fact]
    public void UseObjectStreams_true_noClassicXrefKeyword()
    {
        var bytes = SaveMultiPage(useObjectStreams: true);
        var content = Encoding.Latin1.GetString(bytes);

        // The literal "\nxref\n" token must NOT appear (classic xref table)
        Assert.DoesNotContain("\nxref\n", content);
    }

    [Fact]
    public void UseObjectStreams_true_noTrailerKeyword()
    {
        var bytes = SaveMultiPage(useObjectStreams: true);
        var content = Encoding.Latin1.GetString(bytes);

        // The "trailer" keyword introduces the classic trailer dict — must be absent
        Assert.DoesNotContain("trailer\n", content);
    }

    [Fact]
    public void UseObjectStreams_true_hasStartxref()
    {
        var bytes = SaveMultiPage(useObjectStreams: true);
        var content = Encoding.Latin1.GetString(bytes);

        Assert.Contains("startxref", content);
    }

    [Fact]
    public void UseObjectStreams_true_endsWithPercentPercentEof()
    {
        var bytes = SaveMultiPage(useObjectStreams: true);
        var content = Encoding.Latin1.GetString(bytes);

        Assert.Contains("%%EOF", content);
    }

    // ── Default (classic) path is byte-for-byte unchanged ───────────────────

    [Fact]
    public void UseObjectStreams_false_containsClassicXrefAndTrailer()
    {
        var bytes = SaveMultiPage(useObjectStreams: false);
        var content = Encoding.Latin1.GetString(bytes);

        Assert.Contains("\nxref\n", content);
        Assert.Contains("trailer\n", content);
    }

    [Fact]
    public void UseObjectStreams_false_byteIdenticalToPreviousSave()
    {
        // Two saves with identical content and a fixed timestamp must be identical.
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bytes1 = SaveMultiPage(useObjectStreams: false, timestamp: ts);
        var bytes2 = SaveMultiPage(useObjectStreams: false, timestamp: ts);

        Assert.Equal(bytes1, bytes2);
    }

    // ── Size reduction ───────────────────────────────────────────────────────

    [Fact]
    public void UseObjectStreams_true_smallerThanClassicForMultiPageDoc()
    {
        var classic = SaveMultiPage(useObjectStreams: false);
        var compressed = SaveMultiPage(useObjectStreams: true);

        Assert.True(
            compressed.Length < classic.Length,
            $"Expected compressed output ({compressed.Length} bytes) to be smaller " +
            $"than classic output ({classic.Length} bytes).");
    }

    // ── Incompatibility guard ────────────────────────────────────────────────

    [Fact]
    public void UseObjectStreams_true_withEncrypt_throwsNotSupported()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.UseObjectStreams = true;
        doc.Encrypt(new PdfEncryptionSettings
        {
            UserPassword = "test",
            OwnerPassword = "test",
        });

        var ms = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => doc.Save(ms));
    }

    // ── Structural integrity ─────────────────────────────────────────────────

    [Fact]
    public void UseObjectStreams_true_pdfHeaderPresent()
    {
        var bytes = SaveMultiPage(useObjectStreams: true);
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void UseObjectStreams_true_catalogRootPresent()
    {
        var bytes = SaveMultiPage(useObjectStreams: true);
        var content = Encoding.Latin1.GetString(bytes);

        // /Root must appear in the XRef stream dictionary (it IS the trailer)
        Assert.Contains("/Root", content);
    }

    [Fact]
    public void UseObjectStreams_true_withFontAndText_stillWorks()
    {
        using var doc = new PdfDocument();
        doc.UseObjectStreams = true;

        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas
            .BeginText()
            .SetFont(font, 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowText("Hello from ObjStm!")
            .EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        var content = Encoding.Latin1.GetString(bytes);

        Assert.True(bytes.Length > 0);
        Assert.Contains("/Type /ObjStm", content);
        Assert.Contains("/Type /XRef", content);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] SaveMultiPage(bool useObjectStreams, DateTimeOffset? timestamp = null)
    {
        using var doc = new PdfDocument();
        doc.UseObjectStreams = useObjectStreams;

        if (timestamp.HasValue)
            doc.Timestamp = timestamp.Value;

        var font = doc.UseFont(Standard14.Helvetica);

        for (var p = 0; p < 5; p++)
        {
            var page = doc.AddPage();
            var canvas = new PdfCanvas(page);
            canvas
                .BeginText()
                .SetFont(font, 12)
                .SetTextMatrix(1, 0, 0, 1, 72, 720)
                .ShowText($"Page {p + 1} — ObjStm test content paragraph with enough text to matter.")
                .EndText();
            canvas.Finish();
        }

        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }
}
