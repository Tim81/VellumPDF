// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for TiffImageLoader — all TIFF files are synthesised in-memory.
///
/// Covers:
///   • Little-endian (II) and big-endian (MM) byte orders.
///   • Compression: 1 (None), 5 (LZW), 7 (new-style JPEG), and 32773 (PackBits).
///   • Photometric: 0 (WhiteIsZero), 1 (BlackIsZero), 2 (RGB), 3 (Palette).
///   • SamplesPerPixel: 1 (grey), 3 (RGB), 4 (RGBA → /SMask).
///   • PlanarConfiguration: 1 (chunky) and 2 (planar).
///   • BitsPerSample: 8 and 16.
///   • 16-bit: Preserve (BitsPerComponent=16) and ReduceToEight (BitsPerComponent=8),
///     both II and MM byte orders; 16-bit RGBA → 16-bit SMask.
///   • LZW + Predictor=2 round-trip.
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

    // ── PlanarConfiguration 2 ────────────────────────────────────────────────

    [Fact]
    public void Tiff_Planar2_Rgb_MatchesChunky()
    {
        // Build a 2×2 RGB image as both chunky (PlanarConfig=1) and planar (PlanarConfig=2).
        // The decoded pixel bytes must be identical.
        var pixels = new byte[]
        {
            0x10, 0x20, 0x30, // pixel (0,0): R,G,B
            0x40, 0x50, 0x60, // pixel (0,1)
            0x70, 0x80, 0x90, // pixel (1,0)
            0xA0, 0xB0, 0xC0, // pixel (1,1)
        };
        var chunky = BuildTiff(2, 2, 8, 1, photometric: 2, samplesPerPixel: 3,
            pixels, littleEndian: true, colorMap: null, alphaIncluded: false);
        var planar = BuildPlanarTiff(2, 2, 8, 1, pixels);

        var chunkyPixels = DecompressStream(TiffImageLoader.Load(chunky).BuildStream());
        var planarPixels = DecompressStream(TiffImageLoader.Load(planar).BuildStream());

        Assert.Equal(chunkyPixels, planarPixels);
    }

    [Fact]
    public void Tiff_Planar2_Grey_PixelRoundTrip()
    {
        // 1-sample (grey) image: PlanarConfig=2 is trivially the same as chunky.
        // Verify it decodes without error.
        var pixels = new byte[] { 0x10, 0x20, 0x30, 0x40 }; // 2×2 grey
        var planar = BuildPlanarTiff(2, 2, 8, 1, pixels, samplesPerPixel: 1, photometric: 1);
        var img = TiffImageLoader.Load(planar);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, decoded);
    }

    // ── 16-bit samples ───────────────────────────────────────────────────────

    [Fact]
    public void Tiff_16Bit_Greyscale_LittleEndian_Preserve()
    {
        // 2×1 grey 16-bit LE: sample values 0x1234 and 0xABCD stored LE.
        // LE on disk: [0x34,0x12, 0xCD,0xAB]. After endian swap → BE: [0x12,0x34, 0xAB,0xCD].
        var pixelData = new byte[] { 0x34, 0x12, 0xCD, 0xAB }; // LE: 0x1234, 0xABCD
        var tiff = BuildTiff(2, 1, 16, 1, photometric: 1, samplesPerPixel: 1,
            pixelData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff, ImageLoadOptions.Default);

        Assert.Equal(16, ReadBitsPerComponent(img.BuildStream()));
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(4, decoded.Length); // 2 pixels × 2 bytes
        Assert.Equal(0x12, decoded[0]); // high byte after swap
        Assert.Equal(0x34, decoded[1]); // low byte after swap
        Assert.Equal(0xAB, decoded[2]);
        Assert.Equal(0xCD, decoded[3]);
    }

    [Fact]
    public void Tiff_16Bit_Greyscale_BigEndian_Preserve()
    {
        // 2×1 grey 16-bit BE: samples stored as 0x1234 and 0xABCD big-endian (no swap needed).
        var pixelData = new byte[] { 0x12, 0x34, 0xAB, 0xCD }; // BE: 0x1234, 0xABCD
        var tiff = BuildTiff(2, 1, 16, 1, photometric: 1, samplesPerPixel: 1,
            pixelData, littleEndian: false, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff, ImageLoadOptions.Default);

        Assert.Equal(16, ReadBitsPerComponent(img.BuildStream()));
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(4, decoded.Length);
        Assert.Equal(0x12, decoded[0]);
        Assert.Equal(0x34, decoded[1]);
        Assert.Equal(0xAB, decoded[2]);
        Assert.Equal(0xCD, decoded[3]);
    }

    [Fact]
    public void Tiff_16Bit_Greyscale_ReduceToEight()
    {
        // 2×1 grey 16-bit LE (0x1234, 0xABCD). ReduceToEight → high bytes only: [0x12, 0xAB].
        var pixelData = new byte[] { 0x34, 0x12, 0xCD, 0xAB }; // LE
        var tiff = BuildTiff(2, 1, 16, 1, photometric: 1, samplesPerPixel: 1,
            pixelData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff, new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight });

        Assert.Equal(8, ReadBitsPerComponent(img.BuildStream()));
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(2, decoded.Length); // 2 pixels × 1 byte
        Assert.Equal(0x12, decoded[0]);
        Assert.Equal(0xAB, decoded[1]);
    }

    [Fact]
    public void Tiff_16Bit_Rgb_LittleEndian_Preserve()
    {
        // 1×1 RGB 16-bit LE: R=0x1122, G=0x3344, B=0x5566.
        // Stored LE: [0x22,0x11, 0x44,0x33, 0x66,0x55]. After swap → BE: [0x11,0x22, 0x33,0x44, 0x55,0x66].
        var pixelData = new byte[] { 0x22, 0x11, 0x44, 0x33, 0x66, 0x55 };
        var tiff = BuildTiff(1, 1, 16, 1, photometric: 2, samplesPerPixel: 3,
            pixelData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff, ImageLoadOptions.Default);

        Assert.Equal(16, ReadBitsPerComponent(img.BuildStream()));
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(6, decoded.Length); // 1 pixel × 3 channels × 2 bytes
        Assert.Equal(0x11, decoded[0]); Assert.Equal(0x22, decoded[1]); // R
        Assert.Equal(0x33, decoded[2]); Assert.Equal(0x44, decoded[3]); // G
        Assert.Equal(0x55, decoded[4]); Assert.Equal(0x66, decoded[5]); // B
    }

    [Fact]
    public void Tiff_16Bit_Rgba_Preserve_HasSmask()
    {
        // 1×1 RGBA 16-bit LE: R=0x1122, G=0x3344, B=0x5566, A=0x7788 (non-opaque).
        // Stored LE: [0x22,0x11, 0x44,0x33, 0x66,0x55, 0x88,0x77].
        var pixelData = new byte[] { 0x22, 0x11, 0x44, 0x33, 0x66, 0x55, 0x88, 0x77 };
        var tiff = BuildTiff(1, 1, 16, 1, photometric: 2, samplesPerPixel: 4,
            pixelData, littleEndian: true, colorMap: null, alphaIncluded: true);
        var img = TiffImageLoader.Load(tiff, ImageLoadOptions.Default);

        Assert.NotNull(img.SMask);
        Assert.Equal(16, img.SMaskBitsPerComponent);

        var colorBytes = DecompressStream(img.BuildStream());
        Assert.Equal(6, colorBytes.Length);
        Assert.Equal(0x11, colorBytes[0]); Assert.Equal(0x22, colorBytes[1]); // R
        Assert.Equal(0x33, colorBytes[2]); Assert.Equal(0x44, colorBytes[3]); // G
        Assert.Equal(0x55, colorBytes[4]); Assert.Equal(0x66, colorBytes[5]); // B

        var alphaBytes = DecompressStream(img.SMask!);
        Assert.Equal(2, alphaBytes.Length);
        Assert.Equal(0x77, alphaBytes[0]); // A high byte
        Assert.Equal(0x88, alphaBytes[1]); // A low byte
    }

    [Fact]
    public void Tiff_16Bit_WhiteIsZero_Inversion()
    {
        // 1×1 16-bit WhiteIsZero LE: sample=0x0000. Inverted: 0xFFFF-0x0000 = 0xFFFF.
        var pixelData = new byte[] { 0x00, 0x00 };
        var tiff = BuildTiff(1, 1, 16, 1, photometric: 0, samplesPerPixel: 1,
            pixelData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff, ImageLoadOptions.Default);

        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(2, decoded.Length);
        Assert.Equal(0xFF, decoded[0]);
        Assert.Equal(0xFF, decoded[1]);
    }

    // ── LZW compression (Compression=5) ──────────────────────────────────────

    [Fact]
    public void Tiff_Lzw_Greyscale_RoundTrip()
    {
        // 4×1 greyscale image, LZW compressed.
        var rawPixels = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var lzwData = TiffLzwEncode(rawPixels);
        var tiff = BuildTiff(4, 1, 8, 5, photometric: 1, samplesPerPixel: 1,
            lzwData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(rawPixels, decoded);
    }

    [Fact]
    public void Tiff_Lzw_Rgb_RoundTrip()
    {
        // 2×1 RGB image, LZW compressed.
        var rawPixels = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        var lzwData = TiffLzwEncode(rawPixels);
        var tiff = BuildTiff(2, 1, 8, 5, photometric: 2, samplesPerPixel: 3,
            lzwData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(rawPixels, decoded);
    }

    [Fact]
    public void Tiff_Lzw_Predictor2_RoundTrip()
    {
        // 3×1 RGB image with LZW + horizontal differencing (Predictor=2).
        // Raw pixels: (10,20,30), (15,25,35), (20,30,40).
        // After predictor encoding (delta from left):
        //   pixel 0: (10,20,30) as-is; pixel 1: (5,5,5); pixel 2: (5,5,5).
        var rawPixels = new byte[] { 10, 20, 30, 15, 25, 35, 20, 30, 40 };
        // Predictor-encode: subtract left sample
        var predictorEncoded = new byte[rawPixels.Length];
        predictorEncoded[0] = rawPixels[0];
        predictorEncoded[1] = rawPixels[1];
        predictorEncoded[2] = rawPixels[2];
        for (var i = 3; i < rawPixels.Length; i++)
            predictorEncoded[i] = (byte)(rawPixels[i] - rawPixels[i - 3]);

        var lzwData = TiffLzwEncode(predictorEncoded);
        var tiff = BuildTiffWithPredictor(3, 1, 8, 5, photometric: 2, samplesPerPixel: 3,
            lzwData, littleEndian: true, predictor: 2);
        var img = TiffImageLoader.Load(tiff);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(rawPixels, decoded);
    }

    [Fact]
    public void Tiff_Lzw_LargerImage_RoundTrip()
    {
        // 8×8 greyscale, LZW: exercises multi-entry table growth.
        var rawPixels = new byte[64];
        for (var i = 0; i < rawPixels.Length; i++)
            rawPixels[i] = (byte)(i * 3 % 256);
        var lzwData = TiffLzwEncode(rawPixels);
        var tiff = BuildTiff(8, 8, 8, 5, photometric: 1, samplesPerPixel: 1,
            lzwData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(rawPixels, decoded);
    }

    [Fact]
    public void Tiff_Lzw_RepeatedByte_KwKwK_RoundTrip()
    {
        // 512 copies of a single byte: the most canonical KwKwK trigger.
        // After ClearCode the encoder emits 0x42, then sees 0x42+0x42 → adds entry 258=0x42+0x42
        // and so on.  The decoder will encounter code == nextCode (KwKwK) once the
        // repeated-string code is reused before its table entry is fully registered.
        var rawPixels = new byte[512];
        Array.Fill(rawPixels, (byte)0x42);
        var lzwData = TiffLzwEncode(rawPixels);
        var tiff = BuildTiff(512, 1, 8, 5, photometric: 1, samplesPerPixel: 1,
            lzwData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(rawPixels, decoded);
    }

    [Fact]
    public void Tiff_Lzw_RepeatingPattern_KwKwK_RoundTrip()
    {
        // {1,2,3} repeated 128 times (= 384 bytes): forces multi-byte table entries
        // and the back-reference / KwKwK code path.
        var pattern = new byte[] { 1, 2, 3 };
        var rawPixels = new byte[384];
        for (var i = 0; i < rawPixels.Length; i++)
            rawPixels[i] = pattern[i % pattern.Length];
        var lzwData = TiffLzwEncode(rawPixels);
        var tiff = BuildTiff(384, 1, 8, 5, photometric: 1, samplesPerPixel: 1,
            lzwData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(rawPixels, decoded);
    }

    [Fact]
    public void Tiff_Lzw_CodeWidth9To10_RoundTrip()
    {
        // 16×16 = 256 bytes of varied grey values; the LZW table will accumulate enough
        // novel sequences to push nextCode past 511, crossing the 9→10 bit boundary.
        var rawPixels = new byte[256];
        for (var i = 0; i < rawPixels.Length; i++)
            rawPixels[i] = (byte)(i & 0xFF);
        var lzwData = TiffLzwEncode(rawPixels);
        var tiff = BuildTiff(16, 16, 8, 5, photometric: 1, samplesPerPixel: 1,
            lzwData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(rawPixels, decoded);
    }

    [Fact]
    public void Tiff_Lzw_CodeWidth10To11_TableFull_RoundTrip()
    {
        // 64×64 = 4096 bytes of varied data, driving nextCode well past 1023 (10→11 boundary)
        // and pushing the table to its 4096-entry limit, which forces a mid-stream ClearCode reset.
        // The image uses an 8×8 tile pattern to mix repetition with novelty.
        var rawPixels = new byte[4096];
        for (var i = 0; i < rawPixels.Length; i++)
            rawPixels[i] = (byte)((i * 7 + (i / 64) * 13) & 0xFF);
        var lzwData = TiffLzwEncode(rawPixels);
        var tiff = BuildTiff(64, 64, 8, 5, photometric: 1, samplesPerPixel: 1,
            lzwData, littleEndian: true, colorMap: null, alphaIncluded: false);
        var img = TiffImageLoader.Load(tiff);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(rawPixels, decoded);
    }

    [Fact]
    public void Tiff_Lzw_Predictor2_LargerImage_RoundTrip()
    {
        // 8×8 RGB image with LZW + Predictor=2; multiple rows exercise the per-row
        // horizontal differencing combined with real LZW back-references.
        const int w = 8;
        const int h = 8;
        const int spp = 3;
        var rawPixels = new byte[w * h * spp];
        for (var i = 0; i < rawPixels.Length; i++)
            rawPixels[i] = (byte)((i * 5 + (i / (w * spp)) * 17) & 0xFF);

        // Apply horizontal predictor per row (delta per channel from left pixel)
        var predicted = new byte[rawPixels.Length];
        for (var row = 0; row < h; row++)
        {
            var rowStart = row * w * spp;
            for (var c = 0; c < spp; c++)
                predicted[rowStart + c] = rawPixels[rowStart + c];
            for (var col = 1; col < w; col++)
            {
                for (var c = 0; c < spp; c++)
                {
                    var idx = rowStart + col * spp + c;
                    predicted[idx] = (byte)(rawPixels[idx] - rawPixels[idx - spp]);
                }
            }
        }

        var lzwData = TiffLzwEncode(predicted);
        var tiff = BuildTiffWithPredictor(w, h, 8, 5, photometric: 2, samplesPerPixel: spp,
            lzwData, littleEndian: true, predictor: 2);
        var img = TiffImageLoader.Load(tiff);
        var decoded = DecompressStream(img.BuildStream());
        Assert.Equal(rawPixels, decoded);
    }

    // ── New-style JPEG (Compression=7) ───────────────────────────────────────

    [Fact]
    public void Tiff_Jpeg7_SingleStrip_DctDecodePassthrough()
    {
        // Embed a minimal baseline JPEG as a single TIFF strip.
        // The result must be a DCTDecode passthrough with the JPEG bytes verbatim.
        var jpeg = BuildMinimalJpeg(2, 1);
        var tiff = BuildTiffWithJpeg7(jpeg);
        var img = TiffImageLoader.Load(tiff);

        // Must be DCTDecode (passthrough), not FlateDecode.
        var dictText = StreamDictText(img);
        Assert.Contains("DCTDecode", dictText);
        Assert.DoesNotContain("FlateDecode", dictText);
    }

    [Fact]
    public void Tiff_Jpeg7_SingleStrip_BytesVerbatim()
    {
        // The JPEG bytes in the TIFF stream must be written verbatim (passthrough).
        var jpeg = BuildMinimalJpeg(2, 1);
        var tiff = BuildTiffWithJpeg7(jpeg);
        var img = TiffImageLoader.Load(tiff);

        // Read stream bytes and locate the embedded JPEG (SOI = FF D8).
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
        img.BuildStream().WriteTo(writer);
        var raw = ms.ToArray();

        // Find FF D8 (JPEG SOI) in the output
        var soiPos = -1;
        for (var i = 0; i < raw.Length - 1; i++)
        {
            if (raw[i] == 0xFF && raw[i + 1] == 0xD8) { soiPos = i; break; }
        }
        Assert.True(soiPos >= 0, "JPEG SOI marker FF D8 not found in TIFF stream output.");

        // Verify the JPEG bytes match verbatim from the SOI position.
        for (var i = 0; i < jpeg.Length; i++)
            Assert.Equal(jpeg[i], raw[soiPos + i]);
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
    public void Tiff_UnsupportedCompression_OldJpeg_ThrowsNotSupportedException()
    {
        // Compression=6 (old-style JPEG) must still throw.
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
        // BitsPerSample=4 is not supported.
        var tiff = CreateRgbTiffWithBitsPerSample(2, 2, bps: 4);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_Jpeg7_MultiStrip_ThrowsNotSupportedException()
    {
        // Compression=7 with more than one strip must throw.
        var jpeg = BuildMinimalJpeg(2, 2);
        var tiff = BuildTiffWithJpeg7MultiStrip(jpeg);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_Jpeg7_YCbCr_Photometric6_LoadsAsDctDecode()
    {
        // A single-strip TIFF with Compression=7 and PhotometricInterpretation=6 (YCbCr)
        // embedding a 3-component JPEG must load successfully as a DCTDecode passthrough.
        // The photometric=6 guard must NOT fire — JPEG-7 is dispatched early before it.
        var jpeg = BuildMinimalJpeg3Component(2, 1);
        var tiff = BuildTiffWithJpeg7Photometric(jpeg, photometric: 6);
        var img = TiffImageLoader.Load(tiff);

        var dictText = StreamDictText(img);
        Assert.Contains("DCTDecode", dictText);
        Assert.DoesNotContain("FlateDecode", dictText);

        // JPEG bytes must appear verbatim.
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
        img.BuildStream().WriteTo(writer);
        var raw = ms.ToArray();
        var soiPos = -1;
        for (var i = 0; i < raw.Length - 1; i++)
        {
            if (raw[i] == 0xFF && raw[i + 1] == 0xD8) { soiPos = i; break; }
        }
        Assert.True(soiPos >= 0, "JPEG SOI marker FF D8 not found in TIFF stream output.");
        for (var i = 0; i < jpeg.Length; i++)
            Assert.Equal(jpeg[i], raw[soiPos + i]);
    }

    [Fact]
    public void Tiff_Jpeg7_Rgb_Photometric2_LoadsAsDctDecode()
    {
        // Sanity: Compression=7 with photometric=2 (RGB) and a 3-component JPEG loads correctly.
        var jpeg = BuildMinimalJpeg3Component(2, 1);
        var tiff = BuildTiffWithJpeg7Photometric(jpeg, photometric: 2);
        var img = TiffImageLoader.Load(tiff);

        var dictText = StreamDictText(img);
        Assert.Contains("DCTDecode", dictText);
        Assert.DoesNotContain("FlateDecode", dictText);
    }

    [Fact]
    public void Tiff_Lzw_YCbCr_Photometric6_ThrowsNotSupportedException()
    {
        // PhotometricInterpretation=6 (YCbCr) with a non-JPEG compression (LZW) must
        // still be rejected — the YCbCr allowance is scoped to the JPEG-7 early dispatch only.
        var rawPixels = new byte[] { 0x80, 0x80, 0x80 };
        var lzwData = TiffLzwEncode(rawPixels);
        var tiff = BuildTiff(1, 1, 8, 5, photometric: 6, samplesPerPixel: 3,
            lzwData, littleEndian: true, colorMap: null, alphaIncluded: false);
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

    private static int ReadBitsPerComponent(VellumPdf.Core.PdfStream stream)
    {
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
        stream.WriteTo(writer);
        var text = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        const string key = "/BitsPerComponent ";
        var idx = text.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("BitsPerComponent not found in stream dict.");
        var rest = text[(idx + key.Length)..];
        var end = rest.IndexOf('\n');
        if (end < 0) end = rest.IndexOf(' ');
        if (end < 0) end = rest.Length;
        return int.Parse(rest[..end].Trim());
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
    /// Builds a minimal TIFF with PlanarConfiguration=2.
    /// pixels must be in chunky (interleaved) format — this builder separates them into planes.
    /// Strategy: write plane data first, then write strip offset/bytecount arrays immediately
    /// before the IFD (so they have known offsets), then write the IFD pointing to them.
    /// This avoids the TIFF value-inline-vs-offset complexity for multi-entry arrays.
    /// For count=1 (single-sample images), the TIFF spec stores the 4-byte value inline —
    /// we handle that by embedding the plane offset directly.
    /// </summary>
    private static byte[] BuildPlanarTiff(
        int w, int h, int bitsPerSample, int compression,
        byte[] chunkyPixels, int samplesPerPixel = 3, int photometric = 2)
    {
        int bytesPerSample = bitsPerSample == 16 ? 2 : 1;
        int planeRowBytes = w * bytesPerSample;
        int pixelStride = samplesPerPixel * bytesPerSample;

        // Split chunky pixels into separate plane buffers.
        var planes = new byte[samplesPerPixel][];
        for (var p = 0; p < samplesPerPixel; p++)
        {
            planes[p] = new byte[h * planeRowBytes];
            for (var row = 0; row < h; row++)
            {
                for (var col = 0; col < w; col++)
                {
                    var srcBase = row * w * pixelStride + col * pixelStride + p * bytesPerSample;
                    var dstBase = row * planeRowBytes + col * bytesPerSample;
                    for (var b = 0; b < bytesPerSample; b++)
                        planes[p][dstBase + b] = chunkyPixels[srcBase + b];
                }
            }
        }

        // Layout:
        //   offset 0-3: byte order + magic
        //   offset 4-7: IFD offset (placeholder filled in later)
        //   offset 8..: plane data (all planes concatenated)
        //   then: strip-offsets array (samplesPerPixel × 4 bytes) — only if spp > 1
        //   then: strip-bytecounts array (samplesPerPixel × 4 bytes) — only if spp > 1
        //   then: IFD
        using var ms = new MemoryStream();

        // Byte order: II little-endian
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        // IFD offset placeholder at bytes 4-7
        var ifdOffsetPos = (int)ms.Position;
        WriteU32(ms, 0, true); // placeholder

        // Write plane data, record offsets
        var planeOffsets = new uint[samplesPerPixel];
        var planeByteCount = (uint)(h * planeRowBytes);
        for (var p = 0; p < samplesPerPixel; p++)
        {
            planeOffsets[p] = (uint)ms.Position;
            ms.Write(planes[p]);
        }

        // For multi-sample images: write strip offset/bytecount arrays here (known position before IFD).
        // For single-sample: inline, no array needed.
        uint stripOffsetsTagValue;
        uint stripByteCountsTagValue;
        uint stripTagCount = (uint)samplesPerPixel;

        if (samplesPerPixel > 1)
        {
            // Arrays stored at this position, before the IFD.
            stripOffsetsTagValue = (uint)ms.Position;
            for (var p = 0; p < samplesPerPixel; p++)
                WriteU32(ms, planeOffsets[p], true);

            stripByteCountsTagValue = (uint)ms.Position;
            for (var p = 0; p < samplesPerPixel; p++)
                WriteU32(ms, planeByteCount, true);
        }
        else
        {
            // count=1, 4 bytes fits inline: store actual plane offset + byte count directly.
            stripOffsetsTagValue = planeOffsets[0];
            stripByteCountsTagValue = planeByteCount;
        }

        // IFD starts here
        var ifdStart = (uint)ms.Position;
        ms.Seek(ifdOffsetPos, SeekOrigin.Begin);
        WriteU32(ms, ifdStart, true);
        ms.Seek(0, SeekOrigin.End);

        // IFD entries (sorted by tag)
        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, (uint)bitsPerSample),
            (259, 3, 1, (uint)compression),
            (262, 3, 1, (uint)photometric),
            (273, 4, stripTagCount, stripOffsetsTagValue),
            (277, 3, 1, (uint)samplesPerPixel),
            (278, 4, 1, (uint)h),   // RowsPerStrip = full image (1 strip per plane)
            (279, 4, stripTagCount, stripByteCountsTagValue),
            (284, 3, 1, 2u),         // PlanarConfiguration = 2
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, true);

        foreach (var (tag, type, cnt, value) in entries)
        {
            var typeSize = GetTypeSize(type);
            var totalBytes = typeSize * cnt;

            WriteU16(ms, tag, true);
            WriteU16(ms, type, true);
            WriteU32(ms, cnt, true);

            if (totalBytes <= 4)
            {
                // Inline value
                if (type == 3)
                {
                    ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                    ms.WriteByte(0); ms.WriteByte(0);
                }
                else
                {
                    WriteU32(ms, value, true);
                }
            }
            else
            {
                // Offset to external array
                WriteU32(ms, value, true);
            }
        }
        WriteU32(ms, 0, true); // next IFD = 0

        return ms.ToArray();
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

    /// <summary>
    /// Builds a TIFF with a Predictor=2 tag added to the IFD.
    /// </summary>
    private static byte[] BuildTiffWithPredictor(
        int w, int h, int bitsPerSample, int compression,
        int photometric, int samplesPerPixel,
        byte[] pixelData, bool littleEndian, int predictor)
    {
        using var ms = new MemoryStream();

        if (littleEndian)
        {
            ms.WriteByte(0x49); ms.WriteByte(0x49);
            ms.WriteByte(0x2A); ms.WriteByte(0x00);
        }
        else
        {
            ms.WriteByte(0x4D); ms.WriteByte(0x4D);
            ms.WriteByte(0x00); ms.WriteByte(0x2A);
        }

        var pixelOffset = 8u;
        var ifdOffset = pixelOffset + (uint)pixelData.Length;
        WriteU32(ms, ifdOffset, littleEndian);
        ms.Write(pixelData);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, (uint)bitsPerSample),
            (259, 3, 1, (uint)compression),
            (262, 3, 1, (uint)photometric),
            (273, 4, 1, pixelOffset),
            (277, 3, 1, (uint)samplesPerPixel),
            (278, 4, 1, (uint)h),
            (279, 4, 1, (uint)pixelData.Length),
            (284, 3, 1, 1),
            (317, 3, 1, (uint)predictor), // Predictor
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, littleEndian);
        foreach (var (tag, type, count, value) in entries)
        {
            var typeSize = GetTypeSize(type);
            var totalBytes = typeSize * count;
            WriteU16(ms, tag, littleEndian);
            WriteU16(ms, type, littleEndian);
            WriteU32(ms, count, littleEndian);

            if (totalBytes <= 4)
            {
                if (littleEndian)
                {
                    if (type == 3)
                    {
                        ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                        ms.WriteByte(0); ms.WriteByte(0);
                    }
                    else WriteU32(ms, value, littleEndian);
                }
                else
                {
                    if (type == 3)
                    {
                        ms.WriteByte((byte)(value >> 8)); ms.WriteByte((byte)value);
                        ms.WriteByte(0); ms.WriteByte(0);
                    }
                    else WriteU32(ms, value, littleEndian);
                }
            }
            else
            {
                WriteU32(ms, value, littleEndian);
            }
        }
        WriteU32(ms, 0, littleEndian);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a TIFF with Compression=7 (new-style JPEG) wrapping the given JPEG bytes as a single strip.
    /// </summary>
    private static byte[] BuildTiffWithJpeg7(byte[] jpegBytes)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        var jpegOffset = 8u;
        var ifdOffset = jpegOffset + (uint)jpegBytes.Length;
        WriteU32(ms, ifdOffset, true);
        ms.Write(jpegBytes);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, 2),          // ImageWidth
            (257, 4, 1, 1),          // ImageLength
            (258, 3, 1, 8),          // BitsPerSample
            (259, 3, 1, 7),          // Compression=7 new-style JPEG
            (262, 3, 1, 2),          // PhotometricInterpretation=2 (RGB)
            (273, 4, 1, jpegOffset), // StripOffsets
            (277, 3, 1, 3),          // SamplesPerPixel
            (278, 4, 1, 1),          // RowsPerStrip
            (279, 4, 1, (uint)jpegBytes.Length), // StripByteCounts
            (284, 3, 1, 1),          // PlanarConfiguration
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, true);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteU16(ms, tag, true);
            WriteU16(ms, type, true);
            WriteU32(ms, count, true);
            if (type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else WriteU32(ms, value, true);
        }
        WriteU32(ms, 0, true);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a multi-strip TIFF with Compression=7 (to test the rejection of multi-strip JPEG).
    /// </summary>
    private static byte[] BuildTiffWithJpeg7MultiStrip(byte[] jpegBytes)
    {
        // Two fake strips — both pointing to the same JPEG data (values don't matter, just the count).
        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        var jpegOffset = 8u;
        // IFD: after JPEG bytes + two arrays of 2 longs each for StripOffsets + StripByteCounts
        var ifdOffset = jpegOffset + (uint)jpegBytes.Length;

        // We'll put strip arrays after IFD. IFD = 2 + 10*12 + 4 = 126 bytes.
        // Strip arrays follow IFD.
        var stripOffsetsOff = ifdOffset + 126u;
        var stripByteCountsOff = stripOffsetsOff + 8u; // 2 × 4 bytes

        WriteU32(ms, ifdOffset, true);
        ms.Write(jpegBytes);

        // IFD entries
        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, 2),
            (257, 4, 1, 2),  // height=2 so multi-strip
            (258, 3, 1, 8),
            (259, 3, 1, 7),  // Compression=7
            (262, 3, 1, 2),
            (273, 4, 2, stripOffsetsOff),     // 2 strips
            (277, 3, 1, 3),
            (278, 4, 1, 1),  // RowsPerStrip=1 → 2 rows = 2 strips
            (279, 4, 2, stripByteCountsOff),  // 2 strip byte counts
            (284, 3, 1, 1),
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, true);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteU16(ms, tag, true);
            WriteU16(ms, type, true);
            WriteU32(ms, count, true);
            if (count * GetTypeSize(type) <= 4 && type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else WriteU32(ms, value, true);
        }
        WriteU32(ms, 0, true); // next IFD

        // Strip offsets array: both strips point to same jpeg data
        WriteU32(ms, jpegOffset, true);
        WriteU32(ms, jpegOffset, true);

        // Strip byte counts array
        WriteU32(ms, (uint)jpegBytes.Length, true);
        WriteU32(ms, (uint)jpegBytes.Length, true);

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

    // ── TIFF LZW encoder (MSB-first, early-change, full LZW) ────────────────

    /// <summary>
    /// Full TIFF-variant LZW encoder for test fixtures.
    /// MSB-first bit packing; early change (grow code width when nextCode == (1 &lt;&lt; codeWidth) - 1).
    /// Implements the standard LZW string-table algorithm; emits back-references and
    /// triggers the KwKwK decoder path on highly repetitive input.
    /// Resets the table (emits ClearCode) when the table fills to 4096 entries.
    /// </summary>
    private static byte[] TiffLzwEncode(byte[] input)
    {
        const int clearCode = 256;
        const int eoiCode = 257;
        const int firstFreeCode = 258;
        const int maxTableSize = 4096;

        using var ms = new MemoryStream();
        int codeWidth = 9;
        int nextCode = firstFreeCode;

        // LZW string table: key = (prefixCode << 8) | suffixByte → assigned code
        var table = new Dictionary<long, int>();

        // MSB-first bit buffer
        int bitBuf = 0;
        int bitsInBuf = 0;

        void EmitCode(int code)
        {
            bitBuf = (bitBuf << codeWidth) | (code & ((1 << codeWidth) - 1));
            bitsInBuf += codeWidth;
            while (bitsInBuf >= 8)
            {
                bitsInBuf -= 8;
                ms.WriteByte((byte)(bitBuf >> bitsInBuf));
                bitBuf &= (1 << bitsInBuf) - 1;
            }
        }

        void ResetTable()
        {
            table.Clear();
            codeWidth = 9;
            nextCode = firstFreeCode;
        }

        EmitCode(clearCode);

        if (input.Length == 0)
        {
            EmitCode(eoiCode);
            if (bitsInBuf > 0)
                ms.WriteByte((byte)(bitBuf << (8 - bitsInBuf)));
            return ms.ToArray();
        }

        int w = input[0]; // current prefix code (starts as first byte's literal)

        for (var i = 1; i < input.Length; i++)
        {
            int k = input[i];
            long key = ((long)w << 8) | (byte)k;

            if (table.TryGetValue(key, out int existing))
            {
                // W+K is in the table; extend the current string
                w = existing;
            }
            else
            {
                // W+K not in table: output code(W), add W+K to table, W=K
                EmitCode(w);

                if (nextCode < maxTableSize)
                {
                    table[key] = nextCode;
                    nextCode++;
                    // TIFF early-change: the decoder grows its read width when its nextCode
                    // reaches (1<<width)-1, which happens one entry after the encoder adds the
                    // same entry (because the first code after a ClearCode is decoded without
                    // adding an entry, keeping the decoder one entry behind).  To stay in sync,
                    // the encoder must grow its write width one entry later — i.e. when nextCode
                    // reaches exactly (1<<codeWidth), not (1<<codeWidth)-1.
                    if (nextCode == (1 << codeWidth) && codeWidth < 12)
                        codeWidth++;
                }
                else
                {
                    // Table full: emit ClearCode and reset
                    EmitCode(clearCode);
                    ResetTable();
                }

                w = k;
            }
        }

        // Emit the final pending code
        EmitCode(w);
        EmitCode(eoiCode);

        // Flush remaining bits (pad to byte boundary)
        if (bitsInBuf > 0)
            ms.WriteByte((byte)(bitBuf << (8 - bitsInBuf)));

        return ms.ToArray();
    }

    // ── Minimal baseline JPEG builder ────────────────────────────────────────

    /// <summary>
    /// Builds a minimal valid baseline JPEG (grayscale, 1×1 or 2×1).
    /// Contains proper SOI, APP0, SOF0, DHT, SOS, and EOI markers.
    /// This is the smallest possible valid JPEG that JpegImageLoader.Load can parse.
    /// </summary>
    private static byte[] BuildMinimalJpeg(int w, int h)
    {
        // We build a minimal greyscale 1-component JPEG.
        // Use a hardcoded minimal JPEG structure. The actual image data is
        // a solid grey value — we just need a structurally valid JPEG.
        using var ms = new MemoryStream();

        // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);

        // APP0 (JFIF marker) — 16 bytes
        ms.WriteByte(0xFF); ms.WriteByte(0xE0);
        WriteJpegU16(ms, 16); // length including length field
        ms.Write("JFIF\0"u8);
        ms.WriteByte(1); ms.WriteByte(1); // version 1.1
        ms.WriteByte(0);                  // aspect ratio units
        WriteJpegU16(ms, 1);              // X density
        WriteJpegU16(ms, 1);              // Y density
        ms.WriteByte(0); ms.WriteByte(0); // thumbnail size

        // SOF0 (Start Of Frame, baseline DCT) — minimal for w×h, 1 component
        ms.WriteByte(0xFF); ms.WriteByte(0xC0);
        WriteJpegU16(ms, 11); // length: 8 + 3 per component = 11 for 1 component
        ms.WriteByte(8);      // precision
        WriteJpegU16(ms, (ushort)h);
        WriteJpegU16(ms, (ushort)w);
        ms.WriteByte(1);      // 1 component
        ms.WriteByte(1);      // component id
        ms.WriteByte(0x11);   // sampling factors 1×1
        ms.WriteByte(0);      // quantization table id

        // EOI — terminate immediately (not a valid scan, but JpegImageLoader only reads SOF)
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal valid baseline JPEG with 3 components (e.g. YCbCr or RGB).
    /// Contains SOI, APP0, SOF0 with 3 components, and EOI.
    /// JpegImageLoader.ReadSof returns components=3 → DeviceRGB colour space.
    /// </summary>
    private static byte[] BuildMinimalJpeg3Component(int w, int h)
    {
        using var ms = new MemoryStream();

        // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);

        // APP0 (JFIF marker) — 16 bytes
        ms.WriteByte(0xFF); ms.WriteByte(0xE0);
        WriteJpegU16(ms, 16);
        ms.Write("JFIF\0"u8);
        ms.WriteByte(1); ms.WriteByte(1); // version 1.1
        ms.WriteByte(0);                  // aspect ratio units
        WriteJpegU16(ms, 1);              // X density
        WriteJpegU16(ms, 1);              // Y density
        ms.WriteByte(0); ms.WriteByte(0); // thumbnail size

        // SOF0 — 3 components: length = 8 + 3*3 = 17
        ms.WriteByte(0xFF); ms.WriteByte(0xC0);
        WriteJpegU16(ms, 17);
        ms.WriteByte(8);      // precision
        WriteJpegU16(ms, (ushort)h);
        WriteJpegU16(ms, (ushort)w);
        ms.WriteByte(3);      // 3 components
        // Component 1
        ms.WriteByte(1); ms.WriteByte(0x11); ms.WriteByte(0);
        // Component 2
        ms.WriteByte(2); ms.WriteByte(0x11); ms.WriteByte(1);
        // Component 3
        ms.WriteByte(3); ms.WriteByte(0x11); ms.WriteByte(1);

        // EOI
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a single-strip TIFF with Compression=7 (new-style JPEG) and the specified photometric.
    /// </summary>
    private static byte[] BuildTiffWithJpeg7Photometric(byte[] jpegBytes, int photometric)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        var jpegOffset = 8u;
        var ifdOffset = jpegOffset + (uint)jpegBytes.Length;
        WriteU32(ms, ifdOffset, true);
        ms.Write(jpegBytes);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, 2),
            (257, 4, 1, 1),
            (258, 3, 1, 8),
            (259, 3, 1, 7),                              // Compression=7
            (262, 3, 1, (uint)photometric),              // PhotometricInterpretation (caller-supplied)
            (273, 4, 1, jpegOffset),
            (277, 3, 1, 3),
            (278, 4, 1, 1),
            (279, 4, 1, (uint)jpegBytes.Length),
            (284, 3, 1, 1),
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, true);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteU16(ms, tag, true);
            WriteU16(ms, type, true);
            WriteU32(ms, count, true);
            if (type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else WriteU32(ms, value, true);
        }
        WriteU32(ms, 0, true);
        return ms.ToArray();
    }

    private static void WriteJpegU16(Stream s, ushort v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
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

    // ── CCITT Group 4 (Compression=4) tests ─────────────────────────────────

    [Fact]
    public void Tiff_G4_SingleStrip_Photometric0_CcittFaxDecode()
    {
        // Photometric=0 (WhiteIsZero) with G4 → /Filter CCITTFaxDecode, no /BlackIs1.
        var g4Data = CcittImageTests.BuildAllWhiteG4(8, 4);
        var tiff = BuildTiffG4(8, 4, photometric: 0, g4Data);
        var img = TiffImageLoader.Load(tiff);

        var dict = StreamDictText(img);
        Assert.Contains("/CCITTFaxDecode", dict);
        Assert.DoesNotContain("/FlateDecode", dict);
        Assert.Contains("/K -1", dict);
        Assert.Contains("/Columns 8", dict);
        Assert.Contains("/Rows 4", dict);
        Assert.DoesNotContain("/BlackIs1", dict);
    }

    [Fact]
    public void Tiff_G4_SingleStrip_Photometric1_BlackIs1_True()
    {
        // Photometric=1 (BlackIsZero) → /BlackIs1 true must be present.
        var g4Data = CcittImageTests.BuildAllWhiteG4(8, 4);
        var tiff = BuildTiffG4(8, 4, photometric: 1, g4Data);
        var img = TiffImageLoader.Load(tiff);

        Assert.Contains("/BlackIs1 true", StreamDictText(img));
    }

    [Fact]
    public void Tiff_G4_Dimensions_Correct()
    {
        var g4Data = CcittImageTests.BuildAllWhiteG4(16, 12);
        var tiff = BuildTiffG4(16, 12, photometric: 0, g4Data);
        var img = TiffImageLoader.Load(tiff);

        Assert.Equal(16, img.Width);
        Assert.Equal(12, img.Height);
    }

    [Fact]
    public void Tiff_G4_SingleStrip_BytesVerbatim()
    {
        // The strip bytes must appear verbatim in the output stream.
        var g4Data = CcittImageTests.BuildAllWhiteG4(8, 4);
        var tiff = BuildTiffG4(8, 4, photometric: 0, g4Data);
        var img = TiffImageLoader.Load(tiff);

        var raw = WriteStreamRaw(img);
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        Assert.True(markerStart >= 0, "stream marker not found");
        var bodyStart = markerStart + 8;
        var endStream = FindSequence(raw, "\nendstream"u8);
        Assert.True(endStream >= 0, "endstream not found");
        var body = raw[bodyStart..endStream];
        Assert.Equal(g4Data, body);
    }

    [Fact]
    public void Tiff_G4_MultiStrip_ThrowsNotSupportedException()
    {
        var g4Data = CcittImageTests.BuildAllWhiteG4(8, 4);
        var tiff = BuildTiffG4MultiStrip(8, 4, g4Data);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_G4_FillOrder2_BytesAreReversed()
    {
        // FillOrder=2 (LSB-first) TIFF: the TiffImageLoader must bit-reverse every byte
        // before passthrough so the CCITTFaxDecode filter receives MSB-first data.
        var original = new byte[] { 0xAB, 0xCD };
        var tiff = BuildTiffG4WithFillOrder(4, 2, photometric: 0, stripData: original, fillOrder: 2);
        var img = TiffImageLoader.Load(tiff);

        var raw = WriteStreamRaw(img);
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        var bodyStart = markerStart + 8;
        var endStream = FindSequence(raw, "\nendstream"u8);
        var body = raw[bodyStart..endStream];

        // 0xAB = 1010_1011 reversed = 1101_0101 = 0xD5
        // 0xCD = 1100_1101 reversed = 1011_0011 = 0xB3
        Assert.Equal(new byte[] { 0xD5, 0xB3 }, body);
    }

    [Fact]
    public void Tiff_CcittG3_Compression2_MultiSample_ThrowsNotSupportedException()
    {
        // Compression=2 with SamplesPerPixel=3 (RGB) must still be rejected —
        // G3 only supports bilevel (SamplesPerPixel=1).
        var tiff = CreateRgbTiffWithCompression(2, 2, compressionValue: 2);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_CcittG3_Compression3_MultiSample_ThrowsNotSupportedException()
    {
        // Compression=3 with SamplesPerPixel=3 (RGB) must still be rejected.
        var tiff = CreateRgbTiffWithCompression(2, 2, compressionValue: 3);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    // ── CCITT Group 3 (Compression=2 and 3) ─────────────────────────────────

    [Fact]
    public void Tiff_G3_Compression2_Filter_IsCcittFaxDecode()
    {
        // Compression=2: K=0, EncodedByteAlign=true, EndOfLine=false (PDF default = absent).
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3(8, 2, photometric: 0, compression: 2, t4Options: 0, mhData);
        var img = TiffImageLoader.Load(tiff);

        var dict = StreamDictText(img);
        Assert.Contains("/CCITTFaxDecode", dict);
        Assert.DoesNotContain("/FlateDecode", dict);
        Assert.Contains("/K 0", dict);
        Assert.Contains("/EncodedByteAlign true", dict);
        Assert.DoesNotContain("/EndOfLine", dict);
        Assert.Contains("/Columns 8", dict);
        Assert.Contains("/Rows 2", dict);
    }

    [Fact]
    public void Tiff_G3_Compression2_Photometric0_NoBlackIs1()
    {
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3(8, 2, photometric: 0, compression: 2, t4Options: 0, mhData);
        var img = TiffImageLoader.Load(tiff);
        Assert.DoesNotContain("/BlackIs1", StreamDictText(img));
    }

    [Fact]
    public void Tiff_G3_Compression2_Photometric1_BlackIs1_True()
    {
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3(8, 2, photometric: 1, compression: 2, t4Options: 0, mhData);
        var img = TiffImageLoader.Load(tiff);
        Assert.Contains("/BlackIs1 true", StreamDictText(img));
    }

    [Fact]
    public void Tiff_G3_Compression2_BytesVerbatim()
    {
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3(8, 2, photometric: 0, compression: 2, t4Options: 0, mhData);
        var img = TiffImageLoader.Load(tiff);

        var raw = WriteStreamRaw(img);
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        Assert.True(markerStart >= 0, "stream marker not found");
        var bodyStart = markerStart + 8;
        var endStream = FindSequence(raw, "\nendstream"u8);
        var body = raw[bodyStart..endStream];
        Assert.Equal(mhData, body);
    }

    [Fact]
    public void Tiff_G3_Compression3_1D_T4Options0_DecodeParms_Correct()
    {
        // Compression=3, T4Options=0 (bit0=0 → 1D), bit2=0 → EncodedByteAlign=false, EndOfLine=true.
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3(8, 2, photometric: 0, compression: 3, t4Options: 0, mhData);
        var img = TiffImageLoader.Load(tiff);

        var dict = StreamDictText(img);
        Assert.Contains("/CCITTFaxDecode", dict);
        Assert.Contains("/K 0", dict);
        Assert.DoesNotContain("/EncodedByteAlign", dict);
        Assert.Contains("/EndOfLine true", dict);
    }

    [Fact]
    public void Tiff_G3_Compression3_1D_T4Options4_EncodedByteAlign_True()
    {
        // T4Options bit2=1 → EncodedByteAlign=true.
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3(8, 2, photometric: 0, compression: 3, t4Options: 4, mhData);
        var img = TiffImageLoader.Load(tiff);

        var dict = StreamDictText(img);
        Assert.Contains("/EncodedByteAlign true", dict);
        Assert.Contains("/EndOfLine true", dict);
        Assert.Contains("/K 0", dict);
    }

    [Fact]
    public void Tiff_G3_Compression3_2D_T4Options1_K_IsPositive()
    {
        // T4Options bit0=1 → mixed 1D/2D → K = height (positive), EndOfLine=true.
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3(8, 2, photometric: 0, compression: 3, t4Options: 1, mhData);
        var img = TiffImageLoader.Load(tiff);

        var dict = StreamDictText(img);
        Assert.Contains("/EndOfLine true", dict);
        // K must be positive (equal to height = 2 for this fixture).
        Assert.Contains("/K 2", dict);
    }

    [Fact]
    public void Tiff_G3_Dimensions_Correct()
    {
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3(8, 2, photometric: 0, compression: 2, t4Options: 0, mhData);
        var img = TiffImageLoader.Load(tiff);
        Assert.Equal(8, img.Width);
        Assert.Equal(2, img.Height);
    }

    [Fact]
    public void Tiff_G3_MultiStrip_ThrowsNotSupportedException()
    {
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3MultiStrip(8, 2, photometric: 0, mhData);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    [Fact]
    public void Tiff_G3_FillOrder2_BytesAreReversed()
    {
        // FillOrder=2 TIFF: bytes must be bit-reversed before passthrough.
        var original = new byte[] { 0xAB, 0xCD };
        var tiff = BuildTiffG3WithFillOrder(4, 2, photometric: 0, compression: 2, t4Options: 0,
            original, fillOrder: 2);
        var img = TiffImageLoader.Load(tiff);

        var raw = WriteStreamRaw(img);
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        var bodyStart = markerStart + 8;
        var endStream = FindSequence(raw, "\nendstream"u8);
        var body = raw[bodyStart..endStream];

        // 0xAB reversed = 0xD5; 0xCD reversed = 0xB3
        Assert.Equal(new byte[] { 0xD5, 0xB3 }, body);
    }

    [Fact]
    public void Tiff_G3_DecodeToRaster_AllWhite_ReturnsFlate()
    {
        // DecodeToRaster via TiffImageLoader for Compression=2 (Modified Huffman 1D).
        var mhData = CcittImageTests.BuildAllWhite1D_8wide_2rows();
        var tiff = BuildTiffG3(8, 2, photometric: 0, compression: 2, t4Options: 0, mhData);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        var img = TiffImageLoader.Load(tiff, opts);

        var dict = StreamDictText(img);
        Assert.Contains("/FlateDecode", dict);
        Assert.DoesNotContain("/CCITTFaxDecode", dict);
        Assert.Equal(8, img.Width);
        Assert.Equal(2, img.Height);
    }

    [Fact]
    public void Tiff_G3_DecodeToRaster_2D_ThrowsNotSupportedException()
    {
        // Compression=3 with T4Options bit0=1 (2D rows) → DecodeToRaster must throw.
        // Build a stream that contains an EOL (000000000001 = 12 bits) followed by a
        // 2D-row tag bit (0), so the decoder detects a 2D row immediately.
        // [0x00, 0x10]: bits 0-11 = "000000000001" (EOL), bit 12 = 0 (2D row indicator).
        var stream2D = new byte[] { 0x00, 0x10 };
        var tiff = BuildTiffG3(8, 2, photometric: 0, compression: 3, t4Options: 1, stream2D);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff, opts));
    }

    [Fact]
    public void Tiff_G3_TruncatedStrip_ThrowsInvalidDataException()
    {
        // Strip claims 8 bytes but TIFF file is truncated to half that.
        var tiff = BuildTiffG3WithTruncatedStrip(8, 2);
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    // ── G4 TIFF builder helpers ───────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal single-strip TIFF with Compression=4 (CCITT Group 4).
    /// BitsPerSample defaults to 1 (bilevel). SamplesPerPixel=1.
    /// </summary>
    private static byte[] BuildTiffG4(int w, int h, int photometric, byte[] stripData)
        => BuildTiffG4WithFillOrder(w, h, photometric, stripData, fillOrder: 1);

    private static byte[] BuildTiffG4WithFillOrder(
        int w, int h, int photometric, byte[] stripData, int fillOrder)
    {
        using var ms = new MemoryStream();
        // Little-endian II
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        var stripOffset = 8u;
        var ifdOffset = stripOffset + (uint)stripData.Length;
        WriteU32(ms, ifdOffset, true);
        ms.Write(stripData);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),             // ImageWidth
            (257, 4, 1, (uint)h),             // ImageLength
            (258, 3, 1, 1),                   // BitsPerSample = 1
            (259, 3, 1, 4),                   // Compression = 4 (CCITT G4)
            (262, 3, 1, (uint)photometric),   // PhotometricInterpretation
            (266, 3, 1, (uint)fillOrder),     // FillOrder
            (273, 4, 1, stripOffset),         // StripOffsets
            (277, 3, 1, 1),                   // SamplesPerPixel
            (278, 4, 1, (uint)h),             // RowsPerStrip
            (279, 4, 1, (uint)stripData.Length), // StripByteCounts
            (284, 3, 1, 1),                   // PlanarConfiguration
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, true);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteU16(ms, tag, true);
            WriteU16(ms, type, true);
            WriteU32(ms, count, true);
            if (type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else
            {
                WriteU32(ms, value, true);
            }
        }
        WriteU32(ms, 0, true);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a multi-strip G4 TIFF (two strips) to test the rejection path.
    /// </summary>
    private static byte[] BuildTiffG4MultiStrip(int w, int h, byte[] stripData)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        // Two strips, each half the data (approximate).
        var half = (stripData.Length + 1) / 2;
        var strip1 = stripData[..half];
        var strip2 = stripData[half..];

        var strip1Offset = 8u;
        var strip2Offset = strip1Offset + (uint)strip1.Length;

        // IFD will follow both strips, then the strip-array data.
        // StripOffsets: 2 × LONG = 8 bytes; StripByteCounts: 2 × LONG = 8 bytes.
        // We'll write strip arrays after IFD. Compute IFD size: 2 + 10*12 + 4 = 126 bytes.
        var ifdOffset = strip2Offset + (uint)strip2.Length;
        var stripOffsetsArrayOff = ifdOffset + 126u;
        var stripByteCountsArrayOff = stripOffsetsArrayOff + 8u;

        WriteU32(ms, ifdOffset, true);
        ms.Write(strip1);
        ms.Write(strip2);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, 1),
            (259, 3, 1, 4),
            (262, 3, 1, 0),
            (273, 4, 2, stripOffsetsArrayOff),
            (277, 3, 1, 1),
            (278, 4, 1, (uint)(h / 2)),
            (279, 4, 2, stripByteCountsArrayOff),
            (284, 3, 1, 1),
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, true);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteU16(ms, tag, true);
            WriteU16(ms, type, true);
            WriteU32(ms, count, true);
            if (count * GetTypeSize(type) <= 4 && type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else
            {
                WriteU32(ms, value, true);
            }
        }
        WriteU32(ms, 0, true);

        // Strip offsets array
        WriteU32(ms, strip1Offset, true);
        WriteU32(ms, strip2Offset, true);

        // Strip byte counts array
        WriteU32(ms, (uint)strip1.Length, true);
        WriteU32(ms, (uint)strip2.Length, true);

        return ms.ToArray();
    }

    // ── G3 TIFF builder helpers ───────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal single-strip TIFF with Compression=2 or 3 (CCITT Group 3).
    /// BitsPerSample=1, SamplesPerPixel=1.
    /// </summary>
    private static byte[] BuildTiffG3(
        int w, int h, int photometric, int compression, int t4Options, byte[] stripData)
        => BuildTiffG3WithFillOrder(w, h, photometric, compression, t4Options, stripData, fillOrder: 1);

    private static byte[] BuildTiffG3WithFillOrder(
        int w, int h, int photometric, int compression, int t4Options,
        byte[] stripData, int fillOrder)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49); // II little-endian
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        var stripOffset = 8u;
        var ifdOffset = stripOffset + (uint)stripData.Length;
        WriteU32(ms, ifdOffset, true);
        ms.Write(stripData);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, 1),                       // BitsPerSample=1
            (259, 3, 1, (uint)compression),       // Compression (2 or 3)
            (262, 3, 1, (uint)photometric),
            (266, 3, 1, (uint)fillOrder),         // FillOrder
            (273, 4, 1, stripOffset),
            (277, 3, 1, 1),                       // SamplesPerPixel=1
            (278, 4, 1, (uint)h),                 // RowsPerStrip=h
            (279, 4, 1, (uint)stripData.Length),
            (284, 3, 1, 1),                       // PlanarConfiguration=chunky
            (292, 4, 1, (uint)t4Options),         // T4Options (LONG)
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, true);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteU16(ms, tag, true);
            WriteU16(ms, type, true);
            WriteU32(ms, count, true);
            if (type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else
            {
                WriteU32(ms, value, true);
            }
        }
        WriteU32(ms, 0, true);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a multi-strip G3 TIFF (two strips) to test the rejection path.
    /// </summary>
    private static byte[] BuildTiffG3MultiStrip(int w, int h, int photometric, byte[] stripData)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        var half = (stripData.Length + 1) / 2;
        var strip1 = stripData[..half];
        var strip2 = half < stripData.Length ? stripData[half..] : new byte[] { 0x00 };

        var strip1Offset = 8u;
        var strip2Offset = strip1Offset + (uint)strip1.Length;

        // IFD size: 2 + 11*12 + 4 = 138 bytes. Strip arrays follow.
        var ifdOffset = strip2Offset + (uint)strip2.Length;
        var stripOffsetsArrayOff = ifdOffset + 138u;
        var stripByteCountsArrayOff = stripOffsetsArrayOff + 8u;

        WriteU32(ms, ifdOffset, true);
        ms.Write(strip1);
        ms.Write(strip2);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, 1),
            (259, 3, 1, 2),                                 // Compression=2
            (262, 3, 1, (uint)photometric),
            (266, 3, 1, 1),                                 // FillOrder=1
            (273, 4, 2, stripOffsetsArrayOff),
            (277, 3, 1, 1),
            (278, 4, 1, (uint)(h / 2)),
            (279, 4, 2, stripByteCountsArrayOff),
            (284, 3, 1, 1),
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, true);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteU16(ms, tag, true);
            WriteU16(ms, type, true);
            WriteU32(ms, count, true);
            if (count * GetTypeSize(type) <= 4 && type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else WriteU32(ms, value, true);
        }
        WriteU32(ms, 0, true);

        WriteU32(ms, strip1Offset, true);
        WriteU32(ms, strip2Offset, true);
        WriteU32(ms, (uint)strip1.Length, true);
        WriteU32(ms, (uint)strip2.Length, true);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a G3 TIFF where StripByteCounts claims a byte count large enough to extend
    /// well beyond the end of the file, triggering the bounds check in the G3 dispatch path.
    /// </summary>
    private static byte[] BuildTiffG3WithTruncatedStrip(int w, int h)
    {
        // Use a claimed byte count larger than any reasonable file: int.MaxValue.
        // ValidateTiffLong accepts values up to int.MaxValue; the subsequent
        // bounds check (g3Offset + g3Length > tiff.Length) will always fire.
        const uint claimedByteCount = 0x7FFFFFFF; // int.MaxValue

        var realData = new byte[] { 0x98, 0x98 }; // 2 bytes of real strip data (irrelevant)

        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        var stripOffset = 8u;
        var ifdOffset = stripOffset + (uint)realData.Length;
        WriteU32(ms, ifdOffset, true);
        ms.Write(realData);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, 1),
            (259, 3, 1, 2),                    // Compression=2
            (262, 3, 1, 0),
            (266, 3, 1, 1),
            (273, 4, 1, stripOffset),
            (277, 3, 1, 1),
            (278, 4, 1, (uint)h),
            (279, 4, 1, claimedByteCount),     // int.MaxValue > any file length
            (284, 3, 1, 1),
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteU16(ms, (ushort)entries.Count, true);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteU16(ms, tag, true);
            WriteU16(ms, type, true);
            WriteU32(ms, count, true);
            if (type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else WriteU32(ms, value, true);
        }
        WriteU32(ms, 0, true);
        return ms.ToArray();
    }

    private static byte[] WriteStreamRaw(VellumPdf.Images.PdfImageXObject img)
    {
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
        img.BuildStream().WriteTo(writer);
        return ms.ToArray();
    }
}
