// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Decodes a baseline TIFF 6.0 file and produces a FlateDecode Image XObject.
///
/// Supported:
///   • Byte order: II (little-endian) and MM (big-endian).
///   • Compression: 1 (None/Uncompressed) and 32773 (PackBits RLE).
///   • Photometric: 0 (WhiteIsZero greyscale — inverted), 1 (BlackIsZero greyscale),
///     2 (RGB), 3 (Palette/Indexed — ColorMap expanded to DeviceRGB).
///   • BitsPerSample: 8. SamplesPerPixel: 1 (grey/palette), 3 (RGB), 4 (RGB+alpha → /SMask).
///   • PlanarConfiguration: 1 (chunky) only.
///   • Multiple strips (StripOffsets / StripByteCounts / RowsPerStrip).
///
/// Rejected (throws NotSupportedException): LZW (5), JPEG (6), and all other compression values;
/// PlanarConfiguration 2 (planar); BitsPerSample other than 8; unsupported photometric values.
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
    private const ushort TagExtraSamples = 338;

    // Compression constants
    private const int CompressionNone = 1;
    private const int CompressionPackBits = 32773;
    private const int CompressionLzw = 5;

    /// <summary>Decodes baseline TIFF file bytes into a FlateDecode Image XObject.</summary>
    public static PdfImageXObject Load(byte[] tiff)
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

        if (bitsPerSample != 8)
            throw new NotSupportedException(
                $"Only 8-bit-per-sample TIFF is supported; found BitsPerSample={bitsPerSample}.");

        if (planarConfiguration != 1)
            throw new NotSupportedException(
                $"Only chunky (PlanarConfiguration=1) TIFF is supported; found {planarConfiguration}.");

        if (compression == CompressionLzw)
            throw new NotSupportedException("LZW (Compression=5) TIFF is not supported.");

        if (compression != CompressionNone && compression != CompressionPackBits)
            throw new NotSupportedException(
                $"TIFF Compression={compression} is not supported. Supported: 1 (None), 32773 (PackBits).");

        if (photometric is not (0 or 1 or 2 or 3))
            throw new NotSupportedException(
                $"TIFF PhotometricInterpretation={photometric} is not supported.");

        if (photometric == 3 && colorMap is null)
            throw new InvalidDataException("TIFF Palette photometric requires a ColorMap tag.");

        if (stripOffsets is null || stripOffsets.Length == 0)
            throw new InvalidDataException("TIFF is missing StripOffsets.");
        if (stripByteCounts is null || stripByteCounts.Length == 0)
            throw new InvalidDataException("TIFF is missing StripByteCounts.");
        if (stripOffsets.Length != stripByteCounts.Length)
            throw new InvalidDataException(
                $"TIFF StripOffsets count ({stripOffsets.Length}) != StripByteCounts count ({stripByteCounts.Length}).");

        // Clamp rowsPerStrip to avoid very large allocation
        if (rowsPerStrip <= 0)
            throw new InvalidDataException("TIFF RowsPerStrip must be positive.");
        if (rowsPerStrip > height)
            rowsPerStrip = height;

        // Determine colour samples per pixel (excluding extra alpha sample)
        int colorSamples = samplesPerPixel;
        bool hasAlpha = false;
        if (samplesPerPixel == 4 && (photometric == 2))
        {
            hasAlpha = true;
            colorSamples = 3;
        }

        // ── Decode strips into a top-to-bottom pixel buffer ───────────────────
        // bytesPerRow: for palette, 1 index per pixel; for grey, 1 sample; for RGB, 3; for RGBA, 4.
        var bytesPerPixelRaw = samplesPerPixel; // raw bytes including extra samples
        var rawRowBytes = width * bytesPerPixelRaw;
        var rawBuffer = new byte[(long)height * rawRowBytes];

        var stripIndex = 0;
        var rowsDone = 0;
        while (rowsDone < height && stripIndex < stripOffsets.Length)
        {
            var stripOffset = (int)stripOffsets[stripIndex];
            var stripByteCount = (int)stripByteCounts[stripIndex];

            if (stripOffset < 0 || (long)stripOffset + stripByteCount > tiff.Length)
                throw new InvalidDataException(
                    $"TIFF strip {stripIndex} data (offset={stripOffset}, length={stripByteCount}) extends beyond end of file.");

            var stripRowCount = (int)Math.Min(rowsPerStrip, height - rowsDone);
            var expectedStripBytes = stripRowCount * rawRowBytes;

            byte[] stripData;
            if (compression == CompressionNone)
            {
                if (stripByteCount < expectedStripBytes)
                    throw new InvalidDataException(
                        $"TIFF strip {stripIndex} is too small ({stripByteCount} bytes; expected {expectedStripBytes}).");
                stripData = tiff[stripOffset..(stripOffset + expectedStripBytes)];
            }
            else // PackBits
            {
                stripData = DecodePackBits(tiff, stripOffset, stripByteCount, expectedStripBytes);
            }

            // Apply horizontal predictor if set (rare but valid for uncompressed)
            if (predictor == 2)
                ApplyHorizontalPredictor(stripData, stripRowCount, width, bytesPerPixelRaw);

            Buffer.BlockCopy(stripData, 0, rawBuffer, rowsDone * rawRowBytes, expectedStripBytes);
            rowsDone += stripRowCount;
            stripIndex++;
        }

        if (rowsDone < height)
            throw new InvalidDataException(
                $"TIFF has only {rowsDone} decoded rows but ImageLength={height}.");

        // ── Build colour (and optional alpha) output ──────────────────────────
        return photometric switch
        {
            0 or 1 => BuildGreyscale(rawBuffer, width, height, photometric == 0),
            2 => BuildRgb(rawBuffer, width, height, hasAlpha),
            3 => BuildPalette(rawBuffer, width, height, colorMap!),
            _ => throw new NotSupportedException(
                $"TIFF PhotometricInterpretation={photometric} is not supported.")
        };
    }

    // ── Photometric output builders ───────────────────────────────────────────

    private static PdfImageXObject BuildGreyscale(
        byte[] raw, int width, int height, bool invertWhiteIsZero)
    {
        var grey = invertWhiteIsZero ? new byte[raw.Length] : raw;
        if (invertWhiteIsZero)
            for (var i = 0; i < raw.Length; i++)
                grey[i] = (byte)(255 - raw[i]);
        return new PdfImageXObject(width, height, grey, PdfName.FlateDecode, ImageColorSpace.DeviceGray, 8);
    }

    private static PdfImageXObject BuildRgb(byte[] raw, int width, int height, bool hasAlpha)
    {
        var pixelCount = width * height;
        if (!hasAlpha)
            return new PdfImageXObject(width, height, raw, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);

        var rgb = new byte[pixelCount * 3];
        var alpha = new byte[pixelCount];
        var hasNonOpaqueAlpha = false;
        for (var i = 0; i < pixelCount; i++)
        {
            rgb[i * 3] = raw[i * 4];
            rgb[i * 3 + 1] = raw[i * 4 + 1];
            rgb[i * 3 + 2] = raw[i * 4 + 2];
            var a = raw[i * 4 + 3];
            alpha[i] = a;
            if (a != 255) hasNonOpaqueAlpha = true;
        }
        PdfStream? sMask = hasNonOpaqueAlpha ? new PdfStream(alpha) : null;
        return new PdfImageXObject(width, height, rgb, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8, sMask);
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
            rgb[i * 3] = (byte)(colorMap[idx] >> 8);                      // R
            rgb[i * 3 + 1] = (byte)(colorMap[idx + paletteSize] >> 8);    // G
            rgb[i * 3 + 2] = (byte)(colorMap[idx + 2 * paletteSize] >> 8); // B
        }
        return new PdfImageXObject(width, height, rgb, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8);
    }

    // ── Horizontal differencing predictor (TIFF Predictor=2) ─────────────────

    private static void ApplyHorizontalPredictor(byte[] data, int rows, int width, int samplesPerPixel)
    {
        var rowBytes = width * samplesPerPixel;
        for (var row = 0; row < rows; row++)
        {
            var rowBase = row * rowBytes;
            for (var x = samplesPerPixel; x < rowBytes; x++)
                data[rowBase + x] = (byte)(data[rowBase + x] + data[rowBase + x - samplesPerPixel]);
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
                1 => data[valueField],          // BYTE
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
