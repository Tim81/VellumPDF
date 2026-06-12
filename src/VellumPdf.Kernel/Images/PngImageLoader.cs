// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Decodes a PNG file and produces a FlateDecode Image XObject.
/// Alpha channel (RGBA or greyscale+alpha) is separated into an /SMask Image XObject.
///
/// Supports colour types: 0 (Greyscale), 2 (RGB), 3 (Indexed→RGB), 4 (Greyscale+Alpha),
/// 6 (RGB+Alpha). Bit depths: 1, 2, 4 (greyscale and indexed; unpacked to 8-bit),
/// 8, and 16 (preserved at 16-bit or downsampled to 8 per ImageLoadOptions).
/// Interlace methods: 0 (None) and 1 (Adam7).
/// </summary>
public static class PngImageLoader
{
    private const ulong PngSignature = 0x89504E470D0A1A0A;

    // Adam7 pass parameters: xStart, yStart, xStep, yStep for passes 0..6
    private static readonly int[] Adam7XStart = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] Adam7YStart = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] Adam7XStep = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] Adam7YStep = [8, 8, 8, 4, 4, 2, 2];

    /// <summary>Decodes PNG file bytes into a FlateDecode Image XObject (alpha becomes an /SMask).</summary>
    public static PdfImageXObject Load(byte[] pngBytes) => Load(pngBytes, ImageLoadOptions.Default);

    /// <summary>Decodes PNG file bytes into a FlateDecode Image XObject with the specified load options.</summary>
    public static PdfImageXObject Load(byte[] pngBytes, ImageLoadOptions options)
    {
        ValidateSignature(pngBytes);

        // ── Parse chunks ──
        int width = 0, height = 0;
        byte bitDepth = 0, colorType = 0, interlaceMethod = 0;
        byte[]? palette = null;
        byte[]? transparencyBytes = null;
        var idatData = new List<byte[]>();

        var pos = 8;
        while (pos < pngBytes.Length - 8)
        {
            var length = ReadU32Be(pngBytes, pos);

            // Validate chunk length before slicing to prevent IndexOutOfRangeException on truncated files.
            if (length > int.MaxValue)
                throw new InvalidDataException($"PNG chunk length {length} exceeds int.MaxValue.");
            if ((long)pos + 12 + (long)length > pngBytes.Length)
                throw new InvalidDataException("PNG file is truncated: chunk extends beyond end of file.");

            var type = ReadTag(pngBytes, pos + 4);
            var data = pngBytes.AsSpan(pos + 8, (int)length);

            switch (type)
            {
                case "IHDR":
                    if (length != 13)
                        throw new InvalidDataException($"PNG IHDR chunk length must be 13; found {length}.");
                    width = (int)ReadU32Be(pngBytes, pos + 8);
                    height = (int)ReadU32Be(pngBytes, pos + 12);
                    bitDepth = data[8];
                    colorType = data[9];
                    interlaceMethod = data[12];
                    break;

                case "PLTE":
                    palette = data.ToArray();
                    break;

                case "tRNS":
                    transparencyBytes = data.ToArray();
                    break;

                case "IDAT":
                    idatData.Add(data.ToArray());
                    break;

                case "IEND":
                    goto done;
            }
            pos += 12 + (int)length;
        }
    done:
        // Reject hostile dimensions and out-of-range IHDR fields before allocating buffers.
        ImageLimits.ValidateDimensions("PNG", width, height);
        if (bitDepth is not (1 or 2 or 4 or 8 or 16))
            throw new InvalidDataException($"PNG has invalid bit depth {bitDepth}.");
        if (colorType is not (0 or 2 or 3 or 4 or 6))
            throw new InvalidDataException($"PNG has invalid colour type {colorType}.");
        if (interlaceMethod is not (0 or 1))
            throw new InvalidDataException($"PNG has invalid interlace method {interlaceMethod}.");

        // Validate PNG-spec bit depth / colour type combinations.
        // colorType 2 (RGB), 4 (Grey+Alpha), 6 (RGBA) require bitDepth 8 or 16.
        // colorType 3 (Indexed) requires bitDepth 1, 2, 4, or 8 (not 16).
        // colorType 0 (Greyscale) allows 1, 2, 4, 8, or 16.
        if (colorType is 2 or 4 or 6 && bitDepth is not (8 or 16))
            throw new InvalidDataException(
                $"PNG colour type {colorType} requires bit depth 8 or 16; found {bitDepth}.");
        if (colorType == 3 && bitDepth == 16)
            throw new InvalidDataException(
                "PNG colour type 3 (Indexed) does not support bit depth 16.");

        // ── Decompress IDAT ──
        var samplesPerPixel = SamplesPerPixel(colorType);
        // Row stride is ceil(width * bitsPerSample / 8) bytes — handles sub-byte packing.
        var bitsPerRow = (long)width * samplesPerPixel * bitDepth;
        var rowBytes = (int)((bitsPerRow + 7) / 8);

        long expectedRaw;
        if (interlaceMethod == 0)
        {
            // For non-interlaced PNG the decompressed size is exactly height*(rowBytes+1) — each
            // scanline carries a 1-byte filter tag. Cap inflation to that to defuse zlib bombs.
            expectedRaw = (long)height * (rowBytes + 1);
        }
        else
        {
            // For Adam7: sum over all 7 passes of (reducedRowBytes+1)*reducedHeight,
            // counting only passes with at least 1 pixel.
            expectedRaw = ComputeAdam7ExpectedRaw(width, height, colorType, bitDepth, samplesPerPixel);
        }

        var compressed = Combine(idatData);
        var raw = Inflate(compressed, expectedRaw);

        // Reject a stream that decompressed to fewer bytes than the dimensions require.
        if (raw.Length < expectedRaw)
            throw new InvalidDataException(
                "PNG image data is truncated: fewer bytes than the declared dimensions require.");

        // ── Unfilter scanlines ──
        byte[] unfiltered;
        if (interlaceMethod == 0)
        {
            unfiltered = Unfilter(raw, rowBytes, width, height, colorType, bitDepth);
        }
        else
        {
            unfiltered = DeinterlaceAdam7(raw, width, height, colorType, bitDepth, samplesPerPixel);
        }

        // ── Extract colour and alpha planes ──
        return BuildXObject(unfiltered, width, height, colorType, bitDepth, palette, options, transparencyBytes);
    }

    /// <summary>
    /// Computes the expected total decompressed bytes for an Adam7-interlaced PNG.
    /// This is the sum over all 7 passes of (reducedRowBytes + 1) * reducedHeight,
    /// counting only passes that contain at least one pixel.
    /// </summary>
    private static long ComputeAdam7ExpectedRaw(
        int width, int height, byte colorType, byte bitDepth, int samplesPerPixel)
    {
        long total = 0;
        for (var pass = 0; pass < 7; pass++)
        {
            var rw = ReducedDimension(width, Adam7XStart[pass], Adam7XStep[pass]);
            var rh = ReducedDimension(height, Adam7YStart[pass], Adam7YStep[pass]);
            if (rw == 0 || rh == 0) continue;
            var passRowBits = (long)rw * samplesPerPixel * bitDepth;
            var passRowBytes = (int)((passRowBits + 7) / 8);
            total += (long)(passRowBytes + 1) * rh;
        }
        return total;
    }

    /// <summary>
    /// Computes the reduced dimension for one Adam7 pass axis.
    /// reducedDim = ceil((fullDim - start) / step), or 0 if start >= fullDim.
    /// </summary>
    private static int ReducedDimension(int fullDim, int start, int step)
    {
        if (start >= fullDim) return 0;
        return (fullDim - start + step - 1) / step;
    }

    /// <summary>
    /// De-interlaces an Adam7-compressed stream into a full-raster byte array in the same
    /// layout as non-interlaced output: packed rows of (width * samplesPerPixel * bitDepth)
    /// bits each, stored in full-width rows without filter bytes.
    ///
    /// Strategy: build a full-raster byte array large enough for packed output, then for each
    /// of the 7 passes, unfilter that pass's sub-image and scatter each sample bit-group into
    /// the correct raster position. For sub-byte bit depths, we work at the sample level by
    /// reading individual samples from the pass buffer and writing them into the full raster
    /// using the same MSB-first bit packing as the non-interlaced path feeds to UnpackSubByte.
    /// </summary>
    private static byte[] DeinterlaceAdam7(
        byte[] raw, int width, int height,
        byte colorType, byte bitDepth, int samplesPerPixel)
    {
        // Full-raster row stride (packed, no filter bytes).
        var bitsPerFullRow = (long)width * samplesPerPixel * bitDepth;
        var fullRowBytes = (int)((bitsPerFullRow + 7) / 8);
        var raster = new byte[height * fullRowBytes];

        var rawOffset = 0;

        for (var pass = 0; pass < 7; pass++)
        {
            var xStart = Adam7XStart[pass];
            var yStart = Adam7YStart[pass];
            var xStep = Adam7XStep[pass];
            var yStep = Adam7YStep[pass];

            var rw = ReducedDimension(width, xStart, xStep);
            var rh = ReducedDimension(height, yStart, yStep);
            if (rw == 0 || rh == 0) continue;

            var passRowBits = (long)rw * samplesPerPixel * bitDepth;
            var passRowBytes = (int)((passRowBits + 7) / 8);

            // Slice the raw bytes for this pass and unfilter it.
            var passRawLen = rh * (passRowBytes + 1);
            var passRaw = raw.AsSpan(rawOffset, passRawLen).ToArray();
            rawOffset += passRawLen;

            var passPixels = Unfilter(passRaw, passRowBytes, rw, rh, colorType, bitDepth);

            // Scatter pass pixels into the full raster.
            if (bitDepth >= 8)
            {
                // Each sample is bytesPerSample bytes. Scatter sample groups (pixels) directly.
                var bytesPerSample = bitDepth / 8;
                var passBytesPerPixel = samplesPerPixel * bytesPerSample;
                for (var row = 0; row < rh; row++)
                {
                    var fullRow = yStart + row * yStep;
                    var passRowStart = row * rw * passBytesPerPixel;
                    for (var col = 0; col < rw; col++)
                    {
                        var fullCol = xStart + col * xStep;
                        var srcBase = passRowStart + col * passBytesPerPixel;
                        var dstBase = fullRow * fullRowBytes + fullCol * passBytesPerPixel;
                        for (var b = 0; b < passBytesPerPixel; b++)
                            raster[dstBase + b] = passPixels[srcBase + b];
                    }
                }
            }
            else
            {
                // Sub-byte bit depths: read individual samples from pass and write into raster
                // at the bit position corresponding to the scattered pixel column.
                var bitMask = (1 << bitDepth) - 1;
                for (var row = 0; row < rh; row++)
                {
                    var fullRow = yStart + row * yStep;
                    var passRowStart = row * passRowBytes;
                    var rasterRowStart = fullRow * fullRowBytes;

                    for (var col = 0; col < rw; col++)
                    {
                        var fullCol = xStart + col * xStep;
                        // Read each sample for this pixel from the pass buffer.
                        for (var s = 0; s < samplesPerPixel; s++)
                        {
                            // Source bit position in the pass row.
                            var srcBitPos = (col * samplesPerPixel + s) * bitDepth;
                            var srcByteIdx = passRowStart + srcBitPos / 8;
                            var srcBitOffset = 8 - bitDepth - (srcBitPos % 8);
                            var sample = (passPixels[srcByteIdx] >> srcBitOffset) & bitMask;

                            // Destination bit position in the full raster row.
                            var dstBitPos = (fullCol * samplesPerPixel + s) * bitDepth;
                            var dstByteIdx = rasterRowStart + dstBitPos / 8;
                            var dstBitOffset = 8 - bitDepth - (dstBitPos % 8);
                            // Clear the target bits, then OR in the sample.
                            raster[dstByteIdx] &= (byte)~(bitMask << dstBitOffset);
                            raster[dstByteIdx] |= (byte)(sample << dstBitOffset);
                        }
                    }
                }
            }
        }

        return raster;
    }

    private static PdfImageXObject BuildXObject(
        byte[] pixels, int w, int h,
        byte colorType, byte bitDepth, byte[]? palette, ImageLoadOptions options,
        byte[]? transparencyBytes = null)
    {
        // ── Sub-byte unpacking (bit depths 1, 2, 4) ──────────────────────────
        // PNG packs multiple samples per byte for bit depths < 8.
        // Unpack them to one byte per sample before further processing.
        if (bitDepth < 8)
        {
            pixels = UnpackSubByte(pixels, w, h, colorType, bitDepth);
            // After unpacking, treat as 8-bit for all downstream logic.
        }

        bool hasAlpha = colorType == 4 || colorType == 6;
        int channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 3 };
        var colorChannels = channels - (hasAlpha ? 1 : 0);

        byte[]? alphaBytes = null;
        byte[] colorBytes;

        if (colorType == 3)
        {
            // Indexed: expand palette (indices are now 1 byte each after unpack)
            if (palette is null)
                throw new InvalidDataException("PNG indexed image (colour type 3) has no PLTE chunk.");
            colorBytes = ExpandPalette(pixels, w, h, palette);

            // tRNS for indexed images: per-palette-index alpha table (ISO 15948 §11.3.2).
            // Build an 8-bit DeviceGray SMask whose samples are the per-pixel alpha
            // looked up via the palette index. Entries beyond tRNS length default to 255.
            PdfStream? indexedSMask = null;
            if (transparencyBytes is { Length: > 0 })
            {
                var alphaPlane = new byte[w * h];
                for (var i = 0; i < w * h; i++)
                {
                    var idx = pixels[i]; // raw palette index (1 byte per pixel after unpack)
                    alphaPlane[i] = idx < transparencyBytes.Length ? transparencyBytes[idx] : (byte)255;
                }
                indexedSMask = new PdfStream(alphaPlane);
            }
            return new PdfImageXObject(w, h, colorBytes, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8,
                indexedSMask, 8);
        }

        if (hasAlpha)
        {
            if (bitDepth == 16)
            {
                // 16-bit: each sample is 2 bytes. Split on 2-byte boundaries.
                var colorSampleBytes = colorChannels * 2;
                var alphaSampleBytes = 2;
                var totalSampleBytes = channels * 2;
                colorBytes = new byte[w * h * colorSampleBytes];
                alphaBytes = new byte[w * h * alphaSampleBytes];
                for (int i = 0, src = 0; i < w * h; i++, src += totalSampleBytes)
                {
                    for (var c = 0; c < colorChannels; c++)
                    {
                        colorBytes[i * colorSampleBytes + c * 2] = pixels[src + c * 2];
                        colorBytes[i * colorSampleBytes + c * 2 + 1] = pixels[src + c * 2 + 1];
                    }
                    alphaBytes[i * 2] = pixels[src + colorChannels * 2];
                    alphaBytes[i * 2 + 1] = pixels[src + colorChannels * 2 + 1];
                }
            }
            else
            {
                // 8-bit (or sub-byte unpacked to 8): each sample is 1 byte.
                colorBytes = new byte[w * h * colorChannels];
                alphaBytes = new byte[w * h];
                for (int i = 0, src = 0; i < w * h; i++, src += channels)
                {
                    for (var c = 0; c < colorChannels; c++)
                        colorBytes[i * colorChannels + c] = pixels[src + c];
                    alphaBytes[i] = pixels[src + colorChannels];
                }
            }
        }
        else
        {
            colorBytes = pixels;
        }

        // Downsample 16-bit to 8-bit only when ReduceToEight is requested.
        int bitsPerComponent;
        int sMaskBitsPerComponent;
        if (bitDepth == 16)
        {
            if (options.BitDepth == ImageBitDepth.ReduceToEight)
            {
                colorBytes = Downsample16(colorBytes);
                if (alphaBytes is not null) alphaBytes = Downsample16(alphaBytes);
                bitsPerComponent = 8;
                sMaskBitsPerComponent = 8;
            }
            else
            {
                // Preserve: keep 16-bit bytes as-is. PNG stores samples big-endian,
                // which matches PDF's expected byte order — no swap needed.
                bitsPerComponent = 16;
                sMaskBitsPerComponent = 16;
            }
        }
        else
        {
            bitsPerComponent = 8;
            sMaskBitsPerComponent = 8;
        }

        var cs = colorChannels == 1 ? ImageColorSpace.DeviceGray : ImageColorSpace.DeviceRgb;

        PdfStream? sMask = null;
        if (alphaBytes is not null)
            sMask = new PdfStream(alphaBytes); // grayscale SMask; will compress via FlateDecode

        // tRNS colour-key mask for greyscale (type 0) and RGB (type 2) images.
        // ISO 32000-2 §8.9.6.3: /Mask is [min0 max0 ...] per colour component.
        // PNG tRNS for these types stores big-endian 16-bit samples (regardless of bitDepth).
        // After any downsampling we use the high byte; at 8-bit we shift by (16 - bitsPerComponent).
        PdfArray? colorKeyMask = null;
        if (!hasAlpha && transparencyBytes is { Length: > 0 } && (colorType == 0 || colorType == 2))
        {
            // tRNS for greyscale: 2 bytes = one 16-bit sample.
            // tRNS for RGB: 6 bytes = three 16-bit samples (R, G, B).
            var numComponents = colorType == 0 ? 1 : 3;
            var expectedTrnsBytes = numComponents * 2;
            if (transparencyBytes.Length >= expectedTrnsBytes)
            {
                var maskEntries = new List<PdfObject>(numComponents * 2);
                for (var c = 0; c < numComponents; c++)
                {
                    // tRNS stores each transparent sample as a big-endian 16-bit word whose
                    // meaningful precision is the source bitDepth. The /Mask value must match
                    // the precision the image samples were actually written at, so apply the
                    // same transform the pixel data went through:
                    //   • 16-bit preserved (BitsPerComponent 16): use the full 16-bit value.
                    //   • 16-bit reduced to 8: use the high byte (>> 8), as the samples were.
                    //   • sub-byte greyscale (1/2/4-bit): linearly scaled to 0-255 like UnpackSubByte.
                    //   • native 8-bit: the low byte is the exact sample.
                    var raw16 = (int)((transparencyBytes[c * 2] << 8) | transparencyBytes[c * 2 + 1]);
                    int maskVal;
                    if (bitsPerComponent == 16)
                        maskVal = raw16;
                    else if (bitDepth == 16)
                        maskVal = raw16 >> 8;
                    else if (bitDepth < 8)
                        maskVal = raw16 * (255 / ((1 << bitDepth) - 1));
                    else
                        maskVal = raw16;
                    maskEntries.Add(new PdfInteger(maskVal));
                    maskEntries.Add(new PdfInteger(maskVal)); // [min max] pair — exact match
                }
                colorKeyMask = new PdfArray([.. maskEntries]);
            }
        }

        return new PdfImageXObject(w, h, colorBytes, PdfName.FlateDecode, cs, bitsPerComponent,
            sMask, sMaskBitsPerComponent, decodeParms: null, jbig2Globals: null, colorKeyMask: colorKeyMask);
    }

    /// <summary>
    /// Unpacks sub-byte PNG samples (bit depths 1, 2, 4) into one byte per sample.
    /// Greyscale samples are linearly scaled to the 0-255 range.
    /// Indexed samples are returned as raw palette indices (0-based, 1 byte each).
    /// </summary>
    private static byte[] UnpackSubByte(byte[] packed, int w, int h, byte colorType, byte bitDepth)
    {
        // Only greyscale (0) and indexed (3) support sub-8 bit depths per the PNG spec.
        int samplesPerPixel = SamplesPerPixel(colorType);
        int totalSamples = w * h * samplesPerPixel;
        var result = new byte[totalSamples];

        int maxVal = (1 << bitDepth) - 1;
        // Scale factor from sub-byte to 8-bit (only meaningful for greyscale).
        // e.g. 1-bit: 0→0, 1→255.  4-bit: 15→255.
        int scale = colorType == 0 ? (255 / maxVal) : 1;

        int bitMask = maxVal;
        int outIdx = 0;

        for (var row = 0; row < h; row++)
        {
            // Each packed row is ceil(w * bitDepth / 8) bytes.
            var bitsInRow = w * bitDepth;
            var packedRowStart = row * ((bitsInRow + 7) / 8);
            int bitPos = 0; // current bit position within the row (0 = MSB of first byte)

            for (var col = 0; col < w; col++)
            {
                // Extract one sample of `bitDepth` bits from the MSB-first packed stream.
                var byteIdx = packedRowStart + bitPos / 8;
                var bitOffset = 8 - bitDepth - (bitPos % 8); // shift within byte
                var sample = (packed[byteIdx] >> bitOffset) & bitMask;
                result[outIdx++] = (byte)(sample * scale);
                bitPos += bitDepth;
            }
        }
        return result;
    }

    /// <summary>
    /// Applies PNG filter reconstruction to a raw filtered byte stream.
    /// <paramref name="raw"/> contains rows of (1 filter byte + rowBytes data bytes).
    /// Returns a flat array of height * rowBytes unfiltered bytes.
    /// </summary>
    private static byte[] Unfilter(
        byte[] raw, int rowBytes, int width, int height,
        byte colorType, byte bitDepth)
    {
        var bpp = Math.Max(1, (int)Math.Ceiling(BytesPerPixel(colorType, bitDepth)));
        var stride = rowBytes;
        var result = new byte[height * stride];
        var prev = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var filterType = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var dst = result.AsSpan(y * stride, stride);
            src.CopyTo(dst);

            switch (filterType)
            {
                case 0: break; // None
                case 1: // Sub
                    for (var x = bpp; x < stride; x++)
                        dst[x] = (byte)(dst[x] + dst[x - bpp]);
                    break;
                case 2: // Up
                    for (var x = 0; x < stride; x++)
                        dst[x] = (byte)(dst[x] + prev[x]);
                    break;
                case 3: // Average
                    for (var x = 0; x < stride; x++)
                    {
                        var a = x >= bpp ? dst[x - bpp] : 0;
                        dst[x] = (byte)(dst[x] + (a + prev[x]) / 2);
                    }
                    break;
                case 4: // Paeth
                    for (var x = 0; x < stride; x++)
                    {
                        var a = x >= bpp ? dst[x - bpp] : 0;
                        var b = prev[x];
                        var c = x >= bpp ? prev[x - bpp] : 0;
                        dst[x] = (byte)(dst[x] + PaethPredictor(a, b, c));
                    }
                    break;
                default:
                    throw new InvalidDataException($"PNG row {y}: unsupported filter type {filterType}.");
            }
            dst.CopyTo(prev.AsSpan());
        }
        return result;
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    private static byte[] ExpandPalette(byte[] pixels, int w, int h, byte[] palette)
    {
        if (palette.Length % 3 != 0 || palette.Length < 3)
            throw new InvalidDataException(
                $"PNG PLTE chunk length {palette.Length} is not a multiple of 3 or is empty.");
        var result = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            var idx = pixels[i] * 3;
            if (idx + 2 >= palette.Length)
                throw new InvalidDataException(
                    $"PNG palette index {pixels[i]} out of range for {palette.Length / 3}-entry PLTE.");
            result[i * 3] = palette[idx];
            result[i * 3 + 1] = palette[idx + 1];
            result[i * 3 + 2] = palette[idx + 2];
        }
        return result;
    }

    private static byte[] Downsample16(byte[] data)
    {
        var result = new byte[data.Length / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = data[i * 2]; // take high byte
        return result;
    }

    private static int SamplesPerPixel(byte colorType) =>
        colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 3 };

    private static double BytesPerPixel(byte colorType, byte bitDepth) =>
        SamplesPerPixel(colorType) * bitDepth / 8.0;

    private static byte[] Inflate(byte[] compressed, long maxOutput)
    {
        // Cap decompression to the size the IHDR dimensions imply (plus slack) so a crafted
        // zlib "bomb" — a few KB inflating to gigabytes — fails cleanly instead of exhausting memory.
        var cap = maxOutput + 16;
        var ms = new MemoryStream();
        using var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > cap)
                throw new InvalidDataException(
                    "PNG image data decompresses to more bytes than its declared dimensions allow.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static byte[] Combine(List<byte[]> chunks)
    {
        var total = chunks.Sum(c => c.Length);
        var buf = new byte[total];
        var pos = 0;
        foreach (var c in chunks) { c.CopyTo(buf, pos); pos += c.Length; }
        return buf;
    }

    private static uint ReadU32Be(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static string ReadTag(byte[] data, int offset) =>
        new string([(char)data[offset], (char)data[offset + 1],
                    (char)data[offset + 2], (char)data[offset + 3]]);

    private static void ValidateSignature(byte[] data)
    {
        if (data.Length < 8) throw new InvalidDataException("Not a PNG file.");
        ulong sig = ((ulong)data[0] << 56) | ((ulong)data[1] << 48) | ((ulong)data[2] << 40) |
                    ((ulong)data[3] << 32) | ((ulong)data[4] << 24) | ((ulong)data[5] << 16) |
                    ((ulong)data[6] << 8) | data[7];
        if (sig != PngSignature) throw new InvalidDataException("Not a PNG file.");
    }
}
