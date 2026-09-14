// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Decodes a Windows BMP file and produces a FlateDecode Image XObject.
///
/// Supported variants:
///   • BITMAPINFOHEADER (40-byte header), BI_RGB uncompressed only.
///   • 24-bit RGB: blue–green–red order, three bytes per pixel and no alpha.
///   • 32-bit RGBA: blue–green–red–alpha order; the alpha plane is emitted as an /SMask only
///     when some pixel is not fully opaque.
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

    /// <summary>Decodes BMP file bytes into a FlateDecode Image XObject.</summary>
    /// <exception cref="InvalidDataException">
    /// The bytes are not a BMP file, are truncated, or declare dimensions outside the safety
    /// limit below.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The file is a well-formed BMP of a variant this loader does not read.
    /// </exception>
    /// <remarks>
    /// Treat the input as untrusted, and catch two types rather than one. A malformed file
    /// raises <see cref="InvalidDataException"/>: a wrong signature, a truncated stream, or a
    /// declared size the limit refuses. A well-formed file that this loader cannot read raises
    /// <see cref="NotSupportedException"/> instead. That type does <b>not</b> derive from the
    /// first, so catching only <see cref="InvalidDataException"/> lets it escape.
    /// <para><see langword="null"/> is not checked. It raises
    /// <see cref="NullReferenceException"/>, not <see cref="ArgumentNullException"/>, so a caller
    /// guarding on the documented type will not catch it. You have to reject null yourself. A
    /// later major version will check it.</para>
    /// <para>One size limit applies: a declared pixel count above <b>100,000,000</b> is refused.
    /// Neither edge is limited on its own, so 2,000,000 by 1 is accepted and 10,001 by 10,001 is
    /// not. The limit is internal and has no public setting. It bounds the damage a header can do
    /// rather than preventing it: the raster is sized from the declared dimensions before the
    /// pixel data is checked, so a 1,079-byte file declaring 10,000 by 10,000 still allocates
    /// 300 MB before it refuses (#536).</para>
    /// <para>This loader reads one variant: an uncompressed 40-byte BITMAPINFOHEADER bitmap at
    /// 8, 24 or 32 bits per pixel. Every other well-formed BMP raises
    /// <see cref="NotSupportedException"/>, compressed ones included, and there is no fallback
    /// path. Size is checked first, so a file shorter than 54 bytes is reported as truncated
    /// whatever variant it would have been.</para>
    /// <para>Attention: one conforming 8-bit variant is refused. A bitmap may declare
    /// <c>biClrUsed</c> and ship a palette shorter than 256 entries; this loader never reads that
    /// field and tests the whole file against <b>1,078</b> bytes, so a short-palette file smaller
    /// than that is reported as truncated. A larger one loads and decodes correctly, so the
    /// refusal tracks the file's size and not its palette. If you hit it, pad the palette to 256
    /// entries and move <c>bfOffBits</c> with it; padding alone leaves the offset pointing
    /// into the palette and the image decodes black, with no exception (#533).</para>
    /// <para>Attention: a 32-bit bitmap loses its image entirely. The fourth byte of each pixel is
    /// emitted as a soft mask, but in the only 32-bit variant this loader accepts, <c>BI_RGB</c>
    /// with a 40-byte header, that byte is not alpha and the format says it is unused. Writers
    /// commonly leave it at zero, which this loader reads as fully transparent, so the page shows
    /// nothing and no exception is raised. Convert such a file to 24-bit before loading it
    /// (#535).</para>
    /// </remarks>
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

        // Reject int.MinValue dimensions (Math.Abs would overflow) before normalising.
        if (rawWidth == int.MinValue || rawHeight == int.MinValue)
            throw new InvalidDataException("BMP dimension value is invalid (int.MinValue).");

        var width = Math.Abs(rawWidth);
        var height = Math.Abs(rawHeight);
        var bottomUp = rawHeight > 0; // positive height = bottom-up storage

        // Reject non-positive and absurd dimensions before allocating.
        ImageLimits.ValidateDimensions("BMP", width, height);

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
        RequirePixelData(data, pixelOffset, height, rowStride);

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
        RequirePixelData(data, pixelOffset, height, rowStride);

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
        RequirePixelData(data, pixelOffset, height, rowStride);
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

    // Ensures the declared pixel region [pixelOffset, pixelOffset + height*rowStride) lies within
    // the file, so a truncated BMP fails cleanly instead of throwing IndexOutOfRangeException.
    private static void RequirePixelData(byte[] data, uint pixelOffset, int height, int rowStride)
    {
        if ((long)pixelOffset + (long)height * rowStride > data.Length)
            throw new InvalidDataException("BMP file is truncated: pixel data extends beyond end of file.");
    }
}
