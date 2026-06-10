// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for ICC-based colour management (issue #42):
/// CmykIccProfile byte structure, IccProfiles public API,
/// ICCBased colour space registration and PDF round-trip,
/// cs/CS/scn/SCN canvas operators, and OutputIntent configuration.
/// </summary>
public sealed class ColorSpaceIccTests
{
    // ── 1. CmykIccProfile.Bytes structure ────────────────────────────────────

    [Fact]
    public void CmykIccProfile_Bytes_LargerThan128()
    {
        Assert.True(CmykIccProfile.Bytes.Length > 128);
    }

    [Fact]
    public void CmykIccProfile_Bytes_HasAcspSignature()
    {
        var b = CmykIccProfile.Bytes;
        Assert.Equal((byte)'a', b[36]);
        Assert.Equal((byte)'c', b[37]);
        Assert.Equal((byte)'s', b[38]);
        Assert.Equal((byte)'p', b[39]);
    }

    [Fact]
    public void CmykIccProfile_Bytes_HasPrtrProfileClass()
    {
        var b = CmykIccProfile.Bytes;
        Assert.Equal((byte)'p', b[12]);
        Assert.Equal((byte)'r', b[13]);
        Assert.Equal((byte)'t', b[14]);
        Assert.Equal((byte)'r', b[15]);
    }

    [Fact]
    public void CmykIccProfile_Bytes_HasCmykColorSpace()
    {
        var b = CmykIccProfile.Bytes;
        Assert.Equal((byte)'C', b[16]);
        Assert.Equal((byte)'M', b[17]);
        Assert.Equal((byte)'Y', b[18]);
        Assert.Equal((byte)'K', b[19]);
    }

    [Fact]
    public void CmykIccProfile_Bytes_HasVersion2_1()
    {
        var b = CmykIccProfile.Bytes;
        Assert.Equal(0x02, b[8]);
        Assert.Equal(0x10, b[9]);
        Assert.Equal(0x00, b[10]);
        Assert.Equal(0x00, b[11]);
    }

    [Fact]
    public void CmykIccProfile_Bytes_SizeHeaderMatchesLength()
    {
        var b = CmykIccProfile.Bytes;
        var declaredSize = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        Assert.Equal((uint)b.Length, declaredSize);
    }

    // ── 2. IccProfiles public API ─────────────────────────────────────────────

    [Fact]
    public void IccProfiles_GenericCmyk_MatchesCmykIccProfileBytes()
    {
        Assert.Equal(CmykIccProfile.Bytes, IccProfiles.GenericCmyk);
    }

    [Fact]
    public void IccProfiles_GenericCmyk_ReturnsDistinctInstanceEachCall()
    {
        var first = IccProfiles.GenericCmyk;
        var second = IccProfiles.GenericCmyk;
        Assert.NotSame(first, second);
    }

    [Fact]
    public void IccProfiles_Srgb_NonEmpty()
    {
        Assert.True(IccProfiles.Srgb.Length > 128);
    }

    // ── 3. ICCBased registration end-to-end ───────────────────────────────────

    [Fact]
    public void RegisterIccBasedColorSpace_PdfContainsExpectedStructures()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterIccBasedColorSpace(page, IccProfiles.GenericCmyk, 4, "CS0");
        var canvas = new PdfCanvas(page);
        canvas
            .SetFillColorSpace("CS0")
            .SetFillColor(0.1, 0.2, 0.3, 0.4)
            .Rectangle(72, 72, 200, 200)
            .Fill();
        canvas.Finish();
        var ms = new MemoryStream();
        doc.Save(ms);
        var pdfBytes = ms.ToArray();
        var text = Latin1(pdfBytes);

        Assert.Contains("/ColorSpace", text);
        Assert.Contains("/ICCBased", text);
        Assert.Contains("/N 4", text);
        Assert.Contains("/DeviceCMYK", text);
    }

    [Fact]
    public void RegisterIccBasedColorSpace_ContentStreamHasCorrectOperators()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterIccBasedColorSpace(page, IccProfiles.GenericCmyk, 4, "CS0");
        var canvas = new PdfCanvas(page);
        canvas
            .SetFillColorSpace("CS0")
            .SetFillColor(0.1, 0.2, 0.3, 0.4)
            .Rectangle(72, 72, 200, 200)
            .Fill();
        canvas.Finish();
        var ms = new MemoryStream();
        doc.Save(ms);
        var pdfBytes = ms.ToArray();
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("/CS0 cs", ops, StringComparison.Ordinal);
        Assert.Contains("0.1 0.2 0.3 0.4 scn", ops, StringComparison.Ordinal);
    }

    // ── 4. cs/CS/scn/SCN operators ────────────────────────────────────────────

    [Fact]
    public void SetFillColorSpace_EmitsCsOperator()
    {
        var pdfBytes = BuildPdf(canvas => canvas.SetFillColorSpace("MyCS"));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("/MyCS cs", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetStrokeColorSpace_EmitsCSOperator()
    {
        var pdfBytes = BuildPdf(canvas => canvas.SetStrokeColorSpace("MyCS"));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("/MyCS CS", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetFillColor_EmitsScnOperator()
    {
        var pdfBytes = BuildPdf(canvas => canvas.SetFillColor(0.5, 0.25));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("0.5 0.25 scn", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void SetStrokeColor_EmitsSCNOperator()
    {
        var pdfBytes = BuildPdf(canvas => canvas.SetStrokeColor(0.1, 0.2, 0.3, 0.4));
        var ops = DecompressContentStream(pdfBytes);

        Assert.Contains("0.1 0.2 0.3 0.4 SCN", ops, StringComparison.Ordinal);
    }

    // ── 5. OutputIntent configuration ─────────────────────────────────────────

    [Fact]
    public void UseCmykOutputIntent_PdfContainsCmykN4()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "CMYK OutputIntent Test";
        doc.UseCmykOutputIntent("FOGRA-test");
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.SetFillColorCmyk(0, 0.5, 1, 0).Rectangle(72, 72, 200, 200).Fill();
        canvas.Finish();
        var ms = new MemoryStream();
        doc.Save(ms);
        var text = Latin1(ms.ToArray());

        Assert.Contains("/N 4", text);
        Assert.Contains("/GTS_PDFA1", text);
        Assert.Contains("FOGRA-test", text);
    }

    [Fact]
    public void DefaultOutputIntent_PdfA2b_ContainsSrgbN3()
    {
        using var doc = new PdfDocument();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "Default sRGB OutputIntent Test";
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.Rectangle(72, 72, 100, 100).Fill();
        canvas.Finish();
        var ms = new MemoryStream();
        doc.Save(ms);
        var text = Latin1(ms.ToArray());

        Assert.Contains("/N 3", text);
        Assert.Contains("sRGB IEC61966-2.1", text);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
