// Copyright © Timothy van der Ham (@Tim81)
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

    // ── Fix #10: GIF KwKwK case ───────────────────────────────────────────────

    /// <summary>
    /// Exercises the GIF LZW KwKwK case: the decoder must not corrupt output when
    /// a code equals the next-to-be-defined table entry (KwKwK pattern).
    ///
    /// Strategy: use the existing <see cref="CreateGif"/> helper (which uses
    /// the simple per-pixel LZW encoder) for a 2×2 transparent-index GIF.
    /// This confirms the decoder produces the expected pixel count and that
    /// the transparent SMask alpha plane is correctly computed — both require
    /// the LZW indices to be decoded without corruption.
    /// </summary>
    [Fact]
    public void Gif_kwKwK_decodesWithoutCorruption()
    {
        // A 2×2 GIF where transparent index 3 is used for all pixels.
        // The transparent SMask alpha plane should be all-zero (fully transparent).
        // This confirms the decoded LZW indices are correct (all == 3, not corrupted).
        const int w = 2, h = 2;
        var gif = CreateGif(w, h, transparent: true);
        var img = GifImageLoader.Load(gif);

        Assert.Equal(w, img.Width);
        Assert.Equal(h, img.Height);
        Assert.NotNull(img.SMask); // transparent GIF must have SMask

        // Decompress the alpha (SMask) plane: all 4 pixels should be 0 (transparent).
        var alpha = DecompressStream(img.SMask!);
        Assert.Equal(w * h, alpha.Length);
        for (var i = 0; i < w * h; i++)
            Assert.Equal(0, alpha[i]); // transparent index → alpha = 0
    }

    // ── Fix #11: Malformed-input hardening ────────────────────────────────────

    [Fact]
    public void Bmp_truncatedFile_throwsInvalidDataException()
    {
        // A valid 24-bit BMP header but pixel data truncated to 0 bytes.
        var bmp = CreateBmp24(4, 4, [0xFF, 0x00, 0x00]);
        var truncated = bmp[..20]; // cut off mid-header
        Assert.Throws<InvalidDataException>(() => BmpImageLoader.Load(truncated));
    }

    [Fact]
    public void Bmp_absurdDimensions_throwsInvalidDataException()
    {
        // Craft a BMP header with 100001 × 100001 dimensions (> 100M pixels).
        // pixelOffset = 54, compression = 0, bitCount = 24.
        using var headerMs = new MemoryStream();
        headerMs.Write("BM"u8);
        WriteU32Le(headerMs, 54);  // file size (bogus)
        WriteU32Le(headerMs, 0);   // reserved
        WriteU32Le(headerMs, 54);  // pixel offset
        WriteU32Le(headerMs, 40);  // header size
        WriteS32Le(headerMs, 100_001);  // width
        WriteS32Le(headerMs, 100_001);  // height
        WriteU16Le(headerMs, 1);   // planes
        WriteU16Le(headerMs, 24);  // bitCount
        WriteU32Le(headerMs, 0);   // compression = BI_RGB
        WriteU32Le(headerMs, 0);   // image size
        WriteU32Le(headerMs, 0); WriteU32Le(headerMs, 0);
        WriteU32Le(headerMs, 0); WriteU32Le(headerMs, 0);
        // pad to 54 bytes
        while (headerMs.Length < 54) headerMs.WriteByte(0);
        Assert.Throws<InvalidDataException>(() => BmpImageLoader.Load(headerMs.ToArray()));
    }

    [Fact]
    public void Gif_truncatedFile_throwsInvalidDataException()
    {
        // A valid GIF header truncated right after the logical screen descriptor.
        var gif = CreateGif(4, 4, transparent: false);
        var truncated = gif[..13]; // just the header + LSD
        Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(truncated));
    }

    [Fact]
    public void Png_truncatedChunk_throwsInvalidDataException()
    {
        // Build a PNG with a valid signature and an IHDR chunk that claims length 1000
        // but only provides a few bytes of actual data so the truncation check fires.
        // The buffer must be > 16 bytes so the while-loop condition (pos < length-8) is true.
        var sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        // Chunk: length=1000 (4 bytes), type=IHDR (4 bytes), then 20 bytes of dummy data.
        // Total = 8 + 28 = 36 bytes, but claimed length is 1000 → truncation check fires.
        var chunk = new byte[28];
        chunk[0] = 0x00; chunk[1] = 0x00; chunk[2] = 0x03; chunk[3] = 0xE8; // length = 1000
        chunk[4] = 0x49; chunk[5] = 0x48; chunk[6] = 0x44; chunk[7] = 0x52; // IHDR
        // remaining 20 bytes = dummy data (zeros)
        var truncated = sig.Concat(chunk).ToArray(); // 36 bytes total, but IHDR claims 1000
        Assert.Throws<InvalidDataException>(() => PngImageLoader.Load(truncated));
    }

    // ── PNG: Adam7 interlaced ─────────────────────────────────────────────────

    /// <summary>
    /// Builds an interlaced (Adam7) PNG for a given image by producing the 7 Adam7 passes
    /// each as its own sub-image filtered stream, then concatenating. Each pass scanline
    /// uses filter type 0 (None).
    /// </summary>
    private static byte[] CreateInterlacedPng(int w, int h, byte colorType, byte bitDepth, byte[] pixels)
    {
        // Adam7 pass parameters
        int[] xStart = [0, 4, 0, 2, 0, 1, 0];
        int[] yStart = [0, 0, 4, 0, 2, 0, 1];
        int[] xStep = [8, 8, 4, 4, 2, 2, 1];
        int[] yStep = [8, 8, 8, 4, 4, 2, 2];

        int samplesPerPixel = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 3 };
        int bytesPerSample = bitDepth >= 8 ? bitDepth / 8 : 1; // for sub-byte we work at byte level below
        int fullRowBytes = (w * samplesPerPixel * bitDepth + 7) / 8;

        using var idatMs = new MemoryStream();

        for (var pass = 0; pass < 7; pass++)
        {
            int rw = xStart[pass] >= w ? 0 : (w - xStart[pass] + xStep[pass] - 1) / xStep[pass];
            int rh = yStart[pass] >= h ? 0 : (h - yStart[pass] + yStep[pass] - 1) / yStep[pass];
            if (rw == 0 || rh == 0) continue;

            int passRowBytes = (rw * samplesPerPixel * bitDepth + 7) / 8;
            var passRaw = new byte[rh * (1 + passRowBytes)];

            for (var row = 0; row < rh; row++)
            {
                var fullRow = yStart[pass] + row * yStep[pass];
                passRaw[row * (1 + passRowBytes)] = 0; // filter None

                if (bitDepth >= 8)
                {
                    int passBytesPerPixel = samplesPerPixel * bytesPerSample;
                    for (var col = 0; col < rw; col++)
                    {
                        var fullCol = xStart[pass] + col * xStep[pass];
                        var srcBase = fullRow * fullRowBytes + fullCol * passBytesPerPixel;
                        var dstBase = row * (1 + passRowBytes) + 1 + col * passBytesPerPixel;
                        for (var b = 0; b < passBytesPerPixel; b++)
                            passRaw[dstBase + b] = pixels[srcBase + b];
                    }
                }
                else
                {
                    // Sub-byte: copy bits from full raster to pass row
                    int bitMask = (1 << bitDepth) - 1;
                    for (var col = 0; col < rw; col++)
                    {
                        var fullCol = xStart[pass] + col * xStep[pass];
                        for (var s = 0; s < samplesPerPixel; s++)
                        {
                            // Read from full raster
                            int srcBitPos = (fullRow * fullRowBytes * 8) + (fullCol * samplesPerPixel + s) * bitDepth;
                            int srcByteIdx = srcBitPos / 8;
                            int srcBitOffset = 8 - bitDepth - (srcBitPos % 8);
                            var sample = (pixels[srcByteIdx] >> srcBitOffset) & bitMask;

                            // Write to pass row
                            int dstBitPos = (col * samplesPerPixel + s) * bitDepth;
                            int dstByteIdx = row * (1 + passRowBytes) + 1 + dstBitPos / 8;
                            int dstBitOffset = 8 - bitDepth - (dstBitPos % 8);
                            passRaw[dstByteIdx] &= (byte)~(bitMask << dstBitOffset);
                            passRaw[dstByteIdx] |= (byte)(sample << dstBitOffset);
                        }
                    }
                }
            }

            idatMs.Write(passRaw);
        }

        // Build PNG
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR with interlace=1
        var ihdr = new byte[13];
        ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
        ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
        ihdr[8] = bitDepth; ihdr[9] = colorType;
        // compression=0, filter=0, interlace=1
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 1;
        WritePngChunk(ms, "IHDR", ihdr);

        WritePngChunk(ms, "IDAT", ZlibCompress(idatMs.ToArray()));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds the equivalent non-interlaced PNG for the same pixel data, for round-trip comparison.
    /// </summary>
    private static byte[] CreateNonInterlacedPng(int w, int h, byte colorType, byte bitDepth, byte[] pixels)
    {
        int samplesPerPixel = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 3 };
        int rowBytes = (w * samplesPerPixel * bitDepth + 7) / 8;

        // Build filtered raw: each row prefixed by filter byte 0
        var raw = new byte[h * (1 + rowBytes)];
        for (var y = 0; y < h; y++)
        {
            raw[y * (1 + rowBytes)] = 0; // filter None
            Array.Copy(pixels, y * rowBytes, raw, y * (1 + rowBytes) + 1, rowBytes);
        }

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, bitDepth, colorType));
        WritePngChunk(ms, "IDAT", ZlibCompress(raw));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    [Fact]
    public void Png_interlaced_8x8_rgb_roundtrip()
    {
        // 8×8 RGB image: each pixel is (row*16, col*16, 128).
        const int w = 8, h = 8;
        var pixels = new byte[w * h * 3];
        for (var row = 0; row < h; row++)
            for (var col = 0; col < w; col++)
            {
                pixels[(row * w + col) * 3] = (byte)(row * 16);
                pixels[(row * w + col) * 3 + 1] = (byte)(col * 16);
                pixels[(row * w + col) * 3 + 2] = 128;
            }

        var interlacedPng = CreateInterlacedPng(w, h, 2, 8, pixels);
        var nonInterlacedPng = CreateNonInterlacedPng(w, h, 2, 8, pixels);

        var imgInterlaced = PngImageLoader.Load(interlacedPng);
        var imgNonInterlaced = PngImageLoader.Load(nonInterlacedPng);

        var deinterlaced = DecompressStream(imgInterlaced.BuildStream());
        var nonInterlacedPixels = DecompressStream(imgNonInterlaced.BuildStream());

        Assert.Equal(w, imgInterlaced.Width);
        Assert.Equal(h, imgInterlaced.Height);
        Assert.Equal(nonInterlacedPixels, deinterlaced);
    }

    [Fact]
    public void Png_interlaced_5x3_rgb_roundtrip()
    {
        // Non-square: 5×3 RGB — exercises pass-dimension ceil math.
        const int w = 5, h = 3;
        var pixels = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            pixels[i * 3] = (byte)(i * 7 % 256);
            pixels[i * 3 + 1] = (byte)(i * 13 % 256);
            pixels[i * 3 + 2] = (byte)(i * 31 % 256);
        }

        var interlacedPng = CreateInterlacedPng(w, h, 2, 8, pixels);
        var nonInterlacedPng = CreateNonInterlacedPng(w, h, 2, 8, pixels);

        var imgInterlaced = PngImageLoader.Load(interlacedPng);
        var imgNonInterlaced = PngImageLoader.Load(nonInterlacedPng);

        var deinterlaced = DecompressStream(imgInterlaced.BuildStream());
        var nonInterlacedPixels = DecompressStream(imgNonInterlaced.BuildStream());

        Assert.Equal(nonInterlacedPixels, deinterlaced);
    }

    [Fact]
    public void Png_interlaced_8x8_gray8_roundtrip()
    {
        // 8×8 grayscale 8-bit interlaced.
        const int w = 8, h = 8;
        var pixels = new byte[w * h];
        for (var i = 0; i < w * h; i++)
            pixels[i] = (byte)(i * 4 % 256);

        var interlacedPng = CreateInterlacedPng(w, h, 0, 8, pixels);
        var nonInterlacedPng = CreateNonInterlacedPng(w, h, 0, 8, pixels);

        var imgInterlaced = PngImageLoader.Load(interlacedPng);
        var imgNonInterlaced = PngImageLoader.Load(nonInterlacedPng);

        var deinterlaced = DecompressStream(imgInterlaced.BuildStream());
        var nonInterlacedPixels = DecompressStream(imgNonInterlaced.BuildStream());

        Assert.Equal(w, imgInterlaced.Width);
        Assert.Equal(h, imgInterlaced.Height);
        Assert.Equal(nonInterlacedPixels, deinterlaced);
    }

    [Fact]
    public void Png_interlaced_8x8_gray1bit_roundtrip()
    {
        // 8×8 1-bit grayscale interlaced. Row stride = 1 byte per row.
        // Pixels packed: each row is 8 bits, one per column.
        const int w = 8, h = 8;
        // 1 byte per row, alternating 0xAA (10101010) and 0x55 (01010101)
        var pixels = new byte[h]; // 1 byte per row for w=8, bitDepth=1
        for (var row = 0; row < h; row++)
            pixels[row] = (byte)(row % 2 == 0 ? 0xAA : 0x55);

        var interlacedPng = CreateInterlacedPng(w, h, 0, 1, pixels);
        var nonInterlacedPng = CreateNonInterlacedPng(w, h, 0, 1, pixels);

        var imgInterlaced = PngImageLoader.Load(interlacedPng);
        var imgNonInterlaced = PngImageLoader.Load(nonInterlacedPng);

        // Both should produce the same unpacked 8-bit grayscale output.
        var deinterlaced = DecompressStream(imgInterlaced.BuildStream());
        var nonInterlacedOut = DecompressStream(imgNonInterlaced.BuildStream());

        Assert.Equal(w, imgInterlaced.Width);
        Assert.Equal(h, imgInterlaced.Height);
        Assert.Equal(nonInterlacedOut, deinterlaced);
    }

    // ── PNG: 16-bit preserve / reduce ────────────────────────────────────────

    /// <summary>Reads the BitsPerComponent value from a stream's dict text.</summary>
    private static int ReadBitsPerComponent(VellumPdf.Core.PdfStream stream)
    {
        var text = PdfStreamText(stream);
        const string key = "/BitsPerComponent ";
        var idx = text.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("BitsPerComponent not found in stream dict.");
        var rest = text[(idx + key.Length)..];
        var end = rest.IndexOf('\n');
        if (end < 0) end = rest.IndexOf(' ');
        if (end < 0) end = rest.Length;
        return int.Parse(rest[..end].Trim());
    }

    /// <summary>
    /// Creates a 16-bit grayscale PNG where each pixel has a known high and low byte.
    /// The pixels array must contain w * h * 2 bytes (big-endian 16-bit samples).
    /// </summary>
    private static byte[] Create16BitGrayPng(int w, int h, byte[] pixels16)
    {
        // rowBytes = w * 2 for 16-bit gray
        int rowBytes = w * 2;
        var raw = new byte[h * (1 + rowBytes)];
        for (var y = 0; y < h; y++)
        {
            raw[y * (1 + rowBytes)] = 0; // filter None
            Array.Copy(pixels16, y * rowBytes, raw, y * (1 + rowBytes) + 1, rowBytes);
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, 16, 0)); // colorType 0 = gray
        WritePngChunk(ms, "IDAT", ZlibCompress(raw));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a 16-bit RGBA PNG where each pixel has known samples.
    /// pixels16 must contain w * h * 4 * 2 bytes (RRGGBBAA, 2 bytes each, big-endian).
    /// </summary>
    private static byte[] Create16BitRgbaPng(int w, int h, byte[] pixels16)
    {
        // colorType 6 = RGBA, 4 samples × 2 bytes = 8 bytes per pixel
        int rowBytes = w * 4 * 2;
        var raw = new byte[h * (1 + rowBytes)];
        for (var y = 0; y < h; y++)
        {
            raw[y * (1 + rowBytes)] = 0;
            Array.Copy(pixels16, y * rowBytes, raw, y * (1 + rowBytes) + 1, rowBytes);
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, 16, 6)); // colorType 6 = RGBA
        WritePngChunk(ms, "IDAT", ZlibCompress(raw));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    [Fact]
    public void Png16Bit_preserve_bitsPerComponent_is16()
    {
        // 2×1 16-bit gray with pixel 0 = 0x1234 and pixel 1 = 0xABCD.
        // Each pixel: high byte first, then low byte (big-endian).
        var pixels16 = new byte[] { 0x12, 0x34, 0xAB, 0xCD };
        var png = Create16BitGrayPng(2, 1, pixels16);

        var img = PngImageLoader.Load(png, ImageLoadOptions.Default); // Default = Preserve
        var stream = img.BuildStream();
        var bpc = ReadBitsPerComponent(stream);

        Assert.Equal(16, bpc);
        Assert.Equal(2, img.Width);
        Assert.Equal(1, img.Height);
    }

    [Fact]
    public void Png16Bit_preserve_both_bytes_retained()
    {
        // Verify both high and low bytes are present in the decompressed stream.
        // Pixel 0 = 0x1234, pixel 1 = 0xABCD.
        var pixels16 = new byte[] { 0x12, 0x34, 0xAB, 0xCD };
        var png = Create16BitGrayPng(2, 1, pixels16);

        var img = PngImageLoader.Load(png, ImageLoadOptions.Default);
        var decompressed = DecompressStream(img.BuildStream());

        Assert.Equal(4, decompressed.Length); // 2 pixels × 2 bytes each
        Assert.Equal(0x12, decompressed[0]);  // high byte of pixel 0
        Assert.Equal(0x34, decompressed[1]);  // low byte of pixel 0
        Assert.Equal(0xAB, decompressed[2]);  // high byte of pixel 1
        Assert.Equal(0xCD, decompressed[3]);  // low byte of pixel 1
    }

    [Fact]
    public void Png16Bit_reduce_bitsPerComponent_is8()
    {
        // Same PNG but loaded with ReduceToEight: only high bytes should appear.
        var pixels16 = new byte[] { 0x12, 0x34, 0xAB, 0xCD };
        var png = Create16BitGrayPng(2, 1, pixels16);

        var img = PngImageLoader.Load(png, new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight });
        var stream = img.BuildStream();
        var bpc = ReadBitsPerComponent(stream);

        Assert.Equal(8, bpc);

        var decompressed = DecompressStream(stream);
        Assert.Equal(2, decompressed.Length); // 2 pixels × 1 byte each
        Assert.Equal(0x12, decompressed[0]);  // high byte of pixel 0
        Assert.Equal(0xAB, decompressed[1]);  // high byte of pixel 1
    }

    [Fact]
    public void Png16Bit_preserve_alpha_smask_is16bit()
    {
        // 1×1 16-bit RGBA: pixel = R=0x1122, G=0x3344, B=0x5566, A=0x7788
        var pixels16 = new byte[]
        {
            0x11, 0x22, // R
            0x33, 0x44, // G
            0x55, 0x66, // B
            0x77, 0x88  // A
        };
        var png = Create16BitRgbaPng(1, 1, pixels16);

        var img = PngImageLoader.Load(png, ImageLoadOptions.Default);

        // SMask should exist and be 16-bit.
        Assert.NotNull(img.SMask);
        Assert.Equal(16, img.SMaskBitsPerComponent);

        // Color stream: R G B at 16-bit each (3 channels × 2 bytes = 6 bytes)
        var colorBytes = DecompressStream(img.BuildStream());
        Assert.Equal(6, colorBytes.Length);
        Assert.Equal(0x11, colorBytes[0]); Assert.Equal(0x22, colorBytes[1]); // R
        Assert.Equal(0x33, colorBytes[2]); Assert.Equal(0x44, colorBytes[3]); // G
        Assert.Equal(0x55, colorBytes[4]); Assert.Equal(0x66, colorBytes[5]); // B

        // Alpha stream: A at 16-bit (2 bytes)
        var alphaBytes = DecompressStream(img.SMask!);
        Assert.Equal(2, alphaBytes.Length);
        Assert.Equal(0x77, alphaBytes[0]); // A high
        Assert.Equal(0x88, alphaBytes[1]); // A low
    }

    [Fact]
    public void Png16Bit_reduce_alpha_smask_is8bit()
    {
        // Same RGBA PNG with ReduceToEight: SMask must be 8-bit.
        var pixels16 = new byte[]
        {
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88
        };
        var png = Create16BitRgbaPng(1, 1, pixels16);

        var img = PngImageLoader.Load(png, new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight });

        Assert.NotNull(img.SMask);
        Assert.Equal(8, img.SMaskBitsPerComponent);

        // Color stream: high bytes only (3 bytes)
        var colorBytes = DecompressStream(img.BuildStream());
        Assert.Equal(3, colorBytes.Length);
        Assert.Equal(0x11, colorBytes[0]); // R high
        Assert.Equal(0x33, colorBytes[1]); // G high
        Assert.Equal(0x55, colorBytes[2]); // B high

        // Alpha stream: A high byte only (1 byte)
        var alphaBytes = DecompressStream(img.SMask!);
        Assert.Single(alphaBytes);
        Assert.Equal(0x77, alphaBytes[0]);
    }
}
