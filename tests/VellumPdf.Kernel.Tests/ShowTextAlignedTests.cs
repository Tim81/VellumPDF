// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for <see cref="PdfCanvas.ShowTextAligned"/> and <see cref="PdfCanvas.ShowGlyphsAligned"/>.
/// </summary>
public sealed class ShowTextAlignedTests
{
    private const string TestText = "Hello";
    private const double FontSize = 12.0;
    private const double X = 200.0;
    private const double Y = 500.0;

    // ── ShowTextAligned: Left alignment ──────────────────────────────────────

    [Fact]
    public void ShowTextAligned_Left_emitsTmAtX()
    {
        var ops = BuildAndDecompress((doc, canvas) =>
        {
            var font = doc.UseFont(Standard14.Helvetica);
            canvas.BeginText()
                  .SetFont(font, FontSize)
                  .ShowTextAligned(TestText, X, Y, TextAlignment.Left)
                  .EndText();
        });

        // For Left, xAdjusted == X
        Assert.Contains(TmToken(X, Y), ops, StringComparison.Ordinal);
    }

    // ── ShowTextAligned: Center alignment ────────────────────────────────────

    [Fact]
    public void ShowTextAligned_Center_emitsTmAtXMinusHalfWidth()
    {
        var w = Standard14Metrics.MeasureString(Standard14.Helvetica, TestText, FontSize);
        var expectedX = X - w / 2.0;

        var ops = BuildAndDecompress((doc, canvas) =>
        {
            var font = doc.UseFont(Standard14.Helvetica);
            canvas.BeginText()
                  .SetFont(font, FontSize)
                  .ShowTextAligned(TestText, X, Y, TextAlignment.Center)
                  .EndText();
        });

        Assert.Contains(TmToken(expectedX, Y), ops, StringComparison.Ordinal);
    }

    // ── ShowTextAligned: Right alignment ─────────────────────────────────────

    [Fact]
    public void ShowTextAligned_Right_emitsTmAtXMinusWidth()
    {
        var w = Standard14Metrics.MeasureString(Standard14.Helvetica, TestText, FontSize);
        var expectedX = X - w;

        var ops = BuildAndDecompress((doc, canvas) =>
        {
            var font = doc.UseFont(Standard14.Helvetica);
            canvas.BeginText()
                  .SetFont(font, FontSize)
                  .ShowTextAligned(TestText, X, Y, TextAlignment.Right)
                  .EndText();
        });

        Assert.Contains(TmToken(expectedX, Y), ops, StringComparison.Ordinal);
    }

    // ── ShowTextAligned: default is Left ─────────────────────────────────────

    [Fact]
    public void ShowTextAligned_DefaultAlign_isSameAsLeft()
    {
        var ops = BuildAndDecompress((doc, canvas) =>
        {
            var font = doc.UseFont(Standard14.Helvetica);
            canvas.BeginText()
                  .SetFont(font, FontSize)
                  .ShowTextAligned(TestText, X, Y)
                  .EndText();
        });

        // Default (Left) must place Tm at X unchanged.
        Assert.Contains(TmToken(X, Y), ops, StringComparison.Ordinal);
    }

    // ── ShowTextAligned: throws when no measurable font ──────────────────────

    [Fact]
    public void ShowTextAligned_NoFontSet_throwsInvalidOperationException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        canvas.BeginText();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            canvas.ShowTextAligned(TestText, X, Y));

        Assert.Contains("SetFont", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowTextAligned_AfterSetFontByName_throwsInvalidOperationException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        // SetFontByName clears the retained PdfFontResource.
        canvas.BeginText();
        canvas.SetFontByName("F1", FontSize);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            canvas.ShowTextAligned(TestText, X, Y));

        Assert.Contains("SetFont", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── ShowTextAligned: fluent chaining ─────────────────────────────────────

    [Fact]
    public void ShowTextAligned_ReturnsSameCanvas()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);

        canvas.BeginText().SetFont(font, FontSize);
        var returned = canvas.ShowTextAligned(TestText, X, Y);

        Assert.Same(canvas, returned);
    }

    // ── ShowGlyphsAligned: Left alignment ────────────────────────────────────

    [Fact]
    public void ShowGlyphsAligned_Left_emitsTmAtX()
    {
        var glyphs = new ushort[] { 0x0001, 0x0002 };
        const double measuredWidth = 50.0;

        var ops = BuildAndDecompress((doc, canvas) =>
        {
            canvas.BeginText()
                  .SetFontByName("F1", FontSize)
                  .ShowGlyphsAligned(glyphs, measuredWidth, X, Y, TextAlignment.Left)
                  .EndText();
        });

        Assert.Contains(TmToken(X, Y), ops, StringComparison.Ordinal);
    }

    // ── ShowGlyphsAligned: Center alignment ──────────────────────────────────

    [Fact]
    public void ShowGlyphsAligned_Center_emitsTmAtXMinusHalfWidth()
    {
        var glyphs = new ushort[] { 0x0001 };
        const double measuredWidth = 80.0;
        var expectedX = X - measuredWidth / 2.0;

        var ops = BuildAndDecompress((doc, canvas) =>
        {
            canvas.BeginText()
                  .SetFontByName("F1", FontSize)
                  .ShowGlyphsAligned(glyphs, measuredWidth, X, Y, TextAlignment.Center)
                  .EndText();
        });

        Assert.Contains(TmToken(expectedX, Y), ops, StringComparison.Ordinal);
    }

    // ── ShowGlyphsAligned: Right alignment ───────────────────────────────────

    [Fact]
    public void ShowGlyphsAligned_Right_emitsTmAtXMinusWidth()
    {
        var glyphs = new ushort[] { 0x0001 };
        const double measuredWidth = 100.0;
        var expectedX = X - measuredWidth;

        var ops = BuildAndDecompress((doc, canvas) =>
        {
            canvas.BeginText()
                  .SetFontByName("F1", FontSize)
                  .ShowGlyphsAligned(glyphs, measuredWidth, X, Y, TextAlignment.Right)
                  .EndText();
        });

        Assert.Contains(TmToken(expectedX, Y), ops, StringComparison.Ordinal);
    }

    // ── ShowGlyphsAligned: fluent chaining ───────────────────────────────────

    [Fact]
    public void ShowGlyphsAligned_ReturnsSameCanvas()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        canvas.BeginText().SetFontByName("F1", FontSize);
        var returned = canvas.ShowGlyphsAligned(new ushort[] { 0x0001 }, 50.0, X, Y);

        Assert.Same(canvas, returned);
    }

    // ── SetFont / SetFontByName retained-font transitions ────────────────────

    [Fact]
    public void SetFont_thenSetFontByName_clearsRetainedFont()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);

        canvas.BeginText();
        canvas.SetFont(font, FontSize);
        canvas.SetFontByName("F1", FontSize);

        // After switching to SetFontByName the retained font must be gone.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            canvas.ShowTextAligned(TestText, X, Y));

        Assert.Contains("SetFont", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats the expected Tm token fragment using the same number formatter as
    /// PdfCanvas (format "0.#####", invariant culture, no trailing zeros).
    /// </summary>
    private static string TmToken(double x, double y) =>
        $"1 0 0 1 {N(x)} {N(y)} Tm";

    private static string N(double v) =>
        v.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>Builds a minimal PDF, runs <paramref name="draw"/> on the canvas, and returns the decompressed content stream.</summary>
    private static string BuildAndDecompress(Action<PdfDocument, PdfCanvas> draw)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        draw(doc, canvas);
        canvas.Finish();
        var ms = new MemoryStream();
        doc.Save(ms);
        return DecompressContentStream(ms.ToArray());
    }

    private static string DecompressContentStream(byte[] pdfBytes)
    {
        var streamStart = FindSequence(pdfBytes, "\nstream\n"u8);
        Assert.True(streamStart >= 0, "No stream found in PDF");

        var dataStart = streamStart + 8;

        var streamEnd = FindSequence(pdfBytes, "\nendstream"u8, dataStart);
        Assert.True(streamEnd >= 0, "No endstream found in PDF");

        var compressed = pdfBytes[dataStart..streamEnd];

        using var zms = new MemoryStream(compressed);
        using var z = new ZLibStream(zms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        z.CopyTo(result);
        return Encoding.ASCII.GetString(result.ToArray());
    }

    private static int FindSequence(byte[] haystack, ReadOnlySpan<byte> needle, int startAt = 0)
    {
        for (var i = startAt; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }
}
