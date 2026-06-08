// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Graphics;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for graphics primitives and colour features added in Phase 2:
/// transparency/ExtGState (feature 1), clipping (feature 2),
/// dash patterns (feature 3), CMYK colour (feature 4),
/// and axial/radial shadings (feature 5).
///
/// Resource dictionaries are uncompressed and can be asserted with a string
/// contains check on the full PDF bytes.  Content stream operators are
/// FlateDecode-compressed; those tests decompress the stream to inspect operators.
/// </summary>
public sealed class GraphicsPrimitivesTests
{
    // ── Feature 1: Transparency / ExtGState ──────────────────────────────────

    [Fact]
    public void SetFillAlpha_registersExtGStateWithCa()
    {
        var pdfBytes = BuildPdf(canvas => canvas.SetFillAlpha(0.5));
        var text = Latin1(pdfBytes);

        Assert.Contains("/ExtGState", text);
        Assert.Contains("/ca", text);
    }

    [Fact]
    public void SetStrokeAlpha_registersExtGStateWithCA()
    {
        var pdfBytes = BuildPdf(canvas => canvas.SetStrokeAlpha(0.75));
        var text = Latin1(pdfBytes);

        Assert.Contains("/ExtGState", text);
        Assert.Contains("/CA", text);
    }

    [Fact]
    public void SetFillAlpha_emitsGsOperator()
    {
        var pdfBytes = BuildPdf(canvas => canvas.SetFillAlpha(0.5));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("/GS1 gs", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetStrokeAlpha_emitsGsOperator()
    {
        var pdfBytes = BuildPdf(canvas => canvas.SetStrokeAlpha(1.0));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("gs", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetFillAlpha_sameValue_deduplicatesResource()
    {
        // Two calls with same alpha must produce only one ExtGState resource (GS1).
        var pdfBytes = BuildPdf(canvas =>
        {
            canvas.SetFillAlpha(0.5);
            canvas.SetFillAlpha(0.5);
        });
        var text = Latin1(pdfBytes);

        // GS1 should appear (the resource name); GS2 must NOT appear.
        Assert.Contains("/GS1", text);
        Assert.DoesNotContain("/GS2", text);
    }

    [Fact]
    public void SetFillAndStrokeAlpha_differentValues_twoResources()
    {
        var pdfBytes = BuildPdf(canvas =>
        {
            canvas.SetFillAlpha(0.3);
            canvas.SetStrokeAlpha(0.7);
        });
        var text = Latin1(pdfBytes);

        Assert.Contains("/GS1", text);
        Assert.Contains("/GS2", text);
    }

    // ── Feature 2: Clipping ──────────────────────────────────────────────────

    [Fact]
    public void Clip_emitsWOperator()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.Rectangle(10, 10, 100, 100).Clip().EndPath());
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("W\n", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipEvenOdd_emitsWStar()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.Rectangle(10, 10, 100, 100).ClipEvenOdd().EndPath());
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("W*\n", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipAndGradient_resourcesPresent()
    {
        // Verify that combining clip + shading round-trips correctly.
        var pdfBytes = BuildPdf(canvas =>
        {
            canvas.SaveState();
            canvas.Rectangle(0, 0, 200, 200).Clip().EndPath();
            canvas.PaintAxialGradient(0, 0, 200, 0,
                new KernelColor(1, 0, 0), new KernelColor(0, 0, 1));
            canvas.RestoreState();
        });
        var text = Latin1(pdfBytes);

        Assert.Contains("/Shading", text);
        Assert.Contains("/ShadingType 2", text);
    }

    // ── Feature 3: Dash patterns ────────────────────────────────────────────

    [Fact]
    public void SetLineDash_emitsDashOperator()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.SetLineDash([5, 3], 0));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("[5 3] 0 d", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetLineDash_withPhase_includesPhase()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.SetLineDash([10, 5], 2));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("[10 5] 2 d", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetSolidLine_emitsEmptyDash()
    {
        var pdfBytes = BuildPdf(canvas => canvas.SetSolidLine());
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("[] 0 d", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetLineDash_emptyPattern_emitsEmptyArray()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.SetLineDash([], 0));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("[] 0 d", ops, StringComparison.Ordinal);
    }

    // ── Feature 4: CMYK colour ──────────────────────────────────────────────

    [Fact]
    public void SetFillColorCmyk_emitsKOperator()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.SetFillColorCmyk(0.1, 0.2, 0.3, 0.4));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("0.1 0.2 0.3 0.4 k", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetStrokeColorCmyk_emitsKUpperOperator()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.SetStrokeColorCmyk(0, 0, 0, 1));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("0 0 0 1 K", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetFillColorCmyk_fullBlack_roundTrips()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.SetFillColorCmyk(0, 0, 0, 1).Rectangle(10, 10, 50, 50).Fill());
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains(" k\n", ops, StringComparison.Ordinal);
    }

    // ── Feature 5: Axial (Type 2) shading ──────────────────────────────────

    [Fact]
    public void PaintAxialGradient_registersShading()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.PaintAxialGradient(0, 0, 100, 0,
                new KernelColor(1, 0, 0), new KernelColor(0, 1, 0)));
        var text = Latin1(pdfBytes);

        Assert.Contains("/Shading", text);
        Assert.Contains("/ShadingType 2", text);
    }

    [Fact]
    public void PaintAxialGradient_containsDeviceRgb()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.PaintAxialGradient(0, 0, 100, 0,
                new KernelColor(0, 0, 1), new KernelColor(1, 1, 0)));
        var text = Latin1(pdfBytes);

        Assert.Contains("/DeviceRGB", text);
    }

    [Fact]
    public void PaintAxialGradient_containsExtendTrue()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.PaintAxialGradient(0, 0, 100, 0,
                KernelColor.Black, KernelColor.White));
        var text = Latin1(pdfBytes);

        Assert.Contains("true", text);
    }

    [Fact]
    public void PaintAxialGradient_emitsShOperator()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.PaintAxialGradient(0, 0, 200, 0,
                KernelColor.Black, KernelColor.White));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("sh", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void PaintAxialGradient_sameParams_deduplicatesShading()
    {
        var pdfBytes = BuildPdf(canvas =>
        {
            canvas.PaintAxialGradient(0, 0, 100, 0,
                KernelColor.Black, KernelColor.White);
            canvas.PaintAxialGradient(0, 0, 100, 0,
                KernelColor.Black, KernelColor.White);
        });
        var text = Latin1(pdfBytes);

        // First shading should be Sh1; Sh2 must NOT appear.
        Assert.Contains("/Sh1", text);
        Assert.DoesNotContain("/Sh2", text);
    }

    // ── Feature 5: Radial (Type 3) shading ──────────────────────────────────

    [Fact]
    public void PaintRadialGradient_registersShading()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.PaintRadialGradient(100, 100, 0, 100, 100, 80,
                new KernelColor(1, 0, 0), new KernelColor(0, 0, 0)));
        var text = Latin1(pdfBytes);

        Assert.Contains("/Shading", text);
        Assert.Contains("/ShadingType 3", text);
    }

    [Fact]
    public void PaintRadialGradient_containsDeviceRgb()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.PaintRadialGradient(50, 50, 0, 50, 50, 50,
                KernelColor.White, KernelColor.Black));
        var text = Latin1(pdfBytes);

        Assert.Contains("/DeviceRGB", text);
    }

    [Fact]
    public void PaintRadialGradient_emitsShOperator()
    {
        var pdfBytes = BuildPdf(canvas =>
            canvas.PaintRadialGradient(100, 100, 0, 100, 100, 80,
                KernelColor.Black, KernelColor.White));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("sh", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void PaintAxialAndRadial_differentShadings_twoResources()
    {
        var pdfBytes = BuildPdf(canvas =>
        {
            canvas.PaintAxialGradient(0, 0, 100, 0,
                KernelColor.Black, KernelColor.White);
            canvas.PaintRadialGradient(50, 50, 0, 50, 50, 50,
                KernelColor.Black, KernelColor.White);
        });
        var text = Latin1(pdfBytes);

        Assert.Contains("/Sh1", text);
        Assert.Contains("/Sh2", text);
        Assert.Contains("/ShadingType 2", text);
        Assert.Contains("/ShadingType 3", text);
    }

    // ── Combined: full page exercise ─────────────────────────────────────────

    [Fact]
    public void FullPageExercise_allFeaturesPresent()
    {
        // Build a single page that exercises every new feature.
        var pdfBytes = BuildPdf(canvas =>
        {
            // Fill alpha
            canvas.SetFillAlpha(0.8);

            // Stroke alpha
            canvas.SetStrokeAlpha(0.5);

            // Dash pattern
            canvas.SetLineDash([4, 2], 1);

            // CMYK fill
            canvas.SetFillColorCmyk(0, 0.5, 1, 0);

            // CMYK stroke
            canvas.SetStrokeColorCmyk(0, 0, 0, 0.8);

            // Clip + axial gradient
            canvas.SaveState();
            canvas.Rectangle(10, 10, 200, 200).Clip().EndPath();
            canvas.PaintAxialGradient(10, 110, 210, 110,
                new KernelColor(1, 0, 0), new KernelColor(0, 0, 1));
            canvas.RestoreState();

            // Radial gradient
            canvas.SaveState();
            canvas.Rectangle(50, 50, 100, 100).Clip().EndPath();
            canvas.PaintRadialGradient(100, 100, 0, 100, 100, 60,
                new KernelColor(1, 1, 0), new KernelColor(0, 1, 1));
            canvas.RestoreState();
        });

        var text = Latin1(pdfBytes);
        var ops = DecompressContentStream(pdfBytes);

        // Resource dict checks (uncompressed)
        Assert.Contains("/ExtGState", text);
        Assert.Contains("/ca", text);   // fill alpha
        Assert.Contains("/CA", text);   // stroke alpha
        Assert.Contains("/Shading", text);
        Assert.Contains("/ShadingType 2", text);  // axial
        Assert.Contains("/ShadingType 3", text);  // radial
        Assert.Contains("/DeviceRGB", text);

        // Content stream operator checks (decompressed)
        Assert.Contains("gs", ops, StringComparison.Ordinal);   // ExtGState apply
        Assert.Contains("d\n", ops, StringComparison.Ordinal);  // dash
        Assert.Contains(" k\n", ops, StringComparison.Ordinal); // cmyk fill
        Assert.Contains(" K\n", ops, StringComparison.Ordinal); // cmyk stroke
        Assert.Contains("W\n", ops, StringComparison.Ordinal);  // clip
        Assert.Contains("sh\n", ops, StringComparison.Ordinal); // shading paint
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal one-page PDF, runs <paramref name="draw"/> against the canvas,
    /// and returns the raw PDF bytes.
    /// </summary>
    private static byte[] BuildPdf(Action<PdfCanvas> draw)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        draw(canvas);
        canvas.Finish();
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Latin1(byte[] bytes) =>
        Encoding.Latin1.GetString(bytes);

    /// <summary>
    /// Finds the FlateDecode content stream in the PDF bytes, decompresses it,
    /// and returns the operator text.
    ///
    /// The PDF writer compresses every PdfStream with ZLib.  The content stream
    /// for the first page is the first stream object encountered after the header.
    /// We find it by looking for the "\nstream\n" marker after the FlateDecode
    /// filter declaration.
    /// </summary>
    private static string DecompressContentStream(byte[] pdfBytes)
    {
        // Find the first occurrence of "\nstream\n".
        var streamStart = FindSequence(pdfBytes, "\nstream\n"u8);
        Assert.True(streamStart >= 0, "No stream found in PDF");

        var dataStart = streamStart + 8; // length of "\nstream\n"

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
