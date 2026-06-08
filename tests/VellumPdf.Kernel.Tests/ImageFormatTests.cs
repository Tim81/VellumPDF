// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for BmpImageLoader, GifImageLoader, and the sub-8-bit PNG fix in PngImageLoader.
/// All image files are synthesised in-memory; no external resources are required.
/// </summary>
public sealed class ImageFormatTests
{
    // ── BMP: 24-bit RGB ───────────────────────────────────────────────────────

    [Fact]
    public void Bmp24Bit_dimensions_correct()
    {
        var bmp = CreateBmp24(3, 2, [0xFF, 0x00, 0x00]); // all-red image
        var img = BmpImageLoader.Load(bmp);
        Assert.Equal(3, img.Width);
        Assert.Equal(2, img.Height);
    }

    [Fact]
    public void Bmp24Bit_colorSpace_isDeviceRgb()
    {
        // We can't inspect ColorSpace directly (it's internal), but the stream
        // dict produced by BuildStream must contain /DeviceRGB.
        var bmp = CreateBmp24(2, 2, [0x00, 0xFF, 0x00]);
        var img = BmpImageLoader.Load(bmp);
        var stream = img.BuildStream();
        Assert.Contains("DeviceRGB", PdfStreamText(stream));
    }

    [Fact]
    public void Bmp24Bit_noSmask()
    {
        var bmp = CreateBmp24(2, 2, [0x00, 0x00, 0xFF]);
        var img = BmpImageLoader.Load(bmp);
        Assert.Null(img.SMask);
    }

    [Fact]
    public void Bmp24Bit_pixelRoundTrip()
    {
        // 2×1 BMP: left pixel pure red, right pixel pure green.
        // BMP stores BGR so red = 0x00,0x00,0xFF and green = 0x00,0xFF,0x00.
        var bmp = CreateBmp24Custom(2, 1, [
            // pixel row (2 pixels × 3 bytes = 6 bytes), then DWORD-pad to 8 bytes
            0x00, 0x00, 0xFF,  // blue=0, green=0, red=255 → red pixel
            0x00, 0xFF, 0x00,  // blue=0, green=255, red=0  → green pixel
            0x00, 0x00         // 2-byte padding for DWORD alignment
        ]);
        var img = BmpImageLoader.Load(bmp);

        // Decompress FlateDecode stream to recover pixel bytes
        var pixels = DecompressStream(img.BuildStream());

        // Pixel 0: R=255, G=0, B=0
        Assert.Equal(255, pixels[0]);
        Assert.Equal(0, pixels[1]);
        Assert.Equal(0, pixels[2]);
        // Pixel 1: R=0, G=255, B=0
        Assert.Equal(0, pixels[3]);
        Assert.Equal(255, pixels[4]);
        Assert.Equal(0, pixels[5]);
    }

    // ── BMP: 8-bit palette-indexed ────────────────────────────────────────────

    [Fact]
    public void Bmp8Bit_dimensions_correct()
    {
        var bmp = CreateBmp8(4, 3);
        var img = BmpImageLoader.Load(bmp);
        Assert.Equal(4, img.Width);
        Assert.Equal(3, img.Height);
    }

    [Fact]
    public void Bmp8Bit_colorSpace_isDeviceRgb()
    {
        var bmp = CreateBmp8(2, 2);
        var img = BmpImageLoader.Load(bmp);
        var stream = img.BuildStream();
        Assert.Contains("DeviceRGB", PdfStreamText(stream));
    }

    [Fact]
    public void Bmp8Bit_pixelRoundTrip()
    {
        // 1×1 image: palette entry 0 = pure red (RGBQUAD: B=0, G=0, R=255, reserved=0)
        // The single pixel index is 0.
        var bmp = CreateBmp8RoundTrip(1, 1,
            paletteEntry0Bgr: [0x00, 0x00, 0xFF], // RGBQUAD: blue=0, green=0, red=255
            index: 0);
        var img = BmpImageLoader.Load(bmp);
        var pixels = DecompressStream(img.BuildStream());
        Assert.Equal(255, pixels[0]); // R
        Assert.Equal(0, pixels[1]);   // G
        Assert.Equal(0, pixels[2]);   // B
    }

    // ── GIF: basic 4-colour GIF ───────────────────────────────────────────────

    [Fact]
    public void Gif_dimensions_correct()
    {
        var gif = CreateGif(3, 2, transparent: false);
        var img = GifImageLoader.Load(gif);
        Assert.Equal(3, img.Width);
        Assert.Equal(2, img.Height);
    }

    [Fact]
    public void Gif_colorSpace_isDeviceRgb()
    {
        var gif = CreateGif(2, 2, transparent: false);
        var img = GifImageLoader.Load(gif);
        var stream = img.BuildStream();
        Assert.Contains("DeviceRGB", PdfStreamText(stream));
    }

    [Fact]
    public void Gif_noSmask_whenNoTransparency()
    {
        var gif = CreateGif(2, 2, transparent: false);
        var img = GifImageLoader.Load(gif);
        Assert.Null(img.SMask);
    }

    [Fact]
    public void Gif_hasSmask_whenTransparentIndex()
    {
        var gif = CreateGif(2, 2, transparent: true);
        var img = GifImageLoader.Load(gif);
        Assert.NotNull(img.SMask);
    }

    [Fact]
    public void Gif_transparent_smaskHasZeroForTransparentPixels()
    {
        // 1×1 GIF, all pixels set to transparent index 0.
        var gif = CreateGifAllTransparent(1, 1);
        var img = GifImageLoader.Load(gif);
        Assert.NotNull(img.SMask);
        var alphaMask = DecompressStream(img.SMask!);
        Assert.Equal(0, alphaMask[0]); // fully transparent
    }

    // ── PNG: 4-bit indexed (16-colour) ──────────────────────────────────────

    [Fact]
    public void Png4BitIndexed_dimensions_correct()
    {
        var png = CreateIndexedPng4Bit(4, 4);
        var img = PngImageLoader.Load(png);
        Assert.Equal(4, img.Width);
        Assert.Equal(4, img.Height);
    }

    [Fact]
    public void Png4BitIndexed_colorSpace_isDeviceRgb()
    {
        var png = CreateIndexedPng4Bit(4, 4);
        var img = PngImageLoader.Load(png);
        var stream = img.BuildStream();
        Assert.Contains("DeviceRGB", PdfStreamText(stream));
    }

    [Fact]
    public void Png4BitIndexed_pixelRoundTrip()
    {
        // 2×1 PNG, 4-bit indexed.
        // Palette index 0 → red (255,0,0), index 1 → blue (0,0,255).
        // Row of 2 pixels packed into 1 byte: high nibble = 0 (red), low nibble = 1 (blue).
        var png = CreateIndexedPng4BitRoundTrip();
        var img = PngImageLoader.Load(png);
        var pixels = DecompressStream(img.BuildStream());
        // Pixel 0: red
        Assert.Equal(255, pixels[0]); // R
        Assert.Equal(0, pixels[1]);   // G
        Assert.Equal(0, pixels[2]);   // B
        // Pixel 1: blue
        Assert.Equal(0, pixels[3]);   // R
        Assert.Equal(0, pixels[4]);   // G
        Assert.Equal(255, pixels[5]); // B
    }

    // ── BMP: compressed/exotic variants are rejected ──────────────────────────

    [Fact]
    public void Bmp_compressed_throws()
    {
        var bmp = CreateBmpWithCompression(1); // BI_RLE8
        Assert.Throws<NotSupportedException>(() => BmpImageLoader.Load(bmp));
    }

    [Fact]
    public void Bmp_os2Header_throws()
    {
        var bmp = CreateBmpWithHeaderSize(12); // BITMAPCOREHEADER
        Assert.Throws<NotSupportedException>(() => BmpImageLoader.Load(bmp));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string PdfStreamText(VellumPdf.Core.PdfStream stream)
    {
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
        stream.WriteTo(writer);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    /// <summary>
    /// Decompresses a FlateDecode PdfStream and returns the raw pixel bytes.
    /// PdfStream.WriteTo emits: dict + "\nstream\n" + compressed + "\nendstream"
    /// </summary>
    private static byte[] DecompressStream(VellumPdf.Core.PdfStream pdfStream)
    {
        using var pdfMs = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(pdfMs);
        pdfStream.WriteTo(writer);

        var raw = pdfMs.ToArray();
        // Marker written by PdfStream.WriteTo is "\nstream\n"
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        var start = markerStart + 8; // skip "\nstream\n"
        var end = FindSequence(raw, "\nendstream"u8);
        var compressed = raw[start..end];

        using var zms = new MemoryStream(compressed);
        using var z = new ZLibStream(zms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        z.CopyTo(result);
        return result.ToArray();
    }

    private static int FindSequence(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    // ── BMP builders ─────────────────────────────────────────────────────────

    /// <summary>Creates a minimal valid 24-bit BMP with every pixel set to the given BGR value.</summary>
    private static byte[] CreateBmp24(int w, int h, byte[] bgr)
    {
        var rowStride = (w * 3 + 3) & ~3;
        var pixelDataSize = rowStride * h;
        var fileSize = 54 + pixelDataSize;

        using var ms = new MemoryStream();
        // File header
        ms.Write("BM"u8);
        WriteU32Le(ms, (uint)fileSize);
        WriteU32Le(ms, 0); // reserved
        WriteU32Le(ms, 54); // pixel data offset

        // BITMAPINFOHEADER
        WriteU32Le(ms, 40);  // header size
        WriteS32Le(ms, w);
        WriteS32Le(ms, h);   // positive = bottom-up
        WriteU16Le(ms, 1);   // colour planes
        WriteU16Le(ms, 24);  // bits per pixel
        WriteU32Le(ms, 0);   // BI_RGB
        WriteU32Le(ms, (uint)pixelDataSize);
        WriteU32Le(ms, 2835); // X pixels/meter
        WriteU32Le(ms, 2835); // Y pixels/meter
        WriteU32Le(ms, 0);   // colours used
        WriteU32Le(ms, 0);   // colours important

        // Pixel data — bottom-up, each row DWORD-padded
        var row = new byte[rowStride];
        for (var x = 0; x < w; x++)
        {
            row[x * 3] = bgr[0];
            row[x * 3 + 1] = bgr[1];
            row[x * 3 + 2] = bgr[2];
        }
        for (var y = 0; y < h; y++)
            ms.Write(row);

        return ms.ToArray();
    }

    /// <summary>Creates a 24-bit BMP where the caller provides the exact pixel data bytes.</summary>
    private static byte[] CreateBmp24Custom(int w, int h, byte[] pixelData)
    {
        var rowStride = (w * 3 + 3) & ~3;
        var pixelDataSize = rowStride * h;
        var fileSize = 54 + pixelDataSize;

        using var ms = new MemoryStream();
        ms.Write("BM"u8);
        WriteU32Le(ms, (uint)fileSize);
        WriteU32Le(ms, 0);
        WriteU32Le(ms, 54);
        WriteU32Le(ms, 40);
        WriteS32Le(ms, w);
        WriteS32Le(ms, h);
        WriteU16Le(ms, 1);
        WriteU16Le(ms, 24);
        WriteU32Le(ms, 0);
        WriteU32Le(ms, (uint)pixelDataSize);
        WriteU32Le(ms, 2835);
        WriteU32Le(ms, 2835);
        WriteU32Le(ms, 0);
        WriteU32Le(ms, 0);
        ms.Write(pixelData, 0, pixelDataSize);
        return ms.ToArray();
    }

    /// <summary>Creates a minimal 8-bit indexed BMP with 256 greyscale palette entries.</summary>
    private static byte[] CreateBmp8(int w, int h)
    {
        var rowStride = (w + 3) & ~3;
        var pixelDataSize = rowStride * h;
        var paletteSize = 256 * 4;
        var fileSize = 54 + paletteSize + pixelDataSize;

        using var ms = new MemoryStream();
        ms.Write("BM"u8);
        WriteU32Le(ms, (uint)fileSize);
        WriteU32Le(ms, 0);
        WriteU32Le(ms, (uint)(54 + paletteSize));
        WriteU32Le(ms, 40);
        WriteS32Le(ms, w);
        WriteS32Le(ms, h);
        WriteU16Le(ms, 1);
        WriteU16Le(ms, 8);
        WriteU32Le(ms, 0); // BI_RGB
        WriteU32Le(ms, (uint)pixelDataSize);
        WriteU32Le(ms, 2835);
        WriteU32Le(ms, 2835);
        WriteU32Le(ms, 256);
        WriteU32Le(ms, 0);

        // Greyscale palette: entry i → RGBQUAD (i, i, i, 0)
        for (var i = 0; i < 256; i++)
        {
            ms.WriteByte((byte)i); // B
            ms.WriteByte((byte)i); // G
            ms.WriteByte((byte)i); // R
            ms.WriteByte(0);       // reserved
        }

        // Pixel rows (all zero index = black)
        var row = new byte[rowStride];
        for (var y = 0; y < h; y++)
            ms.Write(row);

        return ms.ToArray();
    }

    /// <summary>Creates an 8-bit indexed BMP with a specific palette entry and all pixels at `index`.</summary>
    private static byte[] CreateBmp8RoundTrip(int w, int h, byte[] paletteEntry0Bgr, byte index)
    {
        var rowStride = (w + 3) & ~3;
        var pixelDataSize = rowStride * h;
        var paletteSize = 256 * 4;
        var fileSize = 54 + paletteSize + pixelDataSize;

        using var ms = new MemoryStream();
        ms.Write("BM"u8);
        WriteU32Le(ms, (uint)fileSize);
        WriteU32Le(ms, 0);
        WriteU32Le(ms, (uint)(54 + paletteSize));
        WriteU32Le(ms, 40);
        WriteS32Le(ms, w);
        WriteS32Le(ms, h);
        WriteU16Le(ms, 1);
        WriteU16Le(ms, 8);
        WriteU32Le(ms, 0);
        WriteU32Le(ms, (uint)pixelDataSize);
        WriteU32Le(ms, 2835);
        WriteU32Le(ms, 2835);
        WriteU32Le(ms, 256);
        WriteU32Le(ms, 0);

        // Entry 0: supplied colour. All others: black.
        ms.WriteByte(paletteEntry0Bgr[0]);
        ms.WriteByte(paletteEntry0Bgr[1]);
        ms.WriteByte(paletteEntry0Bgr[2]);
        ms.WriteByte(0);
        for (var i = 1; i < 256; i++)
        {
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        }

        // All pixels at `index`
        var row = new byte[rowStride];
        for (var x = 0; x < w; x++) row[x] = index;
        for (var y = 0; y < h; y++) ms.Write(row);

        return ms.ToArray();
    }

    private static byte[] CreateBmpWithCompression(uint compression)
    {
        using var ms = new MemoryStream();
        ms.Write("BM"u8);
        WriteU32Le(ms, 54);
        WriteU32Le(ms, 0);
        WriteU32Le(ms, 54);
        WriteU32Le(ms, 40);
        WriteS32Le(ms, 1);
        WriteS32Le(ms, 1);
        WriteU16Le(ms, 1);
        WriteU16Le(ms, 24);
        WriteU32Le(ms, compression);
        WriteU32Le(ms, 4);
        WriteU32Le(ms, 0); WriteU32Le(ms, 0); WriteU32Le(ms, 0); WriteU32Le(ms, 0);
        ms.Write([0, 0, 0, 0]); // pixel data
        return ms.ToArray();
    }

    private static byte[] CreateBmpWithHeaderSize(uint headerSize)
    {
        // Build at least 54 bytes so the file-size guard passes; the header-size
        // guard fires next and must throw NotSupportedException.
        var totalSize = Math.Max(54u, 14u + headerSize);
        using var ms = new MemoryStream();
        ms.Write("BM"u8);
        WriteU32Le(ms, totalSize);
        WriteU32Le(ms, 0);
        WriteU32Le(ms, 14 + headerSize);
        WriteU32Le(ms, headerSize); // non-40 header size
        // Pad to at least 54 bytes
        while (ms.Length < totalSize) ms.WriteByte(0);
        return ms.ToArray();
    }

    private static void WriteU32Le(Stream s, uint v)
    {
        s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24));
    }

    private static void WriteS32Le(Stream s, int v) => WriteU32Le(s, (uint)v);

    private static void WriteU16Le(Stream s, ushort v)
    {
        s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8));
    }

    // ── GIF builders ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal GIF89a with a 4-colour global palette and a solid-colour first frame.
    /// When <paramref name="transparent"/> is true, the Graphic Control Extension marks index 3
    /// as transparent and all pixels use index 3.
    /// </summary>
    private static byte[] CreateGif(int w, int h, bool transparent)
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);

        // Logical screen descriptor: width, height, packed, bg index, pixel AR
        WriteU16Le(ms, (ushort)w);
        WriteU16Le(ms, (ushort)h);
        ms.WriteByte(0xA1); // global colour table flag set, size=2^2=4 colours
        ms.WriteByte(0);    // background colour index
        ms.WriteByte(0);    // pixel aspect ratio

        // Global colour table: 4 entries × 3 bytes = 12 bytes
        // 0=red, 1=green, 2=blue, 3=white
        ms.Write([0xFF, 0x00, 0x00]); // 0: red
        ms.Write([0x00, 0xFF, 0x00]); // 1: green
        ms.Write([0x00, 0x00, 0xFF]); // 2: blue
        ms.Write([0xFF, 0xFF, 0xFF]); // 3: white

        if (transparent)
        {
            // Graphic Control Extension: marks index 3 as transparent
            ms.WriteByte(0x21); // extension introducer
            ms.WriteByte(0xF9); // graphic control label
            ms.WriteByte(4);    // block size
            ms.WriteByte(0x01); // packed: transparent flag set
            ms.WriteByte(0); ms.WriteByte(0); // delay
            ms.WriteByte(3);    // transparent colour index = 3
            ms.WriteByte(0);    // block terminator
        }

        // Image descriptor
        ms.WriteByte(0x2C);
        WriteU16Le(ms, 0); WriteU16Le(ms, 0); // left, top
        WriteU16Le(ms, (ushort)w);
        WriteU16Le(ms, (ushort)h);
        ms.WriteByte(0); // packed (no local colour table)

        // LZW encode: all pixels = index 0 (non-transparent) or 3 (transparent)
        var pixelIndex = (byte)(transparent ? 3 : 0);
        var lzwData = LzwEncode(Enumerable.Repeat(pixelIndex, w * h).ToArray(), 2);
        ms.WriteByte(2); // LZW minimum code size

        // Write sub-blocks (max 255 bytes each)
        for (var i = 0; i < lzwData.Length; i += 255)
        {
            var len = Math.Min(255, lzwData.Length - i);
            ms.WriteByte((byte)len);
            ms.Write(lzwData, i, len);
        }
        ms.WriteByte(0); // sub-block terminator

        ms.WriteByte(0x3B); // GIF trailer
        return ms.ToArray();
    }

    /// <summary>Creates a 1×1 GIF where index 0 is marked transparent.</summary>
    private static byte[] CreateGifAllTransparent(int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        WriteU16Le(ms, (ushort)w);
        WriteU16Le(ms, (ushort)h);
        ms.WriteByte(0xA1); // 4-entry global colour table
        ms.WriteByte(0);
        ms.WriteByte(0);
        ms.Write([0x00, 0x00, 0x00]); // 0: black (will be transparent)
        ms.Write([0xFF, 0xFF, 0xFF]); // 1
        ms.Write([0xFF, 0x00, 0x00]); // 2
        ms.Write([0x00, 0xFF, 0x00]); // 3

        // GCE: transparent index = 0
        ms.WriteByte(0x21); ms.WriteByte(0xF9);
        ms.WriteByte(4);
        ms.WriteByte(0x01); // transparent flag
        ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte(0); // transparent index = 0
        ms.WriteByte(0);

        ms.WriteByte(0x2C);
        WriteU16Le(ms, 0); WriteU16Le(ms, 0);
        WriteU16Le(ms, (ushort)w);
        WriteU16Le(ms, (ushort)h);
        ms.WriteByte(0);

        var lzwData = LzwEncode(Enumerable.Repeat((byte)0, w * h).ToArray(), 2);
        ms.WriteByte(2);
        for (var i = 0; i < lzwData.Length; i += 255)
        {
            var len = Math.Min(255, lzwData.Length - i);
            ms.WriteByte((byte)len);
            ms.Write(lzwData, i, len);
        }
        ms.WriteByte(0);
        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    /// <summary>
    /// Minimal GIF LZW encoder (produces valid output compatible with GifImageLoader.LzwDecode).
    /// Uses fixed-width codes for simplicity: emits Clear, then one code per pixel, then EOI.
    /// This is valid for small palettes.
    /// </summary>
    private static byte[] LzwEncode(byte[] pixels, int minCodeSize)
    {
        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;
        int codeSize = minCodeSize + 1;

        using var ms = new MemoryStream();
        int bitBuf = 0;
        int bitsIn = 0;

        void EmitCode(int code)
        {
            bitBuf |= code << bitsIn;
            bitsIn += codeSize;
            while (bitsIn >= 8)
            {
                ms.WriteByte((byte)(bitBuf & 0xFF));
                bitBuf >>= 8;
                bitsIn -= 8;
            }
        }

        EmitCode(clearCode);
        foreach (var p in pixels)
            EmitCode(p);
        EmitCode(eoiCode);

        if (bitsIn > 0)
            ms.WriteByte((byte)(bitBuf & 0xFF));

        return ms.ToArray();
    }

    // ── PNG 4-bit indexed builders ────────────────────────────────────────────

    /// <summary>
    /// Creates a 4-bit indexed PNG with a 16-colour greyscale palette.
    /// All pixels are set to index 0 (black).
    /// </summary>
    private static byte[] CreateIndexedPng4Bit(int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR: bit depth 4, colour type 3 (indexed)
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, 4, 3));

        // PLTE: 16 greyscale entries
        var palette = new byte[16 * 3];
        for (var i = 0; i < 16; i++)
        {
            var v = (byte)(i * 17); // 0..255 evenly
            palette[i * 3] = v;
            palette[i * 3 + 1] = v;
            palette[i * 3 + 2] = v;
        }
        WritePngChunk(ms, "PLTE", palette);

        // IDAT: 4-bit packed scanlines — each row prefixed by filter byte 0
        var packedRowBytes = (w + 1) / 2;
        var rawData = new byte[h * (1 + packedRowBytes)];
        // filter bytes are already 0 (None); pixel data is all zeros → index 0

        WritePngChunk(ms, "IDAT", ZlibCompress(rawData));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a 2×1 4-bit indexed PNG where pixel 0 = palette index 0 (red) and
    /// pixel 1 = palette index 1 (blue), for round-trip colour verification.
    /// </summary>
    private static byte[] CreateIndexedPng4BitRoundTrip()
    {
        const int w = 2, h = 1;
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, 4, 3));

        // 2-entry palette (minimum 2 for 4-bit, but 4-bit requires full 16 — use 16, only care about 0 and 1)
        var palette = new byte[16 * 3];
        palette[0] = 255; palette[1] = 0; palette[2] = 0; // index 0 = red
        palette[3] = 0; palette[4] = 0; palette[5] = 255; // index 1 = blue
        WritePngChunk(ms, "PLTE", palette);

        // 1 row: filter byte = 0, then 1 packed byte = 0x01 (high nibble=0=red, low nibble=1=blue)
        var rawData = new byte[] { 0, 0x01 };
        WritePngChunk(ms, "IDAT", ZlibCompress(rawData));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] CreatePngIhdr(int w, int h, byte bitDepth, byte colorType)
    {
        var buf = new byte[13];
        buf[0] = (byte)(w >> 24); buf[1] = (byte)(w >> 16); buf[2] = (byte)(w >> 8); buf[3] = (byte)w;
        buf[4] = (byte)(h >> 24); buf[5] = (byte)(h >> 16); buf[6] = (byte)(h >> 8); buf[7] = (byte)h;
        buf[8] = bitDepth; buf[9] = colorType;
        return buf;
    }

    private static void WritePngChunk(Stream s, string type, byte[] data)
    {
        s.WriteByte((byte)(data.Length >> 24)); s.WriteByte((byte)(data.Length >> 16));
        s.WriteByte((byte)(data.Length >> 8)); s.WriteByte((byte)data.Length);
        foreach (var c in type) s.WriteByte((byte)c);
        s.Write(data);
        var crcData = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) crcData[i] = (byte)type[i];
        data.CopyTo(crcData, 4);
        var crc = Crc32(crcData);
        s.WriteByte((byte)(crc >> 24)); s.WriteByte((byte)(crc >> 16));
        s.WriteByte((byte)(crc >> 8)); s.WriteByte((byte)crc);
    }

    private static uint Crc32(byte[] data)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true);
        z.Write(data);
        z.Flush();
        return ms.ToArray();
    }
}
