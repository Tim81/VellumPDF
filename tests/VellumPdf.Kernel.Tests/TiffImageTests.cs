// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for TiffImageLoader — all TIFF files are synthesised in-memory.
///
/// Covers:
///   • Little-endian (II) and big-endian (MM) byte orders.
///   • Compression: 1 (None) and 32773 (PackBits).
///   • Photometric: 0 (WhiteIsZero), 1 (BlackIsZero), 2 (RGB), 3 (Palette).
///   • SamplesPerPixel: 1 (grey), 3 (RGB), 4 (RGBA → /SMask).
///   • Round-trip pixel verification.
///   • Error cases: truncated data → InvalidDataException;
///     unsupported compression → NotSupportedException.
/// </summary>
public sealed class TiffImageTests
{
    // ── Dimensions / colour-space basic checks ────────────────────────────────

    [Fact]
    public void Tiff_LittleEndian_Rgb_Dimensions()
    {
        var tiff = CreateRgbTiff(4, 3, littleEndian: true, packBits: false, [0x11, 0x22, 0x33]);
        var img = TiffImageLoader.Load(tiff);
        Assert.Equal(4, img.Width);
        Assert.Equal(3, img.Height);
    }

    [Fact]
    public void Tiff_BigEndian_Rgb_Dimensions()
    {
        var tiff = CreateRgbTiff(4, 3, littleEndian: false, packBits: false, [0xAA, 0xBB, 0xCC]);
        var img = TiffImageLoader.Load(tiff);
        Assert.Equal(4, img.Width);
        Assert.Equal(3, img.Height);
    }

    [Fact]
    public void Tiff_Greyscale_Dimensions()
    {
        var tiff = CreateGreyscaleTiff(5, 2, littleEndian: true, whiteIsZero: false, value: 128);
        var img = TiffImageLoader.Load(tiff);
        Assert.Equal(5, img.Width);
        Assert.Equal(2, img.Height);
    }

    [Fact]
    public void Tiff_Palette_Dimensions()
    {
        var tiff = CreatePaletteTiff(3, 3, littleEndian: true, pixelIndex: 0);
        var img = TiffImageLoader.Load(tiff);
        Assert.Equal(3, img.Width);
        Assert.Equal(3, img.Height);
    }

    // ── Colour-space assertions (inferred from stream dictionary text) ─────────

    [Fact]
    public void Tiff_Rgb_ColorSpaceIsDeviceRgb()
    {
        var tiff = CreateRgbTiff(2, 2, littleEndian: true, packBits: false, [0xFF, 0x00, 0x00]);
        var img = TiffImageLoader.Load(tiff);
        Assert.Contains("DeviceRGB", StreamDictText(img));
    }

    [Fact]
    public void Tiff_Greyscale_ColorSpaceIsDeviceGray()
    {
        var tiff = CreateGreyscaleTiff(2, 2, littleEndian: true, whiteIsZero: false, value: 200);
        var img = TiffImageLoader.Load(tiff);
        Assert.Contains("DeviceGray", StreamDictText(img));
    }

    [Fact]
    public void Tiff_Palette_ColorSpaceIsDeviceRgb()
    {
        var tiff = CreatePaletteTiff(2, 2, littleEndian: true, pixelIndex: 0);
        var img = TiffImageLoader.Load(tiff);
        Assert.Contains("DeviceRGB", StreamDictText(img));
    }

    // ── Round-trip pixel correctness ──────────────────────────────────────────

    [Fact]
    public void Tiff_Rgb_LittleEndian_PixelRoundTrip()
    {
        // 2×1 image: pixel0 = (200,150,100), pixel1 = (10,20,30)
        var tiff = CreateRgbTiff2x1LittleEndian();
        var img = TiffImageLoader.Load(tiff);
        var pixels = DecompressStream(img.BuildStream());

        Assert.Equal(200, pixels[0]); // R
        Assert.Equal(150, pixels[1]); // G
        Assert.Equal(100, pixels[2]); // B
        Assert.Equal(10, pixels[3]);  // R
        Assert.Equal(20, pixels[4]);  // G
        Assert.Equal(30, pixels[5]);  // B
    }

    [Fact]
    public void Tiff_Rgb_BigEndian_PixelRoundTrip()
    {
        // Same image encoded big-endian
        var tiff = CreateRgbTiff2x1BigEndian();
        var img = TiffImageLoader.Load(tiff);
        var pixels = DecompressStream(img.BuildStream());

        Assert.Equal(200, pixels[0]);
        Assert.Equal(150, pixels[1]);
        Assert.Equal(100, pixels[2]);
        Assert.Equal(10, pixels[3]);
        Assert.Equal(20, pixels[4]);
        Assert.Equal(30, pixels[5]);
    }

    [Fact]
    public void Tiff_Greyscale_BlackIsZero_PixelRoundTrip()
    {
        // 1×1 grey image, value=200, BlackIsZero → output = 200
        var tiff = CreateGreyscaleTiff(1, 1, littleEndian: true, whiteIsZero: false, value: 200);
        var img = TiffImageLoader.Load(tiff);
        var pixels = DecompressStream(img.BuildStream());
        Assert.Equal(200, pixels[0]);
    }

    [Fact]
    public void Tiff_Greyscale_WhiteIsZero_IsInverted()
    {
        // 1×1 grey image, value=200, WhiteIsZero → output = 255-200 = 55
        var tiff = CreateGreyscaleTiff(1, 1, littleEndian: true, whiteIsZero: true, value: 200);
        var img = TiffImageLoader.Load(tiff);
        var pixels = DecompressStream(img.BuildStream());
        Assert.Equal(55, pixels[0]);
    }

    [Fact]
    public void Tiff_Palette_PixelRoundTrip()
    {
        // 1×1 palette image, index 0 = pure red (R=255, G=0, B=0 in the ColorMap)
        var tiff = CreatePaletteRoundTripTiff();
        var img = TiffImageLoader.Load(tiff);
        var pixels = DecompressStream(img.BuildStream());
        Assert.Equal(255, pixels[0]); // R
        Assert.Equal(0, pixels[1]);   // G
        Assert.Equal(0, pixels[2]);   // B
    }

    // ── PackBits compression ──────────────────────────────────────────────────

    [Fact]
    public void Tiff_PackBits_Rgb_Dimensions()
    {
        var tiff = CreateRgbTiff(4, 3, littleEndian: true, packBits: true, [0x80, 0x40, 0x20]);
        var img = TiffImageLoader.Load(tiff);
        Assert.Equal(4, img.Width);
        Assert.Equal(3, img.Height);
    }

    [Fact]
    public void Tiff_PackBits_Rgb_PixelRoundTrip()
    {
        // 2×1 RGB image with a single constant colour, encoded with PackBits replicate run
        var tiff = CreatePackBitsRgbTiff2x1();
        var img = TiffImageLoader.Load(tiff);
        var pixels = DecompressStream(img.BuildStream());
        // Both pixels should be (0xDE, 0xAD, 0xBE)
        Assert.Equal(0xDE, pixels[0]);
        Assert.Equal(0xAD, pixels[1]);
        Assert.Equal(0xBE, pixels[2]);
        Assert.Equal(0xDE, pixels[3]);
        Assert.Equal(0xAD, pixels[4]);
        Assert.Equal(0xBE, pixels[5]);
    }

    // ── Alpha / SMask ─────────────────────────────────────────────────────────

    [Fact]
    public void Tiff_Rgba_HasSmask()
    {
        var tiff = CreateRgbaTiff(2, 2, littleEndian: true, alphaValue: 128);
        var img = TiffImageLoader.Load(tiff);
        Assert.NotNull(img.SMask);
    }

    [Fact]
    public void Tiff_Rgba_SmaskContainsAlphaBytes()
    {
        var tiff = CreateRgbaTiff(1, 1, littleEndian: true, alphaValue: 77);
        var img = TiffImageLoader.Load(tiff);
        Assert.NotNull(img.SMask);
        var alphaMask = DecompressStream(img.SMask!);
        Assert.Equal(77, alphaMask[0]);
    }

    [Fact]
    public void Tiff_Rgba_FullyOpaque_NoSmask()
    {
        // All alpha = 255 → no SMask needed
        var tiff = CreateRgbaTiff(2, 2, littleEndian: true, alphaValue: 255);
        var img = TiffImageLoader.Load(tiff);
        Assert.Null(img.SMask);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Tiff_Truncated_ThrowsInvalidDataException()
    {
        var tiff = CreateRgbTiff(4, 4, littleEndian: true, packBits: false, [0xFF, 0x00, 0x00]);
        var truncated = tiff[..12]; // cut off to just the header
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(truncated));
    }

    [Fact]
    public void Tiff_TooSmall_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load([0x49, 0x49]));
    }

    [Fact]
    public void Tiff_BadMagic_ThrowsInvalidDataException()
    {
        var tiff = CreateRgbTiff(2, 2, littleEndian: true, packBits: false, [0xFF, 0x00, 0x00]);
        tiff[2] = 0x00; // corrupt magic from 42 to something else
        tiff[3] = 0x00;
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_UnsupportedCompression_LZW_ThrowsNotSupportedException()
    {
        var tiff = CreateRgbTiffWithCompression(2, 2, compressionValue: 5);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_UnsupportedCompression_Jpeg_ThrowsNotSupportedException()
    {
        var tiff = CreateRgbTiffWithCompression(2, 2, compressionValue: 6);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_UnsupportedCompression_Deflate_ThrowsNotSupportedException()
    {
        var tiff = CreateRgbTiffWithCompression(2, 2, compressionValue: 8);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_BadByteOrder_ThrowsInvalidDataException()
    {
        var tiff = new byte[] { 0x4C, 0x4C, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00 };
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_UnsupportedBitsPerSample_ThrowsNotSupportedException()
    {
        var tiff = CreateRgbTiffWithBitsPerSample(2, 2, bps: 4);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string StreamDictText(VellumPdf.Images.PdfImageXObject img)
    {
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
        img.BuildStream().WriteTo(writer);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    private static byte[] DecompressStream(VellumPdf.Core.PdfStream pdfStream)
    {
        using var pdfMs = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(pdfMs);
        pdfStream.WriteTo(writer);

        var raw = pdfMs.ToArray();
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        var start = markerStart + 8;
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

    // ── TIFF builder helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal uncompressed RGB or PackBits TIFF with all pixels set to the given RGB value.
    /// </summary>
    private static byte[] CreateRgbTiff(int w, int h, bool littleEndian, bool packBits, byte[] rgb)
    {
        var pixelData = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            pixelData[i * 3] = rgb[0];
            pixelData[i * 3 + 1] = rgb[1];
            pixelData[i * 3 + 2] = rgb[2];
        }

        var compressedData = packBits ? PackBitsEncode(pixelData) : pixelData;
        return BuildTiff(w, h, bitsPerSample: 8, compression: packBits ? 32773 : 1,
            photometric: 2, samplesPerPixel: 3, pixelData: compressedData,
            littleEndian: littleEndian, colorMap: null, alphaIncluded: false);
    }

    private static byte[] CreateRgbTiff2x1LittleEndian()
    {
        // Two specific pixels: (200,150,100) and (10,20,30)
        var pixelData = new byte[] { 200, 150, 100, 10, 20, 30 };
        return BuildTiff(2, 1, 8, 1, 2, 3, pixelData, littleEndian: true, colorMap: null, alphaIncluded: false);
    }

    private static byte[] CreateRgbTiff2x1BigEndian()
    {
        var pixelData = new byte[] { 200, 150, 100, 10, 20, 30 };
        return BuildTiff(2, 1, 8, 1, 2, 3, pixelData, littleEndian: false, colorMap: null, alphaIncluded: false);
    }

    private static byte[] CreateGreyscaleTiff(int w, int h, bool littleEndian, bool whiteIsZero, byte value)
    {
        var pixelData = new byte[w * h];
        Array.Fill(pixelData, value);
        return BuildTiff(w, h, 8, 1, photometric: whiteIsZero ? 0 : 1, samplesPerPixel: 1,
            pixelData, littleEndian, colorMap: null, alphaIncluded: false);
    }

    private static byte[] CreatePaletteTiff(int w, int h, bool littleEndian, byte pixelIndex)
    {
        var pixelData = new byte[w * h];
        Array.Fill(pixelData, pixelIndex);
        // 256-entry ColorMap: all black
        var colorMap = new ushort[768];
        return BuildTiff(w, h, 8, 1, photometric: 3, samplesPerPixel: 1,
            pixelData, littleEndian, colorMap, alphaIncluded: false);
    }

    private static byte[] CreatePaletteRoundTripTiff()
    {
        // 1×1 palette TIFF; palette index 0 maps to pure red (R=0xFFFF, G=0, B=0 in ColorMap)
        var pixelData = new byte[] { 0 }; // pixel index = 0
        var colorMap = new ushort[768];   // 256 entries each for R, G, B
        colorMap[0] = 0xFFFF;             // entry 0 Red channel = full (65535)
        // G and B channels for index 0 stay 0
        return BuildTiff(1, 1, 8, 1, photometric: 3, samplesPerPixel: 1,
            pixelData, littleEndian: true, colorMap, alphaIncluded: false);
    }

    private static byte[] CreateRgbaTiff(int w, int h, bool littleEndian, byte alphaValue)
    {
        var pixelData = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            pixelData[i * 4] = 0xFF;        // R
            pixelData[i * 4 + 1] = 0x80;    // G
            pixelData[i * 4 + 2] = 0x40;    // B
            pixelData[i * 4 + 3] = alphaValue; // A
        }
        return BuildTiff(w, h, 8, 1, photometric: 2, samplesPerPixel: 4,
            pixelData, littleEndian, colorMap: null, alphaIncluded: true);
    }

    private static byte[] CreatePackBitsRgbTiff2x1()
    {
        // 2×1 RGB image: 6 raw bytes = { DE,AD,BE, DE,AD,BE }
        // PackBits encode 6 literal bytes: header=0x05 (5 = count-1), then 6 bytes
        var rawPixels = new byte[] { 0xDE, 0xAD, 0xBE, 0xDE, 0xAD, 0xBE };
        var packed = PackBitsEncode(rawPixels);
        return BuildTiff(2, 1, 8, 32773, photometric: 2, samplesPerPixel: 3,
            packed, littleEndian: true, colorMap: null, alphaIncluded: false);
    }

    private static byte[] CreateRgbTiffWithCompression(int w, int h, int compressionValue)
    {
        var pixelData = new byte[w * h * 3];
        return BuildTiff(w, h, 8, compressionValue, photometric: 2, samplesPerPixel: 3,
            pixelData, littleEndian: true, colorMap: null, alphaIncluded: false);
    }

    private static byte[] CreateRgbTiffWithBitsPerSample(int w, int h, int bps)
    {
        var pixelData = new byte[w * h * 3];
        return BuildTiff(w, h, bps, compression: 1, photometric: 2, samplesPerPixel: 3,
            pixelData, littleEndian: true, colorMap: null, alphaIncluded: false);
    }

    /// <summary>
    /// Core TIFF builder: constructs a valid baseline TIFF with a single strip.
    /// </summary>
    private static byte[] BuildTiff(
        int w, int h,
        int bitsPerSample, int compression, int photometric, int samplesPerPixel,
        byte[] pixelData,
        bool littleEndian,
        ushort[]? colorMap,
        bool alphaIncluded)
    {
        using var ms = new MemoryStream();

        // We'll build everything in two passes using a MemoryStream:
        // 1. Write a placeholder header (we'll know IFD offset once we write pixel data).
        // 2. Write pixel data immediately after header at offset 8.
        // 3. Write ColorMap (if any) immediately after pixel data.
        // 4. Write the IFD immediately after.

        // Byte order + magic
        if (littleEndian)
        {
            ms.WriteByte(0x49); ms.WriteByte(0x49); // II
            ms.WriteByte(0x2A); ms.WriteByte(0x00); // magic 42 LE
        }
        else
        {
            ms.WriteByte(0x4D); ms.WriteByte(0x4D); // MM
            ms.WriteByte(0x00); ms.WriteByte(0x2A); // magic 42 BE
        }

        // Pixel data will be at offset 8
        var pixelOffset = 8u;

        // IFD starts right after pixel data (and ColorMap, if present)
        var colorMapByteLen = colorMap is not null ? (uint)(colorMap.Length * 2) : 0u;
        var colorMapOffset = pixelOffset + (uint)pixelData.Length;
        var ifdOffset = colorMapOffset + colorMapByteLen;

        // Write IFD offset into header
        WriteU32(ms, ifdOffset, littleEndian);

        // Write pixel data
        ms.Write(pixelData);

        // Write ColorMap if present
        if (colorMap is not null)
        {
            foreach (var v in colorMap)
                WriteU16(ms, v, littleEndian);
        }

        // Build IFD entries
        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),                   // ImageWidth (LONG)
            (257, 4, 1, (uint)h),                   // ImageLength (LONG)
            (258, 3, 1, (uint)bitsPerSample),        // BitsPerSample (SHORT)
            (259, 3, 1, (uint)compression),          // Compression (SHORT)
            (262, 3, 1, (uint)photometric),          // PhotometricInterpretation (SHORT)
            (273, 4, 1, pixelOffset),               // StripOffsets (single strip)
            (277, 3, 1, (uint)samplesPerPixel),      // SamplesPerPixel (SHORT)
            (278, 4, 1, (uint)h),                   // RowsPerStrip = height (single strip)
            (279, 4, 1, (uint)pixelData.Length),    // StripByteCounts
            (284, 3, 1, 1),                          // PlanarConfiguration = chunky
        };

        if (colorMap is not null)
        {
            entries.Add((320, 3, (uint)colorMap.Length, colorMapOffset)); // ColorMap — count too large, goes to offset
        }

        if (alphaIncluded)
        {
            entries.Add((338, 3, 1, 2)); // ExtraSamples = 2 (unassociated alpha)
        }

        // Sort entries by tag (TIFF spec requires ascending order)
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        // IFD entry count
        WriteU16(ms, (ushort)entries.Count, littleEndian);

        // For entries where the value fits in 4 bytes, write inline.
        // ColorMap value doesn't fit inline — we already set it as an offset.
        // We need to handle entries that use an offset for the value field:
        // - ColorMap has count * 2 > 4 bytes, so value field = offset (already set to colorMapOffset above)
        foreach (var (tag, type, count, value) in entries)
        {
            var typeSize = GetTypeSize(type);
            var totalBytes = typeSize * count;

            WriteU16(ms, tag, littleEndian);
            WriteU16(ms, type, littleEndian);
            WriteU32(ms, count, littleEndian);

            if (totalBytes <= 4)
            {
                // Value packed inline (padded to 4 bytes)
                if (littleEndian)
                {
                    if (type == 3) // SHORT
                    {
                        ms.WriteByte((byte)value);
                        ms.WriteByte((byte)(value >> 8));
                        ms.WriteByte(0); ms.WriteByte(0);
                    }
                    else // LONG
                    {
                        WriteU32(ms, value, littleEndian);
                    }
                }
                else
                {
                    if (type == 3) // SHORT
                    {
                        ms.WriteByte((byte)(value >> 8));
                        ms.WriteByte((byte)value);
                        ms.WriteByte(0); ms.WriteByte(0);
                    }
                    else // LONG
                    {
                        WriteU32(ms, value, littleEndian);
                    }
                }
            }
            else
            {
                // Value is an offset — write it as a 4-byte LONG
                WriteU32(ms, value, littleEndian);
            }
        }

        // Next IFD offset = 0 (no more IFDs)
        WriteU32(ms, 0, littleEndian);

        return ms.ToArray();
    }

    private static int GetTypeSize(ushort type) => type switch
    {
        1 => 1, // BYTE
        2 => 1, // ASCII
        3 => 2, // SHORT
        4 => 4, // LONG
        5 => 8, // RATIONAL
        _ => 1
    };

    /// <summary>
    /// Minimal PackBits encoder: encodes data as a single literal run.
    /// For runs up to 128 bytes, header = count-1; for larger data, splits into 128-byte chunks.
    /// </summary>
    private static byte[] PackBitsEncode(byte[] data)
    {
        using var ms = new MemoryStream();
        var pos = 0;
        while (pos < data.Length)
        {
            var count = Math.Min(128, data.Length - pos);
            ms.WriteByte((byte)(count - 1)); // literal run header
            ms.Write(data, pos, count);
            pos += count;
        }
        return ms.ToArray();
    }

    // ── Endian write helpers ──────────────────────────────────────────────────

    private static void WriteU16(Stream s, ushort v, bool le)
    {
        if (le)
        {
            s.WriteByte((byte)v);
            s.WriteByte((byte)(v >> 8));
        }
        else
        {
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }
    }

    private static void WriteU32(Stream s, uint v, bool le)
    {
        if (le)
        {
            s.WriteByte((byte)v);
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 24));
        }
        else
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }
    }
}
