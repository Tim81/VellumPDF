// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Decodes a baseline TIFF 6.0 file and produces an Image XObject.
///
/// Supported:
///   • Byte order: II (little-endian) and MM (big-endian).
///   • Compression: 1 (None/Uncompressed), 4 (CCITT Group 4 / T.6 — single strip,
///     CCITTFaxDecode passthrough), 5 (LZW), 7 (new-style JPEG — single strip,
///     DCTDecode passthrough), and 32773 (PackBits RLE).
///   • Photometric: 0 (WhiteIsZero greyscale — inverted), 1 (BlackIsZero greyscale),
///     2 (RGB), 3 (Palette/Indexed — ColorMap expanded to DeviceRGB).
///   • BitsPerSample: 8 (all photometrics) and 16 (greyscale and RGB only).
///     CCITT Group 4 uses BitsPerSample=1 (bilevel), handled before the 8/16 gate.
///   • SamplesPerPixel: 1 (grey/palette), 3 (RGB), 4 (RGB+alpha → /SMask).
///   • PlanarConfiguration: 1 (chunky) and 2 (planar — reassembled to chunky).
///   • Multiple strips (StripOffsets / StripByteCounts / RowsPerStrip).
///   • Horizontal differencing predictor (Predictor=2) for 8-bit and 16-bit samples.
///   • 16-bit bit-depth: honour ImageLoadOptions.BitDepth (Preserve=16bpc; ReduceToEight=8bpc).
///   • FillOrder tag (266): FillOrder=2 (LSB-first) bytes are bit-reversed to MSB-first
///     before passthrough so the CCITTFaxDecode filter receives standard bit order.
///
/// Rejected (throws NotSupportedException):
///   • Compression 2 and 3 (CCITT Group 3 variants) — T4Options mapping required, out of scope for v1.3.
///   • Compression 6 (old-style JPEG) — not implemented.
///   • Compression 4 (CCITT Group 4) with more than one strip.
///   • BitsPerSample other than 8 or 16 (for non-CCITT images); BitsPerSample=16 with palette photometric.
///   • Compression 7 (new-style JPEG) with more than one strip.
///   • Predictor=2 + BitsPerSample=16 is supported; other Predictor values are rejected.
///   • Unsupported photometric values.
///
/// Throws InvalidDataException on truncated or structurally invalid data.
/// </summary>
public static class TiffImageLoader
{
    // IFD entry type sizes in bytes
    private static readonly int[] TypeSizes = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8];

    // Well-known TIFF tags
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagPhotometricInterpretation = 262;
    private const ushort TagStripOffsets = 273;
    private const ushort TagSamplesPerPixel = 277;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagPlanarConfiguration = 284;
    private const ushort TagPredictor = 317;
    private const ushort TagColorMap = 320;
    private const ushort TagFillOrder = 266;
    private const ushort TagExtraSamples = 338;

    // Compression constants
    private const int CompressionNone = 1;
    private const int CompressionCcittG3Variants1D = 2;
    private const int CompressionCcittG3Mixed = 3;
    private const int CompressionCcittG4 = 4;
    private const int CompressionLzw = 5;
    private const int CompressionJpegOldStyle = 6;
    private const int CompressionJpegNewStyle = 7;
    private const int CompressionPackBits = 32773;

    // 256-entry bit-reversal lookup: BitReverse[b] gives the byte with all bits of b reversed.
    // Built once via a static initializer; MSB→LSB order reversal (FillOrder 2 → FillOrder 1).
    private static readonly byte[] BitReverse = BuildBitReverseLookup();

    private static byte[] BuildBitReverseLookup()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var b = (byte)i;
            b = (byte)(((b & 0xF0) >> 4) | ((b & 0x0F) << 4));
            b = (byte)(((b & 0xCC) >> 2) | ((b & 0x33) << 2));
            b = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
            table[i] = b;
        }
        return table;
    }

    /// <summary>Decodes baseline TIFF file bytes into a FlateDecode Image XObject.</summary>
    public static PdfImageXObject Load(byte[] tiff) => Load(tiff, ImageLoadOptions.Default);

    /// <summary>Decodes baseline TIFF file bytes into a FlateDecode Image XObject with the specified load options.</summary>
    public static PdfImageXObject Load(byte[] tiff, ImageLoadOptions options)
    {
        if (tiff.Length < 8)
            throw new InvalidDataException("TIFF file too small to contain a valid header.");

        // Determine byte order
        bool littleEndian;
        if (tiff[0] == 0x49 && tiff[1] == 0x49)
            littleEndian = true;
        else if (tiff[0] == 0x4D && tiff[1] == 0x4D)
            littleEndian = false;
        else
            throw new InvalidDataException("TIFF byte-order mark must be 'II' or 'MM'.");

        // Magic number: 42
        var magic = ReadU16(tiff, 2, littleEndian);
        if (magic != 42)
            throw new InvalidDataException($"TIFF magic number must be 42; found {magic}.");

        // Offset to first IFD
        var ifdOffset = (int)ReadU32(tiff, 4, littleEndian);
        if (ifdOffset < 8 || ifdOffset + 2 > tiff.Length)
            throw new InvalidDataException($"TIFF IFD offset {ifdOffset} is out of bounds.");

        // Read IFD entry count
        var entryCount = ReadU16(tiff, ifdOffset, littleEndian);
        var ifdEnd = ifdOffset + 2 + entryCount * 12;
        if (ifdEnd > tiff.Length)
            throw new InvalidDataException("TIFF IFD extends beyond end of file.");

        // ── Parse IFD tags ────────────────────────────────────────────────────
        int width = 0, height = 0;
        int bitsPerSample = 1;
        int compression = CompressionNone;
        int photometric = -1;
        long[]? stripOffsets = null;
        int samplesPerPixel = 1;
        long rowsPerStrip = int.MaxValue;
        long[]? stripByteCounts = null;
        int planarConfiguration = 1;
        int predictor = 1;
        int fillOrder = 1; // 1=MSB-first (default), 2=LSB-first
        ushort[]? colorMap = null;
        int extraSamples = 0; // 0=unspecified, 1=pre-multiplied alpha, 2=unassociated alpha

        for (var i = 0; i < entryCount; i++)
        {
            var entryBase = ifdOffset + 2 + i * 12;
            var tag = ReadU16(tiff, entryBase, littleEndian);
            var type = ReadU16(tiff, entryBase + 2, littleEndian);
            var count = (long)ReadU32(tiff, entryBase + 4, littleEndian);

            switch (tag)
            {
                case TagImageWidth:
                    width = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagImageLength:
                    height = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagBitsPerSample:
                    bitsPerSample = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagCompression:
                    compression = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagPhotometricInterpretation:
                    photometric = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagSamplesPerPixel:
                    samplesPerPixel = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagRowsPerStrip:
                    rowsPerStrip = (long)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagPlanarConfiguration:
                    planarConfiguration = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagFillOrder:
                    fillOrder = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagPredictor:
                    predictor = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagExtraSamples:
                    extraSamples = (int)ReadTagValue(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagStripOffsets:
                    stripOffsets = ReadTagArray(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagStripByteCounts:
                    stripByteCounts = ReadTagArray(tiff, entryBase, type, count, littleEndian);
                    break;
                case TagColorMap:
                    colorMap = ReadColorMap(tiff, entryBase, count, littleEndian);
                    break;
            }
        }

        // ── Validate required tags ────────────────────────────────────────────
        if (width <= 0)
            throw new InvalidDataException("TIFF ImageWidth is missing or zero.");
        if (height <= 0)
            throw new InvalidDataException("TIFF ImageLength is missing or zero.");
        if (photometric < 0)
            throw new InvalidDataException("TIFF PhotometricInterpretation tag is missing.");

        ImageLimits.ValidateDimensions("TIFF", width, height);

        // ── CCITT Group 4 (Compression=4): dispatch early before the 8/16 bitsPerSample gate.
        // Group 4 is bilevel (1-bit), so BitsPerSample=1 (or defaulted to 1) is correct.
        if (compression == CompressionCcittG4)
        {
            if (samplesPerPixel != 1)
                throw new NotSupportedException(
                    $"TIFF CCITT Group 4 requires SamplesPerPixel=1; found {samplesPerPixel}.");

            if (stripOffsets is null || stripOffsets.Length == 0)
                throw new InvalidDataException("TIFF is missing StripOffsets.");
            if (stripByteCounts is null || stripByteCounts.Length == 0)
                throw new InvalidDataException("TIFF is missing StripByteCounts.");

            if (stripOffsets.Length != 1)
                throw new NotSupportedException(
                    "Multi-strip CCITT Group 4 TIFF is not supported.");

            var g4Offset = (int)stripOffsets[0];
            var g4Length = (int)stripByteCounts[0];
            if (g4Offset < 0 || (long)g4Offset + g4Length > tiff.Length)
                throw new InvalidDataException(
                    $"TIFF CCITT G4 strip (offset={g4Offset}, length={g4Length}) extends beyond end of file.");

            var stripBytes = tiff[g4Offset..(g4Offset + g4Length)];

            // FillOrder 2 (LSB-first) requires bit-reversal to produce MSB-first bytes
            // expected by the CCITTFaxDecode filter. FillOrder 1 (or absent) — no change.
            if (fillOrder == 2)
            {
                var reversed = new byte[stripBytes.Length];
                for (var bi = 0; bi < stripBytes.Length; bi++)
                    reversed[bi] = BitReverse[stripBytes[bi]];
                stripBytes = reversed;
            }

            // Polarity: PDF CCITTFaxDecode default (BlackIs1=false) renders bit-0 as black.
            // TIFF photometric 0 = WhiteIsZero (bit 0 = white, i.e. PDF default) → blackIs1=false.
            // TIFF photometric 1 = BlackIsZero/MinIsBlack (bit 0 = black) → blackIs1=true.
            bool blackIs1 = photometric == 1;

            return CcittImageLoader.Build(
                stripBytes,
                columns: width,
                rows: height,
                k: -1,
                blackIs1: blackIs1,
                encodedByteAlign: false);
        }

        if (bitsPerSample != 8 && bitsPerSample != 16)
            throw new NotSupportedException(
                $"Only 8-bit and 16-bit TIFF are supported; found BitsPerSample={bitsPerSample}.");

        if (planarConfiguration != 1 && planarConfiguration != 2)
            throw new NotSupportedException(
                $"Only PlanarConfiguration 1 (chunky) or 2 (planar) TIFF is supported; found {planarConfiguration}.");

        if (compression == CompressionCcittG3Variants1D || compression == CompressionCcittG3Mixed)
            throw new NotSupportedException(
                $"TIFF Compression={compression} (CCITT Group 3) is not supported in v1.3. " +
                $"T4Options mapping is required and is out of scope.");

        if (compression == CompressionJpegOldStyle)
            throw new NotSupportedException(
                "TIFF Compression=6 (old-style JPEG) is not supported.");

        if (compression != CompressionNone && compression != CompressionPackBits &&
            compression != CompressionLzw && compression != CompressionJpegNewStyle)
            throw new NotSupportedException(
                $"TIFF Compression={compression} is not supported. " +
                $"Supported: 1 (None), 4 (CCITT Group 4), 5 (LZW), 7 (new-style JPEG), 32773 (PackBits).");

        if (photometric is not (0 or 1 or 2 or 3))
            throw new NotSupportedException(
                $"TIFF PhotometricInterpretation={photometric} is not supported.");

        if (photometric == 3 && colorMap is null)
            throw new InvalidDataException("TIFF Palette photometric requires a ColorMap tag.");

        if (bitsPerSample == 16 && photometric == 3)
            throw new NotSupportedException(
                "TIFF palette (photometric=3) with BitsPerSample=16 is not supported.");

        if (stripOffsets is null || stripOffsets.Length == 0)
            throw new InvalidDataException("TIFF is missing StripOffsets.");
        if (stripByteCounts is null || stripByteCounts.Length == 0)
            throw new InvalidDataException("TIFF is missing StripByteCounts.");

        // Clamp rowsPerStrip to avoid very large allocation
        if (rowsPerStrip <= 0)
            throw new InvalidDataException("TIFF RowsPerStrip must be positive.");
        if (rowsPerStrip > height)
            rowsPerStrip = height;

        // ── New-style JPEG (Compression=7): single-strip passthrough ─────────
        if (compression == CompressionJpegNewStyle)
        {
            if (stripOffsets.Length != 1)
                throw new NotSupportedException(
                    "TIFF Compression=7 (new-style JPEG) is only supported for single-strip images.");
            var jpegOffset = (int)stripOffsets[0];
            var jpegLength = (int)stripByteCounts[0];
            if (jpegOffset < 0 || (long)jpegOffset + jpegLength > tiff.Length)
                throw new InvalidDataException(
                    $"TIFF JPEG strip (offset={jpegOffset}, length={jpegLength}) extends beyond end of file.");
            var jpegBytes = tiff[jpegOffset..(jpegOffset + jpegLength)];
            return JpegImageLoader.Load(jpegBytes);
        }

        // After JPEG dispatch, validate strip count consistency.
        if (stripOffsets.Length != stripByteCounts.Length)
            throw new InvalidDataException(
                $"TIFF StripOffsets count ({stripOffsets.Length}) != StripByteCounts count ({stripByteCounts.Length}).");

        // Determine colour samples per pixel (excluding extra alpha sample)
        int colorSamples = samplesPerPixel;
        bool hasAlpha = false;
        if (samplesPerPixel == 4 && photometric == 2)
        {
            hasAlpha = true;
            colorSamples = 3;
        }

        // Bytes per sample (1 for 8-bit, 2 for 16-bit)
        int bytesPerSample = bitsPerSample == 16 ? 2 : 1;

        // ── Decode strips into a top-to-bottom pixel buffer ───────────────────
        // For chunky (planar=1): strips contain all samples interleaved.
        // For planar (planar=2): strips are organized as N planes, each plane
        // containing all rows for one sample channel.
        //
        // rawRowBytes: bytes per row in the final chunky buffer.
        var rawRowBytes = width * samplesPerPixel * bytesPerSample;
        var rawBuffer = new byte[(long)height * rawRowBytes];

        if (planarConfiguration == 1)
        {
            // Chunky: standard strip-by-strip decode.
            DecodeChunkyStrips(tiff, stripOffsets, stripByteCounts, compression, predictor,
                width, height, samplesPerPixel, bytesPerSample, rawRowBytes,
                (int)rowsPerStrip, rawBuffer);
        }
        else
        {
            // Planar (PlanarConfiguration=2): strips are arranged as N blocks of
            // (stripsPerImage) strips, one block per sample plane.
            // StripOffsets has (stripsPerImage * samplesPerPixel) entries:
            //   plane 0 strips: indices 0..stripsPerImage-1
            //   plane 1 strips: indices stripsPerImage..2*stripsPerImage-1
            //   etc.
            DecodePlanarStrips(tiff, stripOffsets, stripByteCounts, compression, predictor,
                width, height, samplesPerPixel, bytesPerSample, rawRowBytes,
                (int)rowsPerStrip, rawBuffer);
        }

        // ── Endian-normalize 16-bit samples ───────────────────────────────────
        // PDF expects 16-bit samples big-endian. TIFF II stores them little-endian.
        // When little-endian, byte-swap each 16-bit sample in rawBuffer.
        if (bitsPerSample == 16 && littleEndian)
            ByteSwap16Samples(rawBuffer);

        // ── Build colour (and optional alpha) output ──────────────────────────
        return photometric switch
        {
            0 or 1 => BuildGreyscale(rawBuffer, width, height, photometric == 0, bitsPerSample, options),
            2 => BuildRgb(rawBuffer, width, height, hasAlpha, bitsPerSample, options),
            3 => BuildPalette(rawBuffer, width, height, colorMap!),
            _ => throw new NotSupportedException(
                $"TIFF PhotometricInterpretation={photometric} is not supported.")
        };
    }

    // ── Strip decode helpers ──────────────────────────────────────────────────

    private static void DecodeChunkyStrips(
        byte[] tiff,
        long[] stripOffsets, long[] stripByteCounts,
        int compression, int predictor,
        int width, int height, int samplesPerPixel, int bytesPerSample, int rawRowBytes,
        int rowsPerStrip, byte[] rawBuffer)
    {
        var stripIndex = 0;
        var rowsDone = 0;
        while (rowsDone < height && stripIndex < stripOffsets.Length)
        {
            var stripOffset = (int)stripOffsets[stripIndex];
            var stripByteCount = (int)stripByteCounts[stripIndex];
            var stripRowCount = Math.Min(rowsPerStrip, height - rowsDone);
            var expectedStripBytes = stripRowCount * rawRowBytes;

            if (stripOffset < 0 || (long)stripOffset + stripByteCount > tiff.Length)
                throw new InvalidDataException(
                    $"TIFF strip {stripIndex} data (offset={stripOffset}, length={stripByteCount}) extends beyond end of file.");

            var stripData = DecodeStrip(tiff, stripOffset, stripByteCount, compression, expectedStripBytes);

            if (predictor == 2)
                ApplyHorizontalPredictor(stripData, stripRowCount, width, samplesPerPixel, bytesPerSample);
            else if (predictor != 1)
                throw new NotSupportedException(
                    $"TIFF Predictor={predictor} is not supported. Supported: 1 (None), 2 (Horizontal Differencing).");

            Buffer.BlockCopy(stripData, 0, rawBuffer, rowsDone * rawRowBytes, expectedStripBytes);
            rowsDone += stripRowCount;
            stripIndex++;
        }

        if (rowsDone < height)
            throw new InvalidDataException(
                $"TIFF has only {rowsDone} decoded rows but ImageLength={height}.");
    }

    private static void DecodePlanarStrips(
        byte[] tiff,
        long[] stripOffsets, long[] stripByteCounts,
        int compression, int predictor,
        int width, int height, int samplesPerPixel, int bytesPerSample, int rawRowBytes,
        int rowsPerStrip, byte[] rawBuffer)
    {
        // Number of strips per plane.
        var stripsPerImage = (height + rowsPerStrip - 1) / rowsPerStrip;

        if (stripOffsets.Length != stripsPerImage * samplesPerPixel)
            throw new InvalidDataException(
                $"TIFF PlanarConfiguration=2: expected {stripsPerImage * samplesPerPixel} strips " +
                $"({stripsPerImage} per plane × {samplesPerPixel} planes), " +
                $"but StripOffsets has {stripOffsets.Length} entries.");

        // Row bytes for a single sample plane.
        var planeRowBytes = width * bytesPerSample;

        // Plane buffer: decoded rows for a single sample across all height rows.
        var planeBuffer = new byte[(long)height * planeRowBytes];

        for (var plane = 0; plane < samplesPerPixel; plane++)
        {
            var rowsDone = 0;
            for (var s = 0; s < stripsPerImage; s++)
            {
                var stripIndex = plane * stripsPerImage + s;
                var stripOffset = (int)stripOffsets[stripIndex];
                var stripByteCount = (int)stripByteCounts[stripIndex];
                var stripRowCount = Math.Min(rowsPerStrip, height - rowsDone);
                var expectedStripBytes = stripRowCount * planeRowBytes;

                if (stripOffset < 0 || (long)stripOffset + stripByteCount > tiff.Length)
                    throw new InvalidDataException(
                        $"TIFF planar strip {stripIndex} data (offset={stripOffset}, length={stripByteCount}) extends beyond end of file.");

                var stripData = DecodeStrip(tiff, stripOffset, stripByteCount, compression, expectedStripBytes);

                if (predictor == 2)
                    ApplyHorizontalPredictor(stripData, stripRowCount, width, 1, bytesPerSample);
                else if (predictor != 1)
                    throw new NotSupportedException(
                        $"TIFF Predictor={predictor} is not supported. Supported: 1 (None), 2 (Horizontal Differencing).");

                Buffer.BlockCopy(stripData, 0, planeBuffer, rowsDone * planeRowBytes, expectedStripBytes);
                rowsDone += stripRowCount;
            }

            if (rowsDone < height)
                throw new InvalidDataException(
                    $"TIFF plane {plane} has only {rowsDone} decoded rows but ImageLength={height}.");

            // Interleave plane bytes into rawBuffer (chunky layout).
            // For each pixel, the plane byte(s) sit at position: pixelIndex * samplesPerPixel * bytesPerSample + plane * bytesPerSample
            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    var srcBase = row * planeRowBytes + col * bytesPerSample;
                    var dstBase = row * rawRowBytes + (col * samplesPerPixel + plane) * bytesPerSample;
                    for (var b = 0; b < bytesPerSample; b++)
                        rawBuffer[dstBase + b] = planeBuffer[srcBase + b];
                }
            }
        }
    }

    private static byte[] DecodeStrip(byte[] tiff, int stripOffset, int stripByteCount, int compression, int expectedBytes)
    {
        return compression switch
        {
            CompressionNone => DecodeStripNone(tiff, stripOffset, stripByteCount, expectedBytes),
            CompressionPackBits => DecodePackBits(tiff, stripOffset, stripByteCount, expectedBytes),
            CompressionLzw => TiffLzwDecoder.Decode(tiff, stripOffset, stripByteCount, expectedBytes),
            _ => throw new NotSupportedException($"TIFF Compression={compression} is not supported.")
        };
    }

    private static byte[] DecodeStripNone(byte[] tiff, int stripOffset, int stripByteCount, int expectedBytes)
    {
        if (stripByteCount < expectedBytes)
            throw new InvalidDataException(
                $"TIFF uncompressed strip is too small ({stripByteCount} bytes; expected {expectedBytes}).");
        return tiff[stripOffset..(stripOffset + expectedBytes)];
    }

    // ── Endian swap for 16-bit little-endian samples ─────────────────────────

    private static void ByteSwap16Samples(byte[] data)
    {
        for (var i = 0; i < data.Length - 1; i += 2)
        {
            var tmp = data[i];
            data[i] = data[i + 1];
            data[i + 1] = tmp;
        }
    }

    // ── Photometric output builders ───────────────────────────────────────────

    private static PdfImageXObject BuildGreyscale(
        byte[] raw, int width, int height, bool invertWhiteIsZero,
        int bitsPerSample, ImageLoadOptions options)
    {
        if (bitsPerSample == 16)
        {
            // 16-bit greyscale.
            byte[] grey16;
            if (invertWhiteIsZero)
            {
                grey16 = new byte[raw.Length];
                // Each sample is 2 bytes big-endian (after endian normalization).
                // Inversion: 0xFFFF - sample.
                for (var i = 0; i < raw.Length; i += 2)
                {
                    int sample = (raw[i] << 8) | raw[i + 1];
                    int inverted = 0xFFFF - sample;
                    grey16[i] = (byte)(inverted >> 8);
                    grey16[i + 1] = (byte)inverted;
                }
            }
            else
            {
                grey16 = raw;
            }

            if (options.BitDepth == ImageBitDepth.ReduceToEight)
            {
                var grey8 = Downsample16(grey16);
                return new PdfImageXObject(width, height, grey8, PdfName.FlateDecode, ImageColorSpace.DeviceGray, 8);
            }
            return new PdfImageXObject(width, height, grey16, PdfName.FlateDecode, ImageColorSpace.DeviceGray, 16);
        }

        // 8-bit greyscale (original path — unchanged).
        var grey = invertWhiteIsZero ? new byte[raw.Length] : raw;
        if (invertWhiteIsZero)
            for (var i = 0; i < raw.Length; i++)
                grey[i] = (byte)(255 - raw[i]);
        return new PdfImageXObject(width, height, grey, PdfName.FlateDecode, ImageColorSpace.DeviceGray, 8);
    }

    private static PdfImageXObject BuildRgb(
        byte[] raw, int width, int height, bool hasAlpha,
        int bitsPerSample, ImageLoadOptions options)
    {
        var pixelCount = width * height;

        if (bitsPerSample == 16)
        {
            // 16-bit RGB or RGBA.
            int colorSampleBytes = 3 * 2; // 3 channels × 2 bytes
            int alphaSampleBytes = 2;
            int totalSampleBytes = (hasAlpha ? 4 : 3) * 2;

            byte[] rgb16;
            byte[]? alpha16 = null;
            bool hasNonOpaqueAlpha = false;

            if (hasAlpha)
            {
                rgb16 = new byte[pixelCount * colorSampleBytes];
                alpha16 = new byte[pixelCount * alphaSampleBytes];
                for (var i = 0; i < pixelCount; i++)
                {
                    // Copy RGB (3 channels × 2 bytes each)
                    for (var c = 0; c < 3; c++)
                    {
                        rgb16[i * colorSampleBytes + c * 2] = raw[i * totalSampleBytes + c * 2];
                        rgb16[i * colorSampleBytes + c * 2 + 1] = raw[i * totalSampleBytes + c * 2 + 1];
                    }
                    // Copy alpha (2 bytes)
                    alpha16[i * 2] = raw[i * totalSampleBytes + 6];
                    alpha16[i * 2 + 1] = raw[i * totalSampleBytes + 7];
                    // "Fully opaque" check: big-endian 0xFFFF = bytes FF FF
                    if (alpha16[i * 2] != 0xFF || alpha16[i * 2 + 1] != 0xFF)
                        hasNonOpaqueAlpha = true;
                }
            }
            else
            {
                rgb16 = raw;
            }

            if (options.BitDepth == ImageBitDepth.ReduceToEight)
            {
                var rgb8 = Downsample16(rgb16);
                PdfStream? sMask8 = null;
                if (hasAlpha && hasNonOpaqueAlpha)
                    sMask8 = new PdfStream(Downsample16(alpha16!));
                else if (hasAlpha && !hasNonOpaqueAlpha)
                    sMask8 = null;
                return new PdfImageXObject(width, height, rgb8, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8, sMask8, 8);
            }
            else
            {
                PdfStream? sMask16 = null;
                if (hasAlpha && hasNonOpaqueAlpha)
                    sMask16 = new PdfStream(alpha16!);
                return new PdfImageXObject(width, height, rgb16, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 16, sMask16, 16);
            }
        }

        // 8-bit RGB (original path — unchanged).
        if (!hasAlpha)
            return new PdfImageXObject(width, height, raw, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);

        var rgbOut = new byte[pixelCount * 3];
        var alphaOut = new byte[pixelCount];
        var hasNonOpaque = false;
        for (var i = 0; i < pixelCount; i++)
        {
            rgbOut[i * 3] = raw[i * 4];
            rgbOut[i * 3 + 1] = raw[i * 4 + 1];
            rgbOut[i * 3 + 2] = raw[i * 4 + 2];
            var a = raw[i * 4 + 3];
            alphaOut[i] = a;
            if (a != 255) hasNonOpaque = true;
        }
        PdfStream? sMask = hasNonOpaque ? new PdfStream(alphaOut) : null;
        return new PdfImageXObject(width, height, rgbOut, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8, sMask);
    }

    private static PdfImageXObject BuildPalette(
        byte[] raw, int width, int height, ushort[] colorMap)
    {
        // ColorMap layout: 3 * 2^bps entries of 16-bit values (range 0–65535).
        // First 2^bps entries are red, next 2^bps are green, last 2^bps are blue.
        // Scale 0–65535 → 0–255 by taking the high byte.
        var paletteSize = colorMap.Length / 3;
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            var idx = raw[i];
            if (idx >= paletteSize) idx = 0; // out-of-range index — map to first entry
            rgb[i * 3] = (byte)(colorMap[idx] >> 8);                        // R
            rgb[i * 3 + 1] = (byte)(colorMap[idx + paletteSize] >> 8);      // G
            rgb[i * 3 + 2] = (byte)(colorMap[idx + 2 * paletteSize] >> 8);  // B
        }
        return new PdfImageXObject(width, height, rgb, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);
    }

    // ── Horizontal differencing predictor (TIFF Predictor=2) ─────────────────

    /// <summary>
    /// Applies the TIFF horizontal differencing predictor to decode a strip.
    /// Works for both 8-bit (bytesPerSample=1) and 16-bit (bytesPerSample=2) samples.
    /// Differencing is applied per sample (not per byte) for 16-bit.
    /// </summary>
    private static void ApplyHorizontalPredictor(
        byte[] data, int rows, int width, int samplesPerPixel, int bytesPerSample)
    {
        var sampleStride = samplesPerPixel * bytesPerSample;
        var rowBytes = width * sampleStride;

        for (var row = 0; row < rows; row++)
        {
            var rowBase = row * rowBytes;
            // Start from the second pixel (skip the first pixel which is stored as-is).
            for (var x = sampleStride; x < rowBytes; x += bytesPerSample)
            {
                if (bytesPerSample == 1)
                {
                    data[rowBase + x] = (byte)(data[rowBase + x] + data[rowBase + x - sampleStride]);
                }
                else
                {
                    // 16-bit: samples are big-endian after normalization.
                    // Reconstruct as unsigned, add, store back big-endian.
                    int cur = (data[rowBase + x] << 8) | data[rowBase + x + 1];
                    int prev = (data[rowBase + x - sampleStride] << 8) | data[rowBase + x - sampleStride + 1];
                    int sum = (cur + prev) & 0xFFFF;
                    data[rowBase + x] = (byte)(sum >> 8);
                    data[rowBase + x + 1] = (byte)sum;
                }
            }
        }
    }

    // ── PackBits (Compression=32773) ──────────────────────────────────────────

    private static byte[] DecodePackBits(byte[] src, int offset, int compressedLength, int expectedOutput)
    {
        var result = new byte[expectedOutput];
        var outPos = 0;
        var end = offset + compressedLength;
        var pos = offset;

        while (pos < end && outPos < expectedOutput)
        {
            var header = (sbyte)src[pos++];

            if (header >= 0)
            {
                // Literal run: copy (header + 1) bytes literally
                var count = header + 1;
                if (pos + count > end)
                    throw new InvalidDataException("PackBits literal run extends beyond compressed data.");
                for (var i = 0; i < count && outPos < expectedOutput; i++)
                    result[outPos++] = src[pos++];
            }
            else if (header != -128)
            {
                // Replicate run: repeat next byte (-header + 1) times
                var count = -header + 1;
                if (pos >= end)
                    throw new InvalidDataException("PackBits replicate run is missing the byte to repeat.");
                var value = src[pos++];
                for (var i = 0; i < count && outPos < expectedOutput; i++)
                    result[outPos++] = value;
            }
            // header == -128 (0x80): NOP — skip
        }

        return result;
    }

    // ── 16-bit → 8-bit downsampling ───────────────────────────────────────────

    private static byte[] Downsample16(byte[] data)
    {
        // After big-endian normalization: high byte is data[i*2], low byte is data[i*2+1].
        // ReduceToEight takes the high byte (MSB).
        var result = new byte[data.Length / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = data[i * 2];
        return result;
    }

    // ── IFD value readers ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads a single scalar value from an IFD entry (tag type SHORT or LONG, count=1).
    /// If the value fits in 4 bytes it is in the value/offset field directly;
    /// otherwise it is at the offset stored in that field.
    /// </summary>
    private static long ReadTagValue(byte[] data, int entryBase, ushort type, long count, bool le)
    {
        // type: 3=SHORT(2), 4=LONG(4), 1=BYTE(1), 2=ASCII(1), 5=RATIONAL(8), etc.
        var typeSize = type < TypeSizes.Length ? TypeSizes[type] : 1;
        var totalBytes = typeSize * count;

        if (totalBytes <= 4)
        {
            // Value is packed directly into the value/offset field (bytes 8–11 of entry)
            var valueField = entryBase + 8;
            return type switch
            {
                1 => data[valueField],           // BYTE
                3 => ReadU16(data, valueField, le), // SHORT
                4 => ReadU32(data, valueField, le), // LONG
                _ => data[valueField]
            };
        }
        else
        {
            // Value is at the offset stored in the value/offset field
            var offset = (int)ReadU32(data, entryBase + 8, le);
            if (offset + typeSize > data.Length)
                throw new InvalidDataException(
                    $"TIFF IFD value offset {offset} for type {type} is out of bounds.");
            return type switch
            {
                1 => data[offset],
                3 => ReadU16(data, offset, le),
                4 => ReadU32(data, offset, le),
                _ => data[offset]
            };
        }
    }

    /// <summary>
    /// Reads an array of values (for StripOffsets, StripByteCounts which may have count > 1).
    /// </summary>
    private static long[] ReadTagArray(byte[] data, int entryBase, ushort type, long count, bool le)
    {
        var typeSize = type < TypeSizes.Length ? TypeSizes[type] : 1;
        var totalBytes = typeSize * count;
        var result = new long[count];

        int baseOffset;
        if (totalBytes <= 4)
        {
            // Packed directly in value/offset field
            baseOffset = entryBase + 8;
        }
        else
        {
            baseOffset = (int)ReadU32(data, entryBase + 8, le);
            if ((long)baseOffset + totalBytes > data.Length)
                throw new InvalidDataException(
                    $"TIFF tag array at offset {baseOffset} (length {totalBytes}) extends beyond end of file.");
        }

        for (var i = 0; i < count; i++)
        {
            var elemOffset = baseOffset + i * typeSize;
            result[i] = type switch
            {
                1 => data[elemOffset],
                3 => ReadU16(data, elemOffset, le),
                4 => ReadU32(data, elemOffset, le),
                _ => data[elemOffset]
            };
        }
        return result;
    }

    /// <summary>
    /// Reads the ColorMap tag as an array of ushort values.
    /// ColorMap for 8-bit palette has 3 * 256 = 768 ushort entries.
    /// </summary>
    private static ushort[] ReadColorMap(byte[] data, int entryBase, long count, bool le)
    {
        var typeSize = 2; // SHORT
        var totalBytes = typeSize * count;
        var offset = (int)ReadU32(data, entryBase + 8, le);

        if ((long)offset + totalBytes > data.Length)
            throw new InvalidDataException(
                $"TIFF ColorMap at offset {offset} (length {totalBytes}) extends beyond end of file.");

        var result = new ushort[count];
        for (var i = 0; i < count; i++)
            result[i] = ReadU16(data, offset + i * 2, le);
        return result;
    }

    // ── Endian-aware primitive readers ───────────────────────────────────────

    private static ushort ReadU16(byte[] data, int offset, bool le)
    {
        if (offset + 2 > data.Length)
            throw new InvalidDataException($"TIFF read U16 at offset {offset} is out of bounds.");
        return le
            ? (ushort)(data[offset] | (data[offset + 1] << 8))
            : (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadU32(byte[] data, int offset, bool le)
    {
        if (offset + 4 > data.Length)
            throw new InvalidDataException($"TIFF read U32 at offset {offset} is out of bounds.");
        return le
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }
}
