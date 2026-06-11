// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Images;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Conformance-oracle tests for v1.3 image codecs: CCITT Group-4, TIFF-LZW,
/// interlaced PNG, and 16-bit PNG.
///
/// Test categories:
///   (a) Structural assertions — run locally without any external tools. Each
///       generated PDF is inspected as raw bytes to verify that the correct
///       filter, decode parameters, and PDF/A OutputIntent are present.
///   (b) veraPDF PDF/A-2b conformance gates — gate on CI via GateOnCi("verapdf")
///       so they skip locally but fail on CI when veraPDF is absent or reports
///       non-compliance. Uses the same skip/fail pattern as PdfValidatorOracleTests.
///   (c) qpdf structural checks — same gate pattern as (b).
///
/// Layout.Tests does not reference Kernel.Tests, so the BuildAllWhiteG4 helper
/// is replicated here following the identical algorithm documented in
/// CcittImageTests.BuildAllWhiteG4.
/// </summary>
public sealed class ImageCodecOracleTests : IDisposable
{
    private readonly string _tempDir;

    public ImageCodecOracleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumoracle_img_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    // ── Structural tests (locally runnable, no external tools) ───────────────

    [Fact]
    public void CcittG4_Structure_HasCcittFaxDecodeAndOutputIntent()
    {
        var bytes = GeneratePdfACcittImageBytes();

        // PDF header present and document is non-trivial
        var latin1 = Encoding.Latin1.GetString(bytes);
        Assert.True(bytes.Length > 1024, $"PDF output is suspiciously small ({bytes.Length} bytes).");
        Assert.True(latin1.StartsWith("%PDF-", StringComparison.Ordinal), "Output does not start with %PDF-.");

        // Image XObject carries the CCITT filter
        Assert.Contains("/CCITTFaxDecode", latin1, StringComparison.Ordinal);
        Assert.Contains("/DecodeParms", latin1, StringComparison.Ordinal);
        Assert.Contains("/Columns", latin1, StringComparison.Ordinal);
        Assert.Contains("/Rows", latin1, StringComparison.Ordinal);
        Assert.Contains("/BitsPerComponent 1", latin1, StringComparison.Ordinal);

        // PDF/A OutputIntent required
        Assert.Contains("/OutputIntent", latin1, StringComparison.Ordinal);
    }

    [Fact]
    public void TiffLzw_Structure_HasFlateDecodeAndOutputIntent()
    {
        var bytes = GeneratePdfATiffLzwImageBytes();

        var latin1 = Encoding.Latin1.GetString(bytes);
        Assert.True(bytes.Length > 1024, $"PDF output is suspiciously small ({bytes.Length} bytes).");
        Assert.True(latin1.StartsWith("%PDF-", StringComparison.Ordinal), "Output does not start with %PDF-.");

        // LZW TIFF decodes to FlateDecode in the PDF (the loader decompresses and re-encodes)
        Assert.Contains("/FlateDecode", latin1, StringComparison.Ordinal);
        Assert.Contains("/OutputIntent", latin1, StringComparison.Ordinal);
    }

    [Fact]
    public void InterlacedPng_Structure_HasFlateDecodeAndOutputIntent()
    {
        var bytes = GeneratePdfAInterlacedPngImageBytes();

        var latin1 = Encoding.Latin1.GetString(bytes);
        Assert.True(bytes.Length > 1024, $"PDF output is suspiciously small ({bytes.Length} bytes).");
        Assert.True(latin1.StartsWith("%PDF-", StringComparison.Ordinal), "Output does not start with %PDF-.");

        Assert.Contains("/FlateDecode", latin1, StringComparison.Ordinal);
        Assert.Contains("/OutputIntent", latin1, StringComparison.Ordinal);
    }

    [Fact]
    public void Png16Bit_Structure_HasFlateDecodeAndOutputIntentAnd16BpcOrFlate()
    {
        var bytes = GeneratePdfA16BitPngImageBytes();

        var latin1 = Encoding.Latin1.GetString(bytes);
        Assert.True(bytes.Length > 1024, $"PDF output is suspiciously small ({bytes.Length} bytes).");
        Assert.True(latin1.StartsWith("%PDF-", StringComparison.Ordinal), "Output does not start with %PDF-.");

        Assert.Contains("/FlateDecode", latin1, StringComparison.Ordinal);
        Assert.Contains("/OutputIntent", latin1, StringComparison.Ordinal);
        // 16-bit PNG with ImageBitDepth.Preserve should produce BitsPerComponent 16
        Assert.Contains("/BitsPerComponent 16", latin1, StringComparison.Ordinal);
    }

    // ── veraPDF PDF/A-2b conformance gates (CI-gated) ────────────────────────

    [Fact]
    public void PdfA2b_CcittImage_veraPdf_reportsCompliant()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A CCITT oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_ccitt_verapdf.pdf");
        GeneratePdfACcittImageDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    [Fact]
    public void PdfA2b_TiffLzwImage_veraPdf_reportsCompliant()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A TIFF-LZW oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_tifflzw_verapdf.pdf");
        GeneratePdfATiffLzwImageDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    [Fact]
    public void PdfA2b_InterlacedPngImage_veraPdf_reportsCompliant()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A interlaced PNG oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_interlacedpng_verapdf.pdf");
        GeneratePdfAInterlacedPngImageDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    [Fact]
    public void PdfA2b_Png16BitImage_veraPdf_reportsCompliant()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A 16-bit PNG oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_png16bit_verapdf.pdf");
        GeneratePdfA16BitPngImageDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    // ── qpdf structural checks (CI-gated) ────────────────────────────────────

    [Fact]
    public void CcittImage_QpdfCheck_Passes()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for CCITT qpdf oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "ccitt_qpdf.pdf");
        GeneratePdfACcittImageDoc(pdfPath, fontPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on CCITT G4 image doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void TiffLzwImage_QpdfCheck_Passes()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for TIFF-LZW qpdf oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "tifflzw_qpdf.pdf");
        GeneratePdfATiffLzwImageDoc(pdfPath, fontPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on TIFF-LZW image doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void InterlacedPngImage_QpdfCheck_Passes()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for interlaced PNG qpdf oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "interlacedpng_qpdf.pdf");
        GeneratePdfAInterlacedPngImageDoc(pdfPath, fontPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on interlaced PNG image doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void Png16BitImage_QpdfCheck_Passes()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for 16-bit PNG qpdf oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "png16bit_qpdf.pdf");
        GeneratePdfA16BitPngImageDoc(pdfPath, fontPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on 16-bit PNG image doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    // ── Document generators ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a PDF/A-2b document embedding a CCITT Group-4 (T.6 all-white) image.
    /// Uses the same OutputIntent / ICC / page scaffolding as the existing PNG oracle.
    /// </summary>
    private static void GeneratePdfACcittImageDoc(string path, string fontPath)
    {
        File.WriteAllBytes(path, GeneratePdfACcittImageBytesWithFont(fontPath));
    }

    private static byte[] GeneratePdfACcittImageBytes()
    {
        // Structural test variant: no font needed (no text), just an image.
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf CCITT G4 Oracle";
        doc.Info.Producer = "VellumPdf";

        const int w = 64;
        const int h = 48;
        var g4Data = BuildAllWhiteG4(w, h);
        var imgXObj = CcittImageLoader.Load(g4Data, w, h);
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "White G4 test image" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] GeneratePdfACcittImageBytesWithFont(string fontPath)
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf CCITT G4 Oracle";
        doc.Info.Producer = "VellumPdf";

        var handle = doc.LoadTrueTypeFont(fontPath);
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph("PDF/A-2b CCITT Group-4 image document.", style));

        const int w = 64;
        const int h = 48;
        var g4Data = BuildAllWhiteG4(w, h);
        var imgXObj = CcittImageLoader.Load(g4Data, w, h);
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "White G4 test image" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static void GeneratePdfATiffLzwImageDoc(string path, string fontPath)
    {
        File.WriteAllBytes(path, GeneratePdfATiffLzwImageBytesWithFont(fontPath));
    }

    private static byte[] GeneratePdfATiffLzwImageBytes()
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf TIFF-LZW Oracle";
        doc.Info.Producer = "VellumPdf";

        var imgXObj = TiffImageLoader.Load(CreateMinimalLzwRgbTiff());
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "LZW TIFF test image" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] GeneratePdfATiffLzwImageBytesWithFont(string fontPath)
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf TIFF-LZW Oracle";
        doc.Info.Producer = "VellumPdf";

        var handle = doc.LoadTrueTypeFont(fontPath);
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph("PDF/A-2b TIFF-LZW image document.", style));

        var imgXObj = TiffImageLoader.Load(CreateMinimalLzwRgbTiff());
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "LZW TIFF test image" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static void GeneratePdfAInterlacedPngImageDoc(string path, string fontPath)
    {
        File.WriteAllBytes(path, GeneratePdfAInterlacedPngImageBytesWithFont(fontPath));
    }

    private static byte[] GeneratePdfAInterlacedPngImageBytes()
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf Interlaced PNG Oracle";
        doc.Info.Producer = "VellumPdf";

        var imgXObj = PngImageLoader.Load(CreateMinimalInterlacedRgbPng());
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "Interlaced PNG test image" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] GeneratePdfAInterlacedPngImageBytesWithFont(string fontPath)
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf Interlaced PNG Oracle";
        doc.Info.Producer = "VellumPdf";

        var handle = doc.LoadTrueTypeFont(fontPath);
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph("PDF/A-2b interlaced PNG image document.", style));

        var imgXObj = PngImageLoader.Load(CreateMinimalInterlacedRgbPng());
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "Interlaced PNG test image" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static void GeneratePdfA16BitPngImageDoc(string path, string fontPath)
    {
        File.WriteAllBytes(path, GeneratePdfA16BitPngImageBytesWithFont(fontPath));
    }

    private static byte[] GeneratePdfA16BitPngImageBytes()
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf 16-bit PNG Oracle";
        doc.Info.Producer = "VellumPdf";

        var imgXObj = PngImageLoader.Load(
            CreateMinimal16BitGreyscalePng(),
            new ImageLoadOptions { BitDepth = ImageBitDepth.Preserve });
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "16-bit PNG test image" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] GeneratePdfA16BitPngImageBytesWithFont(string fontPath)
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf 16-bit PNG Oracle";
        doc.Info.Producer = "VellumPdf";

        var handle = doc.LoadTrueTypeFont(fontPath);
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph("PDF/A-2b 16-bit PNG image document.", style));

        var imgXObj = PngImageLoader.Load(
            CreateMinimal16BitGreyscalePng(),
            new ImageLoadOptions { BitDepth = ImageBitDepth.Preserve });
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "16-bit PNG test image" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── Image fixture builders ────────────────────────────────────────────────

    /// <summary>
    /// Builds a valid T.6 (CCITT Group 4 / MMR) stream encoding an all-white image.
    ///
    /// Replicated from CcittImageTests.BuildAllWhiteG4 (Kernel.Tests is not
    /// referenced from Layout.Tests). Algorithm:
    ///   Each all-white row against an imaginary all-white reference line encodes
    ///   as a single V(0) codeword (bit "1"). After all rows, EOFB = two 12-bit
    ///   "000000000001" words (24 bits total). Bits are packed MSB-first.
    /// </summary>
    private static byte[] BuildAllWhiteG4(int columns, int rows)
    {
        var totalBits = rows + 24;
        var totalBytes = (totalBits + 7) / 8;
        var buf = new byte[totalBytes];
        var bitPos = 0;

        // Emit rows × V(0) = bit "1"
        for (var r = 0; r < rows; r++)
        {
            var byteIdx = bitPos / 8;
            var bitIdx = 7 - (bitPos % 8);
            buf[byteIdx] |= (byte)(1 << bitIdx);
            bitPos++;
        }

        // Emit EOFB: two × 000000000001 (12 bits each = 24 bits total)
        for (var eofbWord = 0; eofbWord < 2; eofbWord++)
        {
            bitPos += 11; // 11 zero bits already zero in buf
            var byteIdx = bitPos / 8;
            var bitIdx = 7 - (bitPos % 8);
            buf[byteIdx] |= (byte)(1 << bitIdx);
            bitPos++;
        }

        return buf;
    }

    /// <summary>
    /// Builds a minimal LZW-compressed greyscale TIFF (8×8, Compression=5, photometric=1).
    /// The LZW data is produced by the same MSB-first TIFF-variant encoder algorithm
    /// used in TiffImageTests, replicated here (Kernel.Tests not referenced).
    /// </summary>
    private static byte[] CreateMinimalLzwRgbTiff()
    {
        const int w = 8;
        const int h = 8;

        // All-white greyscale pixels
        var rawPixels = new byte[w * h];
        Array.Fill(rawPixels, (byte)0xFF);

        var lzwData = TiffLzwEncode(rawPixels);

        // Build minimal single-strip LE TIFF: Compression=5, photometric=1 (BlackIsZero grey)
        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49); // II
        ms.WriteByte(0x2A); ms.WriteByte(0x00); // magic 42 LE

        var pixelOffset = 8u;
        var ifdOffset = pixelOffset + (uint)lzwData.Length;
        WriteTiffU32(ms, ifdOffset);
        ms.Write(lzwData);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, w),                        // ImageWidth
            (257, 4, 1, h),                        // ImageLength
            (258, 3, 1, 8),                        // BitsPerSample
            (259, 3, 1, 5),                        // Compression = LZW
            (262, 3, 1, 1),                        // PhotometricInterpretation = BlackIsZero
            (273, 4, 1, pixelOffset),              // StripOffsets
            (277, 3, 1, 1),                        // SamplesPerPixel
            (278, 4, 1, h),                        // RowsPerStrip
            (279, 4, 1, (uint)lzwData.Length),     // StripByteCounts
            (284, 3, 1, 1),                        // PlanarConfiguration
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteTiffU16(ms, (ushort)entries.Count);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteTiffU16(ms, tag);
            WriteTiffU16(ms, type);
            WriteTiffU32(ms, count);
            if (type == 3) // SHORT: 2 bytes + 2 padding
            {
                ms.WriteByte((byte)value);
                ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0);
                ms.WriteByte(0);
            }
            else // LONG: 4 bytes
            {
                WriteTiffU32(ms, value);
            }
        }
        WriteTiffU32(ms, 0); // next IFD = 0

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal Adam7-interlaced RGB PNG (8×8, colour type 2, 8-bit, interlace=1).
    ///
    /// Adam7 organises a W×H image into 7 passes. For an 8×8 image the sub-image
    /// sizes per pass are:
    ///   pass 0: 1×1 (cols starting at 0 step 8, rows starting at 0 step 8)
    ///   pass 1: 1×1 (cols starting at 4 step 8)
    ///   pass 2: 2×1 (cols starting at 0 step 4, rows starting at 4 step 8)
    ///   pass 3: 2×2 (cols starting at 2 step 4, rows starting at 0 step 4)
    ///   pass 4: 4×2 (cols starting at 0 step 2, rows starting at 2 step 4)
    ///   pass 5: 4×4 (cols starting at 1 step 2, rows starting at 0 step 2)
    ///   pass 6: 8×4 (cols starting at 0 step 1, rows starting at 1 step 2)
    ///
    /// For each pass: [filter=0][R G B … for rw pixels], repeated rh times.
    /// All pixels are set to white (255, 255, 255).
    /// The concatenated scanlines are zlib-compressed into a single IDAT chunk.
    /// </summary>
    private static byte[] CreateMinimalInterlacedRgbPng()
    {
        const int w = 8;
        const int h = 8;

        // Adam7 pass parameters: xStart, yStart, xStep, yStep
        int[] xStart = [0, 4, 0, 2, 0, 1, 0];
        int[] yStart = [0, 0, 4, 0, 2, 0, 1];
        int[] xStep = [8, 8, 4, 4, 2, 2, 1];
        int[] yStep = [8, 8, 8, 4, 4, 2, 2];

        using var rawMs = new MemoryStream();

        for (var pass = 0; pass < 7; pass++)
        {
            var rw = ReducedDim(w, xStart[pass], xStep[pass]);
            var rh = ReducedDim(h, yStart[pass], yStep[pass]);
            if (rw == 0 || rh == 0) continue;

            // Each row: filter byte (0) + rw × 3 white bytes
            for (var row = 0; row < rh; row++)
            {
                rawMs.WriteByte(0); // filter = None
                for (var col = 0; col < rw; col++)
                {
                    rawMs.WriteByte(255); // R
                    rawMs.WriteByte(255); // G
                    rawMs.WriteByte(255); // B
                }
            }
        }

        var rawScanlines = rawMs.ToArray();
        var compressed = ZlibCompress(rawScanlines);

        using var pngMs = new MemoryStream();
        pngMs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature

        // IHDR: 8×8, 8-bit, colour type 2 (RGB), interlace=1
        var ihdr = new byte[13];
        ihdr[3] = w;    // width = 8
        ihdr[7] = h;    // height = 8
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 2;    // colour type: RGB
        ihdr[12] = 1;   // interlace method: Adam7
        WritePngChunk(pngMs, "IHDR", ihdr);
        WritePngChunk(pngMs, "IDAT", compressed);
        WritePngChunk(pngMs, "IEND", []);

        return pngMs.ToArray();
    }

    private static int ReducedDim(int fullDim, int start, int step)
    {
        if (start >= fullDim) return 0;
        return (fullDim - start + step - 1) / step;
    }

    /// <summary>
    /// Builds a minimal 16-bit greyscale PNG (4×4, colour type 0, bit depth 16).
    /// Each scanline: [filter=0][high_byte low_byte … for w pixels].
    /// All samples are set to 0x8000 (mid-grey at 16 bit).
    /// </summary>
    private static byte[] CreateMinimal16BitGreyscalePng()
    {
        const int w = 4;
        const int h = 4;

        // Each row: filter byte (0) + w × 2 bytes per 16-bit grey sample
        using var rawMs = new MemoryStream();
        for (var row = 0; row < h; row++)
        {
            rawMs.WriteByte(0); // filter = None
            for (var col = 0; col < w; col++)
            {
                rawMs.WriteByte(0x80); // high byte of 0x8000
                rawMs.WriteByte(0x00); // low byte
            }
        }

        var rawScanlines = rawMs.ToArray();
        var compressed = ZlibCompress(rawScanlines);

        using var pngMs = new MemoryStream();
        pngMs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature

        // IHDR: 4×4, 16-bit, colour type 0 (greyscale)
        var ihdr = new byte[13];
        ihdr[3] = w;    // width = 4
        ihdr[7] = h;    // height = 4
        ihdr[8] = 16;   // bit depth
        ihdr[9] = 0;    // colour type: greyscale
        ihdr[12] = 0;   // interlace: none
        WritePngChunk(pngMs, "IHDR", ihdr);
        WritePngChunk(pngMs, "IDAT", compressed);
        WritePngChunk(pngMs, "IEND", []);

        return pngMs.ToArray();
    }

    // ── PNG chunk writing (replicated from PdfTestUtil) ───────────────────────

    private static void WritePngChunk(MemoryStream s, string type, byte[] data)
    {
        s.WriteByte((byte)(data.Length >> 24));
        s.WriteByte((byte)(data.Length >> 16));
        s.WriteByte((byte)(data.Length >> 8));
        s.WriteByte((byte)data.Length);
        foreach (var c in type) s.WriteByte((byte)c);
        s.Write(data);

        var crcInput = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) crcInput[i] = (byte)type[i];
        data.CopyTo(crcInput, 4);
        var crc = ComputePngCrc32(crcInput);
        s.WriteByte((byte)(crc >> 24));
        s.WriteByte((byte)((crc >> 16) & 0xFF));
        s.WriteByte((byte)((crc >> 8) & 0xFF));
        s.WriteByte((byte)(crc & 0xFF));
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(
            output, System.IO.Compression.CompressionLevel.Fastest))
            z.Write(data);
        return output.ToArray();
    }

    private static uint ComputePngCrc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    // ── TIFF write helpers ────────────────────────────────────────────────────

    private static void WriteTiffU16(Stream s, ushort v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
    }

    private static void WriteTiffU32(Stream s, uint v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 24));
    }

    /// <summary>
    /// TIFF-variant LZW encoder (MSB-first, early change at nextCode == 1&lt;&lt;codeWidth,
    /// ClearCode reset at table size 4096). Replicated from TiffImageTests.TiffLzwEncode.
    /// </summary>
    private static byte[] TiffLzwEncode(byte[] input)
    {
        const int clearCode = 256;
        const int eoiCode = 257;
        const int firstFreeCode = 258;
        const int maxTableSize = 4096;

        using var ms = new MemoryStream();
        int codeWidth = 9;
        int nextCode = firstFreeCode;
        var table = new Dictionary<long, int>();

        int bitBuf = 0;
        int bitsInBuf = 0;

        void EmitCode(int code)
        {
            bitBuf = (bitBuf << codeWidth) | (code & ((1 << codeWidth) - 1));
            bitsInBuf += codeWidth;
            while (bitsInBuf >= 8)
            {
                bitsInBuf -= 8;
                ms.WriteByte((byte)(bitBuf >> bitsInBuf));
                bitBuf &= (1 << bitsInBuf) - 1;
            }
        }

        void ResetTable()
        {
            table.Clear();
            codeWidth = 9;
            nextCode = firstFreeCode;
        }

        EmitCode(clearCode);

        if (input.Length == 0)
        {
            EmitCode(eoiCode);
            if (bitsInBuf > 0)
                ms.WriteByte((byte)(bitBuf << (8 - bitsInBuf)));
            return ms.ToArray();
        }

        int w = input[0];

        for (var i = 1; i < input.Length; i++)
        {
            int k = input[i];
            long key = ((long)w << 8) | (byte)k;

            if (table.TryGetValue(key, out int existing))
            {
                w = existing;
            }
            else
            {
                EmitCode(w);

                if (nextCode < maxTableSize)
                {
                    table[key] = nextCode;
                    nextCode++;
                    if (nextCode == (1 << codeWidth) && codeWidth < 12)
                        codeWidth++;
                }
                else
                {
                    EmitCode(clearCode);
                    ResetTable();
                }

                w = k;
            }
        }

        EmitCode(w);
        EmitCode(eoiCode);

        if (bitsInBuf > 0)
            ms.WriteByte((byte)(bitBuf << (8 - bitsInBuf)));

        return ms.ToArray();
    }

    // ── veraPDF / qpdf helpers (mirror PdfValidatorOracleTests) ─────────────

    private static void AssertVeraPdfCompliant(string pdfPath, string flavour)
    {
        if (!TryRunTool("verapdf", $"--flavour {flavour} \"{pdfPath}\"",
            out var exit, out var reportXml, out var stderr))
        {
            GateOnCi("verapdf");
            return;
        }

        var isCompliant = reportXml.Contains("isCompliant=\"true\"", StringComparison.Ordinal) ||
                          reportXml.Contains("compliant=\"true\"", StringComparison.Ordinal);

        Assert.True(
            isCompliant,
            $"veraPDF PDF/A-{flavour} validation failed (exit {exit}).\n" +
            $"veraPDF report:\n{reportXml}\n" +
            $"stderr:\n{stderr}\n" +
            "Common causes: unembedded fonts, missing OutputIntent, incorrect XMP schema, " +
            "unsupported image filter for PDF/A, or missing /BitsPerComponent.");
    }

    private static bool TryRunTool(
        string exe,
        string args,
        out int exitCode,
        out string stdout,
        out string stderr)
    {
        exitCode = -1;
        stdout = string.Empty;
        stderr = string.Empty;

        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        System.Diagnostics.Process? process = null;
        try
        {
            process = System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }

        if (process is null) return false;

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var completed = process.WaitForExit(milliseconds: 30_000);
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();

            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                exitCode = -1;
                return true;
            }

            exitCode = process.ExitCode;
        }

        return true;
    }

    private static void GateOnCi(string toolName)
    {
        var isCI = string.Equals(
            Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase);
        var isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
            StringComparison.OrdinalIgnoreCase);

        if (isCI || isGitHubActions)
        {
            Assert.Fail(
                $"Required external tool '{toolName}' is not available on CI. " +
                "Ensure the CI workflow installs it (e.g. sudo apt-get install -y qpdf poppler-utils).");
        }
        // Local dev: silently skip.
    }
}
