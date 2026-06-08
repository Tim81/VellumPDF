// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Decodes a Windows BMP file and produces a FlateDecode Image XObject.
///
/// Supported variants:
///   • BITMAPINFOHEADER (40-byte header), BI_RGB uncompressed only.
///   • 24-bit RGB: the 4th byte of each 32-bit quad is exposed as an /SMask alpha channel.
///   • 32-bit RGBA: blue–green–red–alpha order; alpha plane is emitted as /SMask.
///   • 8-bit palette-indexed: colour map is expanded to DeviceRGB.
///   • Both bottom-up (positive height) and top-down (negative height) row orders.
///
/// Rejected variants: compressed bitmaps (BI_RLE8, BI_RLE4, BI_BITFIELDS, etc.),
/// OS/2 BITMAPCOREHEADER, and bit depths other than 8, 24, and 32.
/// </summary>
public static class BmpImageLoader
{
    // BMP compression constants
    private const uint BiRgb = 0;

    public static PdfImageXObject Load(byte[] bmpBytes)
    {
        if (bmpBytes.Length < 54)
            throw new InvalidDataException("BMP file too small.");
        if (bmpBytes[0] != 0x42 || bmpBytes[1] != 0x4D) // 'BM'
            throw new InvalidDataException("Not a BMP file.");

        // Pixel data offset
        var pixelOffset = ReadU32Le(bmpBytes, 10);

        // DIB header size
        var headerSize = ReadU32Le(bmpBytes, 14);
        if (headerSize != 40)
            throw new NotSupportedException(
                $"Only BITMAPINFOHEADER (40-byte DIB header) is supported; found {headerSize}-byte header.");

        var rawWidth = ReadS32Le(bmpBytes, 18);
        var rawHeight = ReadS32Le(bmpBytes, 22);
        var bitCount = ReadU16Le(bmpBytes, 28);
        var compression = ReadU32Le(bmpBytes, 30);

        if (compression != BiRgb)
            throw new NotSupportedException(
                $"Only BI_RGB (uncompressed) BMP is supported; found compression={compression}.");

        if (bitCount is not (8 or 24 or 32))
            throw new NotSupportedException(
                $"Only 8-, 24-, and 32-bit BMP are supported; found {bitCount}-bit.");

        // Reject int.MinValue height (Math.Abs would overflow) and absurd dimensions.
        if (rawHeight == int.MinValue)
            throw new InvalidDataException("BMP height value is invalid (int.MinValue).");

        var width = Math.Abs(rawWidth);
        var height = Math.Abs(rawHeight);
        var bottomUp = rawHeight > 0; // positive height = bottom-up storage

        // Reject absurd dimensions before allocating (> 100M pixels).
        var pixelCount = (long)width * height;
        if (pixelCount > 100_000_000L)
            throw new InvalidDataException($"BMP dimensions {width}×{height} exceed the 100M pixel safety limit.");

        // Validate pixelOffset is within the file.
        if (pixelOffset >= (uint)bmpBytes.Length)
            throw new InvalidDataException($"BMP pixel data offset {pixelOffset} is beyond the end of the file.");

        // Validate the palette region (for 8-bit images) is within the file.
        if (bitCount == 8)
        {
            const int paletteOffset = 54;
            const int paletteSize = 256 * 4;
            if (paletteOffset + paletteSize > bmpBytes.Length)
                throw new InvalidDataException("BMP file is truncated: palette extends beyond end of file.");
        }

        return bitCount switch
        {
            8 => Load8Bit(bmpBytes, width, height, bottomUp, pixelOffset),
            24 => Load24Bit(bmpBytes, width, height, bottomUp, pixelOffset),
            _ => Load32Bit(bmpBytes, width, height, bottomUp, pixelOffset),
        };
    }

    // ── 8-bit palette-indexed ────────────────────────────────────────────────

    private static PdfImageXObject Load8Bit(
        byte[] data, int width, int height, bool bottomUp, uint pixelOffset)
    {
        // Colour table starts at offset 54 (after BITMAPINFOHEADER).
        // Each entry is RGBQUAD: blue, green, red, reserved (4 bytes).
        var colorTableOffset = 54;
        var rgb = new byte[width * height * 3];
        var rowStride = (width + 3) & ~3; // DWORD-aligned

        for (var y = 0; y < height; y++)
        {
            var srcRow = bottomUp ? height - 1 - y : y;
            var rowBase = (int)pixelOffset + srcRow * rowStride;
            for (var x = 0; x < width; x++)
            {
                var idx = data[rowBase + x];
                var entry = colorTableOffset + idx * 4;
                var dst = (y * width + x) * 3;
                rgb[dst] = data[entry + 2];     // red
                rgb[dst + 1] = data[entry + 1]; // green
                rgb[dst + 2] = data[entry];     // blue
            }
        }
        return new PdfImageXObject(width, height, rgb, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);
    }

    // ── 24-bit RGB ───────────────────────────────────────────────────────────

    private static PdfImageXObject Load24Bit(
        byte[] data, int width, int height, bool bottomUp, uint pixelOffset)
    {
        // Each row is padded to a DWORD boundary.
        var rowStride = (width * 3 + 3) & ~3;
        var rgb = new byte[width * height * 3];

        for (var y = 0; y < height; y++)
        {
            var srcRow = bottomUp ? height - 1 - y : y;
            var rowBase = (int)pixelOffset + srcRow * rowStride;
            for (var x = 0; x < width; x++)
            {
                // BMP stores BGR
                var src = rowBase + x * 3;
                var dst = (y * width + x) * 3;
                rgb[dst] = data[src + 2];     // R
                rgb[dst + 1] = data[src + 1]; // G
                rgb[dst + 2] = data[src];     // B
            }
        }
        return new PdfImageXObject(width, height, rgb, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);
    }

    // ── 32-bit BGRA ──────────────────────────────────────────────────────────

    private static PdfImageXObject Load32Bit(
        byte[] data, int width, int height, bool bottomUp, uint pixelOffset)
    {
        // 32-bit rows are inherently DWORD-aligned; no padding needed.
        var rowStride = width * 4;
        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        var hasNonOpaqueAlpha = false;

        for (var y = 0; y < height; y++)
        {
            var srcRow = bottomUp ? height - 1 - y : y;
            var rowBase = (int)pixelOffset + srcRow * rowStride;
            for (var x = 0; x < width; x++)
            {
                // BMP stores BGRA
                var src = rowBase + x * 4;
                var dst = y * width + x;
                rgb[dst * 3] = data[src + 2];     // R
                rgb[dst * 3 + 1] = data[src + 1]; // G
                rgb[dst * 3 + 2] = data[src];     // B
                var a = data[src + 3];
                alpha[dst] = a;
                if (a != 255) hasNonOpaqueAlpha = true;
            }
        }

        PdfStream? sMask = hasNonOpaqueAlpha ? new PdfStream(alpha) : null;
        return new PdfImageXObject(width, height, rgb, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8, sMask);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static uint ReadU32Le(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) |
               (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static int ReadS32Le(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8) |
        (data[offset + 2] << 16) | (data[offset + 3] << 24);

    private static ushort ReadU16Le(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));
}
