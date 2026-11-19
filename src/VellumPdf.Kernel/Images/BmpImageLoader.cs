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
///   • 32-bit BI_RGB: four bytes per pixel in BGR order. The format leaves the fourth unused.
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

    // An 8-bit pixel indexes at most 256 colours.
    private const int MaxPaletteEntries = 256;

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
    /// not. The limit is internal and has no public setting. The pixel data is checked against the
    /// file length before the raster is allocated, so a header cannot cost more than the file
    /// carries: a 1,079-byte file declaring 10,000 by 10,000 is refused after under 2 KB.
    /// A file that does carry its pixel data still allocates the full raster, at least three
    /// bytes per pixel, so 300 MB at the limit.</para>
    /// <para>This loader reads one variant: an uncompressed 40-byte BITMAPINFOHEADER bitmap at
    /// 8, 24 or 32 bits per pixel. Every other well-formed BMP raises
    /// <see cref="NotSupportedException"/>, compressed ones included, and there is no fallback
    /// path. Size is checked first, so a file shorter than 54 bytes is reported as truncated
    /// whatever variant it would have been.</para>
    /// <para>An 8-bit bitmap may carry fewer than 256 palette entries. This loader reads
    /// <c>biClrUsed</c> (zero means 256, and a larger value is capped at 256) and requires the
    /// file to hold that many entries after the header. A file declaring 4 entries is 86 bytes for
    /// a 4 by 4 image and loads. A palette that does not fit raises
    /// <see cref="InvalidDataException"/> as truncated, which is true of it. Before 2.4 the field
    /// was ignored and any 8-bit file under 1,078 bytes was refused as truncated whatever it
    /// declared (#533). A file of 1,078 bytes or more still loads as it did then, indexing the
    /// table as 256 entries, so a pixel pointing past <c>biClrUsed</c> reads whatever bytes lie in
    /// that slot. A smaller file is held to the entries it declares: a pixel pointing past them, or
    /// pixel data starting inside the palette, raises <see cref="InvalidDataException"/>.</para>
    /// <para>That smaller-file branch is the only one that reads header fields beyond the ones
    /// needed to decode. A file under 1,078 bytes is accepted only if <c>biPlanes</c> is 1, both
    /// reserved words are zero, <c>biWidth</c> is positive, <c>bfSize</c> equals the file length,
    /// <c>biSizeImage</c> is zero or at least the pixel data the header implies, and
    /// <c>biClrImportant</c> is zero or no more than the entries in use. Any other file of that
    /// size raises <see cref="InvalidDataException"/>, as 2.3.3 did. A file of 1,078 bytes or more
    /// is never held to them, because 2.3.3 ignored them there.</para>
    /// <para>Any other BITMAPINFOHEADER field this loader does not read is neither honoured nor
    /// refused.</para>
    /// <para>A 32-bit bitmap is read as opaque. In <c>BI_RGB</c> with a 40-byte header the fourth
    /// byte of each pixel is unused, so this loader ignores it and returns no soft mask. A file
    /// whose writer filled that byte with transparency values loses them: alpha is carried by
    /// <c>BI_BITFIELDS</c>, <c>BI_ALPHABITFIELDS</c> and the V4 and V5 headers, and this loader
    /// refuses all four with <see cref="NotSupportedException"/>. Before 2.4 the byte was read as
    /// alpha, so a file written with zero there loaded fully transparent (#535).</para>
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

        // 8-bit: the colour table follows the header and holds biClrUsed entries, or 256 when the
        // field is zero. Source: Microsoft Learn, "BITMAPINFOHEADER structure" (Windows GDI,
        // wingdi.h; learn.microsoft.com/en-us/previous-versions/dd183376(v=vs.85)), Members,
        // biClrUsed: "If this value is zero, the bitmap uses the maximum number of colors
        // corresponding to the value of the biBitCount member". The field is the writer's, so it
        // is capped at 256 before it is multiplied.
        var paletteLimit = 0;
        if (bitCount == 8)
        {
            const int paletteOffset = 54;
            var clrUsed = ReadU32Le(bmpBytes, 46);
            var entries = clrUsed is 0 or > MaxPaletteEntries ? MaxPaletteEntries : (int)clrUsed;
            if (paletteOffset + entries * 4 > bmpBytes.Length)
                throw new InvalidDataException("BMP file is truncated: palette extends beyond end of file.");

            // 2.3.3 never read biClrUsed and accepted any file of at least 1,078 bytes, indexing
            // the table as if it held 256 entries whatever the field said. Such a file keeps
            // loading exactly as it did, so an index past biClrUsed still reads the bytes at that
            // slot. Below 1,078 bytes the file was refused, and nothing that was refused is accepted
            // unless the file shows it is a conforming bitmap: RequireConformingHeader. The table
            // is also held to the entries it declares, so a pixel pointing past them, or pixel
            // data that starts inside the table, is refused rather than read from the wrong place.
            if (bmpBytes.Length >= paletteOffset + MaxPaletteEntries * 4)
            {
                paletteLimit = MaxPaletteEntries;
            }
            else
            {
                RequireConformingHeader(bmpBytes, rawWidth, height, entries);
                if (pixelOffset < (uint)(paletteOffset + entries * 4))
                    throw new InvalidDataException(
                        $"BMP pixel data offset {pixelOffset} lies inside the {entries}-entry palette.");
                paletteLimit = entries;
            }
        }

        return bitCount switch
        {
            8 => Load8Bit(bmpBytes, width, height, bottomUp, pixelOffset, paletteLimit),
            24 => Load24Bit(bmpBytes, width, height, bottomUp, pixelOffset),
            _ => Load32Bit(bmpBytes, width, height, bottomUp, pixelOffset),
        };
    }

    // ── 8-bit palette-indexed ────────────────────────────────────────────────

    private static PdfImageXObject Load8Bit(
        byte[] data, int width, int height, bool bottomUp, uint pixelOffset, int paletteLimit)
    {
        // Colour table starts at offset 54 (after BITMAPINFOHEADER).
        // Each entry is RGBQUAD: blue, green, red, reserved (4 bytes).
        var colorTableOffset = 54;
        var rowStride = (width + 3) & ~3; // DWORD-aligned
        RequirePixelData(data, pixelOffset, height, rowStride);
        var rgb = new byte[width * height * 3];

        for (var y = 0; y < height; y++)
        {
            var srcRow = bottomUp ? height - 1 - y : y;
            var rowBase = (int)pixelOffset + srcRow * rowStride;
            for (var x = 0; x < width; x++)
            {
                var idx = data[rowBase + x];
                if (idx >= paletteLimit)
                    throw new InvalidDataException(
                        $"BMP pixel refers to palette entry {idx}, but the palette holds {paletteLimit} entries.");
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
        RequirePixelData(data, pixelOffset, height, rowStride);
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

    // ── 32-bit BGRx ──────────────────────────────────────────────────────────

    private static PdfImageXObject Load32Bit(
        byte[] data, int width, int height, bool bottomUp, uint pixelOffset)
    {
        // 32-bit rows are inherently DWORD-aligned; no padding needed.
        var rowStride = width * 4;
        RequirePixelData(data, pixelOffset, height, rowStride);
        var rgb = new byte[width * height * 3];

        for (var y = 0; y < height; y++)
        {
            var srcRow = bottomUp ? height - 1 - y : y;
            var rowBase = (int)pixelOffset + srcRow * rowStride;
            for (var x = 0; x < width; x++)
            {
                // BI_RGB at 32 bits per pixel stores blue, green, red and then a byte the format
                // leaves unused. Microsoft Learn, "BITMAPINFOHEADER structure" (Windows GDI, wingdi.h;
                // learn.microsoft.com/en-us/previous-versions/dd183376(v=vs.85)), Members, biBitCount,
                // value 32: "The high byte in each DWORD is not used". Writers leave whatever is there, and
                // zero is common, so reading it as alpha made such a file fully transparent (#535).
                // Alpha needs BI_BITFIELDS, BI_ALPHABITFIELDS or a V4/V5 header, none of which this
                // loader accepts.
                var src = rowBase + x * 4;
                var dst = (y * width + x) * 3;
                rgb[dst] = data[src + 2];     // R
                rgb[dst + 1] = data[src + 1]; // G
                rgb[dst + 2] = data[src];     // B
            }
        }
        return new PdfImageXObject(width, height, rgb, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // A file under 1,078 bytes was refused by 2.3.3 whatever it held, so reading it now is only
    // right when it is a conforming bitmap that 2.3.3 refused because of the palette-size bug, not
    // one that merely got past the old size test. Each check below is a field Microsoft's reference
    // constrains. 2.3.3 ignored all of them, so a file of 1,078 bytes or more is never held to them.
    //
    // Sources, Microsoft Learn:
    //   - "BITMAPFILEHEADER structure" (learn.microsoft.com/en-us/windows/win32/api/wingdi/
    //     ns-wingdi-bitmapfileheader), Members: bfSize "The size, in bytes, of the bitmap file.";
    //     bfReserved1 and bfReserved2 "Reserved; must be zero."
    //   - "BITMAPINFOHEADER structure" (learn.microsoft.com/en-us/previous-versions/
    //     dd183376(v=vs.85)), Members: biPlanes "This value must be set to 1."; biSizeImage "The
    //     size, in bytes, of the image. This may be set to zero for BI_RGB bitmaps."; biClrImportant
    //     "The number of color indexes that are required for displaying the bitmap. If this value
    //     is zero, all colors are required."
    //
    // Three requirements are inferences, not quotations. biWidth must be positive: the page gives a
    // sign convention for biHeight only, so a negative width has no defined meaning. biSizeImage,
    // when not zero, must cover the pixel data the header implies, since a smaller figure
    // contradicts "the size, in bytes, of the image". biClrImportant must not exceed the entries in
    // use, since it cannot require more colours than the table holds.
    private static void RequireConformingHeader(byte[] data, int rawWidth, int height, int paletteEntries)
    {
        if (ReadU16Le(data, 26) != 1)
            throw NonConforming("biPlanes is not 1");
        if (ReadU16Le(data, 6) != 0 || ReadU16Le(data, 8) != 0)
            throw NonConforming("a reserved field is not zero");
        if (rawWidth <= 0)
            throw NonConforming("biWidth is not positive");
        if (ReadU32Le(data, 2) != (uint)data.Length)
            throw NonConforming("bfSize is not the file length");
        var rowStride = (rawWidth + 3) & ~3;
        var sizeImage = ReadU32Le(data, 34);
        if (sizeImage != 0 && sizeImage < (long)rowStride * height)
            throw NonConforming("biSizeImage is smaller than the pixel data the header implies");
        var clrImportant = ReadU32Le(data, 50);
        if (clrImportant != 0 && clrImportant > (uint)paletteEntries)
            throw NonConforming("biClrImportant exceeds the palette entries in use");
    }

    // The exception type 2.3.3 gave every file of this size, with the field named.
    private static InvalidDataException NonConforming(string reason) =>
        new("BMP file is truncated: palette extends beyond end of file. " +
            $"A bitmap this small is read only when its header conforms, and {reason}.");

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
