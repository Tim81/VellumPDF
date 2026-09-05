// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;

namespace VellumPdf.Reader;

/// <summary>The PNG colour type byte (ISO/IEC 15948 §11.2.2), as this encoder's own <c>Encode</c>
/// writes it into IHDR.</summary>
internal enum PngColorType : byte
{
    Grayscale = 0,
    Rgb = 2,
    Palette = 3,
    GrayscaleAlpha = 4,
    RgbAlpha = 6,
}

/// <summary>Table-driven CRC32 for a PNG chunk's own trailing checksum (ISO 3309; PNG Annex D):
/// reflected polynomial <c>0xEDB88320</c>, initial and final <c>0xFFFFFFFF</c>, computed over the
/// chunk's type bytes followed by its data. Built once, at start-up, from the polynomial; nothing
/// here is copied from a third-party table.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>Computes the CRC32 of <paramref name="type"/> followed by <paramref name="data"/>,
    /// as one continuous run: a PNG chunk's own CRC covers both together, not each
    /// separately.</summary>
    internal static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}

/// <summary> Writes a lossless PNG (ISO/IEC 15948) from decoded PDF image samples (#98). <see
/// cref="Encode"/> is the low-level chunk writer; <see cref="CanEncode"/>, <see cref="TryEncode"/>,
/// and <see cref="TryEncodeWithAlpha"/> map a <see cref="PdfExtractedImage"/>'s colour space onto a
/// PNG colour type and bit depth (ISO/IEC 15948, the IHDR chunk's colour-type/bit-depth table), and
/// back <see cref="PdfExtractedImage.CanEncodePng"/>/<see cref="PdfExtractedImage.TryEncodePng"/>/
/// <see cref="PdfExtractedImage.TryEncodePngWithAlpha"/>.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary> Writes one PNG file: the signature, an IHDR (compression 0, filter 0, interlace
    /// 0), an optional PLTE, one IDAT holding every row (each prefixed by a filter byte of 0, the
    /// whole stream <see cref="CompressionLevel.Optimal"/> zlib-compressed), and IEND. <paramref
    /// name="rows"/> holds exactly <c>height * rowBytes</c> bytes of sample data with no filter
    /// bytes of its own; this method inserts those. Samples are copied unchanged: ISO 32000-2
    /// §8.9.3 and PNG both pack samples MSB-first, both give 16-bit units most significant byte
    /// first, and both align each row to a byte boundary, so a <c>Raw</c> buffer at 1, 2, 4, 8, or
    /// 16 bits per component is already PNG-shaped.
    /// </summary>
    internal static byte[] Encode(
        int width, int height, int bitDepth, PngColorType type,
        ReadOnlySpan<byte> palette, ReadOnlySpan<byte> rows, int rowBytes)
    {
        var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)type;
        ihdr[10] = 0; // Compression method 0 (the only one PNG defines).
        ihdr[11] = 0; // Filter method 0 (the only one PNG defines); this encoder always uses row filter 0 (None).
        ihdr[12] = 0; // Not interlaced.
        WriteChunk(output, "IHDR"u8, ihdr);

        if (type == PngColorType.Palette)
            WriteChunk(output, "PLTE"u8, palette);

        WriteChunk(output, "IDAT"u8, BuildIdatPayload(rows, rowBytes, height));
        WriteChunk(output, "IEND"u8, []);

        return output.ToArray();
    }

    private static byte[] BuildIdatPayload(ReadOnlySpan<byte> rows, int rowBytes, int height)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            Span<byte> filterByte = [0];
            for (var row = 0; row < height; row++)
            {
                z.Write(filterByte);
                z.Write(rows.Slice(row * rowBytes, rowBytes));
            }
        }
        return ms.ToArray();
    }

    private static void WriteChunk(MemoryStream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuf, data.Length);
        output.Write(lengthBuf);
        output.Write(type);
        output.Write(data);

        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, Crc32.Compute(type, data));
        output.Write(crcBuf);
    }

    // ── Mapping a PdfExtractedImage onto a PNG shape ────────────────────────────────────────────

    /// <summary>See <see cref="PdfExtractedImage.CanEncodePng"/>.</summary>
    internal static bool CanEncode(PdfExtractedImage image) =>
        TryDetermineMapping(image, out _, out _, out _);

    /// <summary>See <see cref="PdfExtractedImage.TryEncodePng"/>.</summary>
    internal static bool TryEncode(PdfExtractedImage image, out byte[]? png)
    {
        if (!TryDetermineMapping(image, out var bitDepth, out var colorType, out var palette))
        {
            png = null;
            return false;
        }

        var rowBytes = (int)(image.ExpectedSampleDataLength / image.Height);
        png = Encode(image.Width, image.Height, bitDepth, colorType, palette, image.Data.Span, rowBytes);
        return true;
    }

    /// <summary>See <see cref="PdfExtractedImage.TryEncodePngWithAlpha"/>.</summary>
    internal static bool TryEncodeWithAlpha(PdfExtractedImage image, out byte[]? png)
    {
        png = null;
        if (!TryDetermineMapping(image, out var bitDepth, out var colorType, out var palette))
            return false;

        var mask = image.SoftMask;
        if (mask is null || mask.Encoding != PdfImageEncoding.Raw || mask.HasMatte
            || mask.Width != image.Width || mask.Height != image.Height)
            return false;

        // The PNG output depth this image maps to (8 for an Indexed image, since its own colour
        // channel is expanded to 8-bit RGB below; otherwise bitDepth itself), which is not
        // mask.BitsPerComponent's own meaning: for an Indexed image that is the INDEX depth, a
        // different number from the sample depth the interleaved alpha channel must match.
        var pngSampleDepth = colorType == PngColorType.Palette ? 8 : bitDepth;
        var maskComponentCount = mask.IsStencilMask ? 1 : mask.ColorSpace?.ComponentCount ?? 0;
        if (maskComponentCount != 1 || mask.BitsPerComponent != pngSampleDepth
            || mask.Data.Length < mask.ExpectedSampleDataLength)
            return false;

        // Table 143 permits a Decode entry on a soft mask, default [0 1]; this method interleaves
        // the mask's stored bytes unchanged (per TryEncodePngWithAlpha's own doc), which is PNG
        // alpha only under that default mapping. A [1 0] mask, or any other non-default array,
        // would write an inverted alpha channel with no diagnostic to explain it, so it is refused
        // instead.
        if (mask.Decode is { Count: 2 } maskDecode && (maskDecode[0] != 0 || maskDecode[1] != 1))
            return false;

        byte[] colorRows;
        PngColorType alphaColorType;
        int colorBitDepth;
        if (colorType == PngColorType.Palette)
        {
            // Lossless: the palette already holds the exact RGB triple for every index, so
            // expanding an index to its triple is a lookup, not a resampling step.
            colorRows = ExpandIndexedToRgb8(image, bitDepth, palette);
            alphaColorType = PngColorType.RgbAlpha;
            colorBitDepth = 8;
        }
        else
        {
            colorRows = image.Data.ToArray();
            alphaColorType = colorType == PngColorType.Grayscale ? PngColorType.GrayscaleAlpha : PngColorType.RgbAlpha;
            colorBitDepth = bitDepth;
        }

        // ISO/IEC 15948's IHDR colour-type/bit-depth table permits colour type 4
        // (GrayscaleAlpha) and 6 (RgbAlpha) only at bit depths 8 and 16. A grey image at 1, 2, or 4
        // bits reaches this method (TryDetermineMapping accepts those depths for colour type 0,
        // which has no such restriction), but an alpha channel cannot be interleaved onto it: the
        // Indexed branch above always forces colorBitDepth to 8, so only the grey/RGB branch can
        // still be sub-byte here.
        if (colorBitDepth is not (8 or 16))
            return false;

        var samplesPerPixel = alphaColorType == PngColorType.GrayscaleAlpha ? 1 : 3;
        var bytesPerSample = colorBitDepth / 8;
        var colorPixelBytes = samplesPerPixel * bytesPerSample;
        var alphaPixelBytes = bytesPerSample;
        var colorRowBytes = image.Width * colorPixelBytes;
        var alphaRowBytes = image.Width * alphaPixelBytes;
        var outRowBytes = colorRowBytes + alphaRowBytes;

        var interleaved = new byte[outRowBytes * image.Height];
        var maskData = mask.Data.Span;
        for (var row = 0; row < image.Height; row++)
        {
            var colorRowStart = row * colorRowBytes;
            var alphaRowStart = row * alphaRowBytes;
            var outRowStart = row * outRowBytes;
            for (var px = 0; px < image.Width; px++)
            {
                var outPixelStart = outRowStart + px * (colorPixelBytes + alphaPixelBytes);
                for (var b = 0; b < colorPixelBytes; b++)
                    interleaved[outPixelStart + b] = colorRows[colorRowStart + px * colorPixelBytes + b];
                for (var b = 0; b < alphaPixelBytes; b++)
                    interleaved[outPixelStart + colorPixelBytes + b] = maskData[alphaRowStart + px * alphaPixelBytes + b];
            }
        }

        png = Encode(image.Width, image.Height, colorBitDepth, alphaColorType, [], interleaved, outRowBytes);
        return true;
    }

    private static byte[] ExpandIndexedToRgb8(PdfExtractedImage image, int bitDepth, byte[] palette)
    {
        var width = image.Width;
        var height = image.Height;
        var srcRowBytes = (int)(image.ExpectedSampleDataLength / height);
        var dstRowBytes = width * 3;
        var output = new byte[dstRowBytes * height];
        var data = image.Data.Span;
        var samplesPerByte = 8 / bitDepth;
        var mask = (1 << bitDepth) - 1;

        for (var row = 0; row < height; row++)
        {
            var srcRowStart = row * srcRowBytes;
            var dstRowStart = row * dstRowBytes;
            for (var x = 0; x < width; x++)
            {
                int index;
                if (bitDepth == 8)
                {
                    index = data[srcRowStart + x];
                }
                else
                {
                    var byteIndex = srcRowStart + x / samplesPerByte;
                    var shift = 8 - bitDepth - (x % samplesPerByte) * bitDepth;
                    index = (data[byteIndex] >> shift) & mask;
                }
                var paletteOffset = index * 3;
                var dstOffset = dstRowStart + x * 3;
                output[dstOffset] = palette[paletteOffset];
                output[dstOffset + 1] = palette[paletteOffset + 1];
                output[dstOffset + 2] = palette[paletteOffset + 2];
            }
        }
        return output;
    }

    private static bool TryDetermineMapping(
        PdfExtractedImage image, out int bitDepth, out PngColorType colorType, out byte[] palette)
    {
        bitDepth = 0;
        colorType = PngColorType.Grayscale;
        palette = [];

        if (image.Encoding != PdfImageEncoding.Raw)
            return false;
        if (image.BitsPerComponent is not (1 or 2 or 4 or 8 or 16))
            return false;
        if (image.Data.Length < image.ExpectedSampleDataLength)
            return false;

        if (image.IsStencilMask)
        {
            bitDepth = 1;
            colorType = PngColorType.Grayscale;
            return true;
        }

        var cs = image.ColorSpace;
        if (cs is null)
            return false;

        var isGrayLike = cs.Family is PdfImageColorSpaceFamily.DeviceGray or PdfImageColorSpaceFamily.CalGray
            || (cs.Family == PdfImageColorSpaceFamily.IccBased && cs.ComponentCount == 1);
        if (isGrayLike)
        {
            bitDepth = image.BitsPerComponent;
            colorType = PngColorType.Grayscale;
            return true;
        }

        var isRgbLike = cs.Family is PdfImageColorSpaceFamily.DeviceRgb or PdfImageColorSpaceFamily.CalRgb
            || (cs.Family == PdfImageColorSpaceFamily.IccBased && cs.ComponentCount == 3);
        if (isRgbLike && image.BitsPerComponent is 8 or 16)
        {
            bitDepth = image.BitsPerComponent;
            colorType = PngColorType.Rgb;
            return true;
        }

        if (cs.Family == PdfImageColorSpaceFamily.Indexed && image.BitsPerComponent <= 8 && cs.Base is { } baseSpace
            && TryBuildPalette(baseSpace, cs, image.BitsPerComponent, out var built))
        {
            bitDepth = image.BitsPerComponent;
            colorType = PngColorType.Palette;
            palette = built;
            return true;
        }

        return false;
    }

    // PNG requires the palette not exceed what the bit depth can index (min(2^bitDepth, 256)
    // entries), while PDF bounds only hival <= 255 (§8.6.6.3), so a 256-entry lookup at
    // /BitsPerComponent 1 is legal PDF and not legal PNG at that depth: this builds exactly the
    // PNG-legal count, filling any index past hival with a copy of entry hival's own colour, which
    // is what §8.6.6.3 itself prescribes for an out-of-range index ("shall be adjusted to the
    // nearest value within that range").
    private static bool TryBuildPalette(
        PdfImageColorSpace baseSpace, PdfImageColorSpace indexed, int bitDepth, out byte[] palette)
    {
        palette = [];
        var isGray = baseSpace.Family is PdfImageColorSpaceFamily.DeviceGray or PdfImageColorSpaceFamily.CalGray
            || (baseSpace.Family == PdfImageColorSpaceFamily.IccBased && baseSpace.ComponentCount == 1);
        var isRgb = !isGray && (baseSpace.Family is PdfImageColorSpaceFamily.DeviceRgb or PdfImageColorSpaceFamily.CalRgb
            || (baseSpace.Family == PdfImageColorSpaceFamily.IccBased && baseSpace.ComponentCount == 3));
        if (!isGray && !isRgb)
            return false; // A CMYK, Lab, Separation, or DeviceN base has no lossless RGB mapping.

        var entryCount = Math.Min(1 << bitDepth, 256);
        var lookup = indexed.Lookup.Span;
        var baseComponents = baseSpace.ComponentCount;
        var built = new byte[entryCount * 3];

        for (var i = 0; i < entryCount; i++)
        {
            var sourceIndex = Math.Min(i, indexed.HighValue);
            var lookupOffset = sourceIndex * baseComponents;
            if (isGray)
            {
                var g = lookup[lookupOffset];
                built[i * 3] = g;
                built[i * 3 + 1] = g;
                built[i * 3 + 2] = g;
            }
            else
            {
                built[i * 3] = lookup[lookupOffset];
                built[i * 3 + 1] = lookup[lookupOffset + 1];
                built[i * 3 + 2] = lookup[lookupOffset + 2];
            }
        }

        palette = built;
        return true;
    }
}
