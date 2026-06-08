// Copyright 2026 Timothy van der Ham (@Tim81)
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
/// 8, and 16 (16-bit is downsampled to 8).
/// </summary>
public static class PngImageLoader
{
    private const ulong PngSignature = 0x89504E470D0A1A0A;

    public static PdfImageXObject Load(byte[] pngBytes)
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

        if (interlaceMethod != 0)
            throw new NotSupportedException("Interlaced PNG not supported.");

        // ── Decompress IDAT ──
        var compressed = Combine(idatData);
        var raw = Inflate(compressed);

        // ── Unfilter scanlines ──
        // Row stride is ceil(width * bitsPerSample / 8) bytes — handles sub-byte packing.
        var bitsPerRow = (long)width * SamplesPerPixel(colorType) * bitDepth;
        var rowBytes = (int)((bitsPerRow + 7) / 8);
        var unfiltered = Unfilter(raw, width, height, colorType, bitDepth, rowBytes);

        // ── Extract colour and alpha planes ──
        return BuildXObject(unfiltered, width, height, colorType, bitDepth, palette);
    }

    private static PdfImageXObject BuildXObject(
        byte[] pixels, int w, int h,
        byte colorType, byte bitDepth, byte[]? palette)
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
            colorBytes = ExpandPalette(pixels, w, h, palette!);
            return new PdfImageXObject(w, h, colorBytes, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);
        }

        if (hasAlpha)
        {
            colorBytes = new byte[w * h * colorChannels];
            alphaBytes = new byte[w * h];
            for (int i = 0, src = 0; i < w * h; i++, src += channels)
            {
                for (var c = 0; c < colorChannels; c++)
                    colorBytes[i * colorChannels + c] = pixels[src + c];
                alphaBytes[i] = pixels[src + colorChannels];
            }
        }
        else
        {
            colorBytes = pixels;
        }

        // Downsample 16-bit to 8-bit
        if (bitDepth == 16)
        {
            colorBytes = Downsample16(colorBytes);
            if (alphaBytes is not null) alphaBytes = Downsample16(alphaBytes);
        }

        var cs = colorChannels == 1 ? ImageColorSpace.DeviceGray : ImageColorSpace.DeviceRgb;

        PdfStream? sMask = null;
        if (alphaBytes is not null)
            sMask = new PdfStream(alphaBytes); // grayscale SMask; will compress via FlateDecode

        return new PdfImageXObject(w, h, colorBytes, PdfName.FlateDecode, cs, 8, sMask);
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

    private static byte[] Unfilter(
        byte[] raw, int width, int height,
        byte colorType, byte bitDepth, int rowBytes)
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
        var result = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            var idx = pixels[i] * 3;
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

    private static byte[] Inflate(byte[] compressed)
    {
        var ms = new MemoryStream();
        using var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        z.CopyTo(ms);
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
        new string([(char)data[offset], (char)data[offset+1],
                    (char)data[offset+2], (char)data[offset+3]]);

    private static void ValidateSignature(byte[] data)
    {
        if (data.Length < 8) throw new InvalidDataException("Not a PNG file.");
        ulong sig = ((ulong)data[0] << 56) | ((ulong)data[1] << 48) | ((ulong)data[2] << 40) |
                    ((ulong)data[3] << 32) | ((ulong)data[4] << 24) | ((ulong)data[5] << 16) |
                    ((ulong)data[6] << 8) | data[7];
        if (sig != PngSignature) throw new InvalidDataException("Not a PNG file.");
    }
}
