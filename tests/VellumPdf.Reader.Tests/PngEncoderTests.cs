// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using VellumPdf.Images;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <see cref="PngEncoder"/> (#98): the low-level chunk writer, CRC32, and the mapping from a
/// <see cref="PdfExtractedImage"/>'s colour space onto a PNG colour type and bit depth.
/// </summary>
public sealed class PngEncoderTests
{
    // An independent bit-by-bit CRC32 (ISO 3309), deliberately not sharing PngEncoder's own
    // table-driven implementation: a shared bug in both would otherwise go unnoticed.
    private static uint Crc32Bitwise(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    [Fact]
    public void Crc32_knownAnswer_ascii123456789()
    {
        Assert.Equal(0xCBF43926u, Crc32Bitwise("123456789"u8));
    }

    [Fact]
    public void Crc32_knownAnswer_empty()
    {
        Assert.Equal(0x00000000u, Crc32Bitwise([]));
    }

    /// <summary>
    /// Pins the exact IHDR and IEND chunk CRCs for a 1x1 grey 8-bit image, computed here from the
    /// polynomial rather than pasted from elsewhere, and checked against the bytes
    /// <see cref="PngEncoder.Encode"/> writes.
    /// </summary>
    [Fact]
    public void Encode_1x1Grey8_ihdrAndIendCrcsMatch()
    {
        var png = PngEncoder.Encode(
            width: 1, height: 1, bitDepth: 8, type: PngColorType.Grayscale,
            palette: [], rows: [0x7F], rowBytes: 1);

        // Signature (8) + IHDR length (4) + "IHDR" (4) + 13-byte payload, then the 4-byte CRC.
        var ihdrTypeAndData = png.AsSpan(8 + 4, 4 + 13);
        var ihdrCrc = BitConverterBigEndian(png.AsSpan(8 + 4 + 4 + 13, 4));
        var computedIhdrCrc = Crc32Bitwise(ihdrTypeAndData);

        Assert.Equal(0x3A7E9B55u, computedIhdrCrc);
        Assert.Equal(computedIhdrCrc, ihdrCrc);

        // IEND is the last 12 bytes of the file: length(4)=0, "IEND"(4), crc(4).
        var iendTypeAndData = png.AsSpan(png.Length - 8, 4); // "IEND", no data.
        var iendCrc = BitConverterBigEndian(png.AsSpan(png.Length - 4, 4));
        var computedIendCrc = Crc32Bitwise(iendTypeAndData);

        Assert.Equal(0xAE426082u, computedIendCrc);
        Assert.Equal(computedIendCrc, iendCrc);

        // The 13-byte IHDR payload itself: width, height, bit depth 8, colour type 0 (grey),
        // compression/filter/interlace all 0.
        var expectedIhdrPayload = new byte[]
        {
            0, 0, 0, 1, 0, 0, 0, 1, 8, 0, 0, 0, 0,
        };
        Assert.Equal(expectedIhdrPayload, png.AsSpan(8 + 8, 13).ToArray());
    }

    private static uint BitConverterBigEndian(ReadOnlySpan<byte> b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    // ── Mapping tests (PngEncoder.CanEncode/TryEncode/TryEncodeWithAlpha) ───────────────────────

    private static PdfExtractedImage MakeImage(
        int width, int height, int bitsPerComponent, PdfImageColorSpace? colorSpace, byte[] data,
        bool isStencilMask = false, PdfExtractedImage? softMask = null) =>
        new(
            pageIndex: 0, objectNumber: 1, generation: 0, isInline: false, isStencilMask: isStencilMask,
            isExplicitMask: false, hasMatte: false, width: width, height: height,
            bitsPerComponent: bitsPerComponent, sMaskInData: 0, colorSpace: colorSpace, decode: null,
            encoding: PdfImageEncoding.Raw, data: data, fileExtension: ".bin", softMask: softMask,
            explicitMask: null, jbig2: null, ccittFax: null, dct: null, interpolate: false,
            isSoftMask: false);

    private static PdfImageColorSpace DeviceGray => new(PdfImageColorSpaceFamily.DeviceGray, 1);
    private static PdfImageColorSpace DeviceRgb => new(PdfImageColorSpaceFamily.DeviceRgb, 3);

    [Fact]
    public void CanEncode_passthroughEncoding_isFalse()
    {
        var image = new PdfExtractedImage(
            pageIndex: 0, objectNumber: 1, generation: 0, isInline: false, isStencilMask: false,
            isExplicitMask: false, hasMatte: false, width: 1, height: 1, bitsPerComponent: 8,
            sMaskInData: 0, colorSpace: DeviceRgb, decode: null, encoding: PdfImageEncoding.Jpeg,
            data: new byte[] { 1, 2, 3 }, fileExtension: ".jpg", softMask: null, explicitMask: null, jbig2: null,
            ccittFax: null, dct: new PdfDctParameters(null), interpolate: false, isSoftMask: false);

        Assert.False(image.CanEncodePng);
        Assert.False(image.TryEncodePng(out var png));
        Assert.Null(png);
    }

    [Fact]
    public void CanEncode_shortBuffer_isFalse()
    {
        // 2x2 grey 8-bit needs 4 bytes; only 2 are supplied.
        var image = MakeImage(2, 2, 8, DeviceGray, [1, 2]);
        Assert.False(image.CanEncodePng);
    }

    [Fact]
    public void TryEncode_grey8_producesGrayscaleColorType()
    {
        var image = MakeImage(1, 1, 8, DeviceGray, [0x42]);
        Assert.True(image.TryEncodePng(out var png));
        Assert.Equal((byte)PngColorType.Grayscale, png![8 + 8 + 9]);
        Assert.Equal(8, png[8 + 8 + 8]); // bit depth
    }

    [Fact]
    public void TryEncode_rgb8_producesRgbColorType()
    {
        var image = MakeImage(1, 1, 8, DeviceRgb, [1, 2, 3]);
        Assert.True(image.TryEncodePng(out var png));
        Assert.Equal((byte)PngColorType.Rgb, png![8 + 8 + 9]);
    }

    [Fact]
    public void TryEncode_rgb1Bit_isRejected_pngHasNoType2BelowEightBits()
    {
        var image = MakeImage(8, 1, 1, DeviceRgb, [0xFF, 0xFF, 0xFF]);
        Assert.False(image.CanEncodePng);
    }

    [Fact]
    public void StencilMask_encodesAsGrayscaleDepth1()
    {
        var image = MakeImage(8, 1, 1, colorSpace: null, data: [0b10101010], isStencilMask: true);
        Assert.True(image.TryEncodePng(out var png));
        Assert.Equal((byte)PngColorType.Grayscale, png![8 + 8 + 9]);
        Assert.Equal(1, png[8 + 8 + 8]);
    }

    /// <summary>
    /// Indexed over DeviceRGB, 4-entry lookup, depth 8: the PLTE holds exactly the lookup's own
    /// RGB triples, unchanged (the derivation is lossless, no scaling).
    /// </summary>
    [Fact]
    public void TryEncode_indexedOverRgb_pltePreservesLookupTriples()
    {
        byte[] lookup = [10, 20, 30, 40, 50, 60, 70, 80, 90];
        var indexed = new PdfImageColorSpace(
            PdfImageColorSpaceFamily.Indexed, 1, @base: DeviceRgb, highValue: 2, lookup: lookup);
        var image = MakeImage(1, 1, 8, indexed, [0]);

        Assert.True(image.TryEncodePng(out var png));
        var plte = ExtractChunk(png!, "PLTE"u8);
        // hival 2 -> 3 entries, min(2^8, 256) = 256 total slots, entries 3..255 filled with entry
        // 2.
        Assert.Equal(256 * 3, plte.Length);
        Assert.Equal(lookup[0..9], plte[0..9]);
        // Index 255 (past hival) repeats entry 2 (the last defined entry, bytes 70,80,90).
        Assert.Equal(new byte[] { 70, 80, 90 }, plte[(255 * 3)..(255 * 3 + 3)]);
    }

    /// <summary>
    /// The depth-clamped case: a 256-entry-capable DeviceRGB lookup used at
    /// <c>/BitsPerComponent</c> 2 must produce a 4-entry PLTE (<c>min(2^2, 256)</c>), not 256.
    /// PNG has no palette larger than the bit depth can index, even though PDF's own <c>hival</c>
    /// bound (255) would otherwise allow it.
    /// </summary>
    [Fact]
    public void TryEncode_indexedDepthClamped_plteHasFourEntries()
    {
        var lookup = new byte[768]; // 256 * 3, hival 255, but only the first four entries matter here.
        for (var i = 0; i < 4; i++)
        {
            lookup[i * 3] = (byte)(i * 10);
            lookup[i * 3 + 1] = (byte)(i * 10 + 1);
            lookup[i * 3 + 2] = (byte)(i * 10 + 2);
        }
        var indexed = new PdfImageColorSpace(
            PdfImageColorSpaceFamily.Indexed, 1, @base: DeviceRgb, highValue: 255, lookup: lookup);
        // 4 samples at 2 bits each, one byte: 00 01 10 11 (0,1,2,3).
        var image = MakeImage(4, 1, 2, indexed, [0b00_01_10_11]);

        Assert.True(image.TryEncodePng(out var png));
        var plte = ExtractChunk(png!, "PLTE"u8);
        Assert.Equal(4 * 3, plte.Length);
        Assert.Equal(new byte[] { 0, 1, 2, 10, 11, 12, 20, 21, 22, 30, 31, 32 }, plte);
    }

    [Fact]
    public void TryEncodeWithAlpha_grey8WithMatchingSoftMask_producesGrayscaleAlpha()
    {
        var mask = MakeImage(1, 1, 8, DeviceGray, [0xFF]);
        var image = MakeImage(1, 1, 8, DeviceGray, [0x11], softMask: mask);

        Assert.True(image.TryEncodePngWithAlpha(out var png));
        Assert.Equal((byte)PngColorType.GrayscaleAlpha, png![8 + 8 + 9]);
    }

    [Fact]
    public void TryEncodeWithAlpha_noSoftMask_isFalse()
    {
        var image = MakeImage(1, 1, 8, DeviceGray, [0x11]);
        Assert.False(image.TryEncodePngWithAlpha(out var png));
        Assert.Null(png);
    }

    /// <summary>
    /// ISO/IEC 15948's IHDR colour-type/bit-depth table permits colour type 4 (GrayscaleAlpha)
    /// only at 8 or 16 bits: an 8x1 DeviceGray image with a same-depth, same-size DeviceGray
    /// <c>/SMask</c> at 1, 2, or 4 bits must be refused, not written as an empty, sample-less
    /// type-4 PNG.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void TryEncodeWithAlpha_greySubEightBit_withMatchingSoftMask_isFalse(int bpc)
    {
        var rowBytes = (8 * bpc + 7) / 8;
        var mask = MakeImage(8, 1, bpc, DeviceGray, new byte[rowBytes]);
        var image = MakeImage(8, 1, bpc, DeviceGray, new byte[rowBytes], softMask: mask);

        Assert.False(image.TryEncodePngWithAlpha(out var png));
        Assert.Null(png);
    }

    /// <summary>
    /// The positive control for the Theory above: the same shape at 8 bits must still succeed, as
    /// colour type 4 at depth 8 is exactly what ISO/IEC 15948's IHDR table permits. Parses IHDR and
    /// the IDAT payload directly rather than trusting only the boolean result.
    /// </summary>
    [Fact]
    public void TryEncodeWithAlpha_grey8_ihdrColorType4Depth8_idatHoldsColourAndAlphaInterleaved()
    {
        var mask = MakeImage(8, 1, 8, DeviceGray, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        var image = MakeImage(8, 1, 8, DeviceGray, [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80], softMask: mask);

        Assert.True(image.TryEncodePngWithAlpha(out var png));
        Assert.Equal((byte)PngColorType.GrayscaleAlpha, png![8 + 8 + 9]);
        Assert.Equal(8, png[8 + 8 + 8]);

        var idat = InflateChunk(png, "IDAT"u8);
        // 8 pixels, one filter byte (None, 0) plus 2 bytes per pixel (grey, alpha) = 17 bytes.
        Assert.Equal(17, idat.Length);
        Assert.Equal(0, idat[0]);
        for (var px = 0; px < 8; px++)
        {
            Assert.Equal((byte)(0x10 * (px + 1)), idat[1 + px * 2]);
            Assert.Equal((byte)(px + 1), idat[1 + px * 2 + 1]);
        }
    }

    /// <summary>
    /// Table 143 permits a soft mask's own <c>/Decode</c>, default <c>[0 1]</c>; a non-default
    /// array would invert the interleaved alpha channel with no diagnostic, so it is refused
    /// instead (mirroring the <c>HasMatte</c> gate's own "the bytes do not mean what PNG alpha
    /// means" reasoning).
    /// </summary>
    [Fact]
    public void TryEncodeWithAlpha_softMaskDecodeInverted_isFalse()
    {
        var mask = new PdfExtractedImage(
            pageIndex: 0, objectNumber: 2, generation: 0, isInline: false, isStencilMask: false,
            isExplicitMask: false, hasMatte: false, width: 1, height: 1, bitsPerComponent: 8,
            sMaskInData: 0, colorSpace: DeviceGray, decode: new List<double> { 1, 0 },
            encoding: PdfImageEncoding.Raw, data: new byte[] { 0xFF }, fileExtension: ".bin",
            softMask: null, explicitMask: null, jbig2: null, ccittFax: null, dct: null,
            interpolate: false, isSoftMask: true);
        var image = MakeImage(1, 1, 8, DeviceGray, [0x11], softMask: mask);

        Assert.False(image.TryEncodePngWithAlpha(out var png));
        Assert.Null(png);
    }

    /// <summary>
    /// The positive control for the Decode gate above: an explicit default <c>[0 1]</c> is not a
    /// non-default array and must still succeed.
    /// </summary>
    [Fact]
    public void TryEncodeWithAlpha_softMaskDecodeDefault_stillSucceeds()
    {
        var mask = new PdfExtractedImage(
            pageIndex: 0, objectNumber: 2, generation: 0, isInline: false, isStencilMask: false,
            isExplicitMask: false, hasMatte: false, width: 1, height: 1, bitsPerComponent: 8,
            sMaskInData: 0, colorSpace: DeviceGray, decode: new List<double> { 0, 1 },
            encoding: PdfImageEncoding.Raw, data: new byte[] { 0xFF }, fileExtension: ".bin",
            softMask: null, explicitMask: null, jbig2: null, ccittFax: null, dct: null,
            interpolate: false, isSoftMask: true);
        var image = MakeImage(1, 1, 8, DeviceGray, [0x11], softMask: mask);

        Assert.True(image.TryEncodePngWithAlpha(out var png));
        Assert.NotNull(png);
    }

    // ── Round-trip through the Kernel PNG loader (type 0 and type 2 only: those are the shapes
    // the loader leaves bit-exact) ─────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_grey16_bitExactThroughPngImageLoader()
    {
        // 2x1 grey 16-bit: samples 0x0102 and 0x0304.
        byte[] samples = [0x01, 0x02, 0x03, 0x04];
        var image = MakeImage(2, 1, 16, DeviceGray, samples);

        Assert.True(image.TryEncodePng(out var png));
        var xobj = PngImageLoader.Load(png!, ImageLoadOptions.Default);
        var decoded = FlateDecompress(ExtractStreamBody(WriteStream(xobj)));

        Assert.Equal(samples, decoded);
    }

    [Fact]
    public void RoundTrip_rgb8_bitExactThroughPngImageLoader()
    {
        byte[] samples = [1, 2, 3, 4, 5, 6]; // 2x1 RGB
        var image = MakeImage(2, 1, 8, DeviceRgb, samples);

        Assert.True(image.TryEncodePng(out var png));
        var xobj = PngImageLoader.Load(png!, ImageLoadOptions.Default);
        var decoded = FlateDecompress(ExtractStreamBody(WriteStream(xobj)));

        Assert.Equal(samples, decoded);
    }

    private static byte[] InflateChunk(byte[] png, ReadOnlySpan<byte> type) =>
        FlateDecompress(ExtractChunk(png, type));

    private static byte[] ExtractChunk(byte[] png, ReadOnlySpan<byte> type)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)BitConverterBigEndian(png.AsSpan(offset, 4));
            var chunkType = png.AsSpan(offset + 4, 4);
            if (chunkType.SequenceEqual(type))
                return png.AsSpan(offset + 8, length).ToArray();
            offset += 4 + 4 + length + 4;
        }
        throw new InvalidOperationException("chunk not found");
    }

    private static byte[] WriteStream(PdfImageXObject img)
    {
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
        img.BuildStream().WriteTo(writer);
        return ms.ToArray();
    }

    private static byte[] ExtractStreamBody(byte[] raw)
    {
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        if (markerStart < 0) throw new InvalidOperationException("stream marker not found");
        var bodyStart = markerStart + 8;
        var endStream = FindSequence(raw, "\nendstream"u8);
        if (endStream < 0) throw new InvalidOperationException("endstream marker not found");
        return raw[bodyStart..endStream];
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

    private static byte[] FlateDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }
}
