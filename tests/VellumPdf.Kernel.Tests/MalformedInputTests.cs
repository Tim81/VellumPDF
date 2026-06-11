// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Fonts.Sfnt;
using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Untrusted-input robustness corpus (issue #4). Feeds truncated, oversized, and otherwise
/// hostile font and image bytes to every loader and asserts a clean
/// <see cref="InvalidDataException"/>/<see cref="NotSupportedException"/> — never an
/// <see cref="IndexOutOfRangeException"/>, an out-of-memory, or a hang.
///
/// All inputs are synthesised in-memory. Parsers are reached directly via InternalsVisibleTo.
/// Tests that exercise a denial-of-service guard (the cmap segment budget and the PNG zlib-bomb
/// cap) are expected to return promptly; completing at all is the proof.
/// </summary>
public sealed class MalformedInputTests
{
    // ── Fonts: sfnt directory ────────────────────────────────────────────────

    [Fact]
    public void SfntFont_truncatedHeader_throwsInvalidDataException()
    {
        // Only 4 bytes: reading numTables (a u16 at offset 4) runs past the buffer.
        Assert.Throws<InvalidDataException>(() => SfntFont.Parse(new byte[] { 0x00, 0x01, 0x00, 0x00 }));
    }

    [Fact]
    public void SfntFont_tableDirectoryExceedsFile_throwsInvalidDataException()
    {
        var font = new byte[12];
        WriteU32Be(font, 0, 0x00010000);
        WriteU16Be(font, 4, 100); // claims 100 tables; the 12-byte file cannot hold the directory
        Assert.Throws<InvalidDataException>(() => SfntFont.Parse(font));
    }

    [Fact]
    public void SfntFont_tableRecordOutOfBounds_throwsInvalidDataException()
    {
        var font = new byte[12 + 16]; // header + one record
        WriteU32Be(font, 0, 0x00010000);
        WriteU16Be(font, 4, 1);
        Encoding.ASCII.GetBytes("glyf").CopyTo(font, 12); // tag
        WriteU32Be(font, 12 + 8, 99_999);                 // offset — far past the file
        WriteU32Be(font, 12 + 12, 10);                    // length
        Assert.Throws<InvalidDataException>(() => SfntFont.Parse(font));
    }

    // ── Fonts: cmap format-4 denial-of-service ───────────────────────────────

    [Fact(Timeout = 10_000)]
    public void CmapFormat4_overlappingWideSegments_throwsAndDoesNotHang()
    {
        // Three segments each covering the whole BMP (0x0000-0xFFFE) sum to ~196k iterations,
        // past the parser's code-point budget. Without the budget this is a multi-billion
        // iteration hang once many such segments are present.
        var cmap = BuildCmapFormat4(
        [
            (0xFFFE, 0x0000, 1, 0),
            (0xFFFE, 0x0000, 1, 0),
            (0xFFFE, 0x0000, 1, 0),
            (0xFFFF, 0xFFFF, 1, 0), // required final sentinel segment
        ]);
        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));
        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
    }

    // ── Fonts: glyf/loca ─────────────────────────────────────────────────────

    [Fact]
    public void GlyfSubsetter_corruptLocaOffset_throwsInvalidDataException()
    {
        // loca claims glyph 0 spans [0, 0x10000000) but the glyf table is only 8 bytes.
        var font = SfntFont.Parse(BuildFont(
            ("head", BuildHead(indexToLocFormat: 1)),
            ("maxp", BuildMaxp(numGlyphs: 2)),
            ("loca", BuildLongLoca(0, 0x10000000, 0x10000000)),
            ("glyf", new byte[8])));
        var subsetter = new GlyfSubsetter(font);
        Assert.Throws<InvalidDataException>(() => subsetter.GetGlyphData(0).ToArray());
    }

    // ── Images: JPEG ─────────────────────────────────────────────────────────

    [Fact]
    public void Jpeg_missingSoiMarker_throwsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => JpegImageLoader.Load(new byte[] { 0x00, 0x01, 0x02, 0x03 }));
    }

    [Fact]
    public void Jpeg_truncatedSofSegment_throwsInvalidDataException()
    {
        // SOI + SOF0 marker + a length claiming a full frame header, but the bytes stop short.
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08 };
        Assert.Throws<InvalidDataException>(() => JpegImageLoader.Load(jpeg));
    }

    // ── Images: PNG ──────────────────────────────────────────────────────────

    [Fact]
    public void Png_truncatedAfterSignature_throwsInvalidDataException()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.Throws<InvalidDataException>(() => PngImageLoader.Load(png));
    }

    [Fact]
    public void Png_invalidBitDepth_throwsInvalidDataException()
    {
        var png = BuildPng(4, 4, bitDepth: 7, colorType: 2, idat: ZlibCompress(new byte[64]));
        Assert.Throws<InvalidDataException>(() => PngImageLoader.Load(png));
    }

    [Fact(Timeout = 10_000)]
    public void Png_zlibBomb_throwsAndDoesNotExhaustMemory()
    {
        // A 1×1 image whose IDAT decompresses to ~4 MB — far beyond the dimensions' expected size.
        var bomb = ZlibCompress(new byte[4_000_000]);
        var png = BuildPng(1, 1, bitDepth: 8, colorType: 2, idat: bomb);
        Assert.Throws<InvalidDataException>(() => PngImageLoader.Load(png));
    }

    [Fact]
    public void Png_truncatedImageData_throwsInvalidDataException()
    {
        // Valid 8×8 RGB IHDR, but the IDAT decompresses to far fewer bytes than the
        // height×(rowBytes+1) the scanline unfilter requires.
        var png = BuildPng(8, 8, bitDepth: 8, colorType: 2, idat: ZlibCompress(new byte[10]));
        Assert.Throws<InvalidDataException>(() => PngImageLoader.Load(png));
    }

    // ── Images: BMP ──────────────────────────────────────────────────────────

    [Fact]
    public void Bmp_truncatedPixelData_throwsInvalidDataException()
    {
        // Header declares a 100×100 24-bit image, but only a few pixel bytes follow.
        var bmp = BuildBmpHeader(width: 100, height: 100, bitCount: 24, pixelOffset: 54, totalSize: 60);
        Assert.Throws<InvalidDataException>(() => BmpImageLoader.Load(bmp));
    }

    // ── Images: GIF ──────────────────────────────────────────────────────────

    [Fact]
    public void Gif_absurdDimensions_throwsInvalidDataException()
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        WriteU16Le(ms, 1); WriteU16Le(ms, 1);          // logical screen size
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); // packed (no global table), bg, aspect
        ms.WriteByte(0x2C);                             // image separator
        WriteU16Le(ms, 0); WriteU16Le(ms, 0);          // left, top
        WriteU16Le(ms, 65535); WriteU16Le(ms, 65535);  // width, height (overflows Int32 when multiplied)
        ms.WriteByte(0);                                // packed (no local table)
        Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(ms.ToArray()));
    }

    // ── Images: TIFF-LZW hardening ───────────────────────────────────────────

    /// <summary>
    /// A valid LZW-compressed TIFF whose strip bytes are truncated mid-stream.
    /// The decoder hits end-of-input before producing the expected number of output bytes,
    /// so the final outIdx != expectedOutput check fires → InvalidDataException.
    /// </summary>
    [Fact]
    public void TiffLzw_truncatedStrip_throwsInvalidDataException()
    {
        // Build a valid 4×1 greyscale LZW TIFF, then cut the strip in half.
        var rawPixels = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var lzwData = TiffLzwEncode(rawPixels);
        // Truncate to roughly the first half of the compressed bytes.
        var truncated = lzwData[..(lzwData.Length / 2)];
        var tiff = BuildTiffLzw(4, 1, 8, 1, 1, truncated);
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    /// <summary>
    /// A TIFF-LZW strip containing a code whose value exceeds nextCode — i.e. a forward
    /// reference that can never be valid. The decoder must throw InvalidDataException
    /// ("invalid code"), not IndexOutOfRangeException.
    /// </summary>
    [Fact]
    public void TiffLzw_invalidCode_throwsInvalidDataException()
    {
        // Build a valid 4×1 greyscale image, then corrupt the strip bytes so that
        // a decoded code value jumps far beyond the current table size.
        // Strategy: take a valid LZW stream and overwrite mid-stream bytes with 0xFF so
        // that the MSB-first 9-bit reader sees codes ~511 (above nextCode for a short stream).
        var rawPixels = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var lzwData = TiffLzwEncode(rawPixels);
        var corrupted = (byte[])lzwData.Clone();
        // Blast the middle bytes with 0xFF — the 9-bit reader will produce code 511
        // (well beyond nextCode ~262 after only a few bytes), triggering the invalid-code guard.
        if (corrupted.Length > 2)
        {
            for (var i = 1; i < corrupted.Length; i++)
                corrupted[i] = 0xFF;
        }
        var tiff = BuildTiffLzw(4, 1, 8, 1, 1, corrupted);
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    /// <summary>
    /// A TIFF that declares a 4×4 greyscale image but whose LZW strip decompresses to only
    /// 4 bytes instead of 16. The output-length mismatch must throw InvalidDataException,
    /// not silently produce a short image.
    /// </summary>
    [Fact]
    public void TiffLzw_dimensionDataMismatch_throwsInvalidDataException()
    {
        // Encode only 4 bytes of pixel data, but declare a 4×4 (=16 bytes) image.
        var rawPixels = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var lzwData = TiffLzwEncode(rawPixels);
        // Build TIFF claiming 4×4=16 expected bytes but strip only decodes to 4.
        var tiff = BuildTiffLzw(4, 4, 8, 1, 1, lzwData);
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    /// <summary>
    /// A TIFF declaring hostile huge dimensions (width=height=50000, total=2.5 billion pixels)
    /// with a minimal LZW strip. ImageLimits.ValidateDimensions must reject it immediately —
    /// before any large allocation — with InvalidDataException.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public void TiffLzw_hugeDimensions_throwsWithoutAllocating()
    {
        // Build the smallest possible LZW stream (just ClearCode + EOI), then embed it
        // in a TIFF that claims 50000×50000.
        var lzwData = TiffLzwEncode([]);
        var tiff = BuildTiffLzw(50_000, 50_000, 8, 1, 1, lzwData);
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    /// <summary>
    /// A 16-bit greyscale TIFF whose LZW strip decodes to far fewer bytes than the declared
    /// width×height×2 requires. Must throw InvalidDataException, not silently truncate.
    /// </summary>
    [Fact]
    public void TiffLzw_16bit_truncatedStrip_throwsInvalidDataException()
    {
        // Declare 4×1 16-bit grey (= 8 bytes expected) but encode only 4 bytes of pixel data.
        var rawPixels = new byte[] { 0x00, 0x01, 0x00, 0x02 }; // 2 pixels × 2 bytes
        var lzwData = TiffLzwEncode(rawPixels);
        // Build TIFF claiming 4×1 at 16 bpp = 8 expected bytes.
        var tiff = BuildTiffLzw(4, 1, 16, 1, 1, lzwData);
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    // ── Images: CCITT G4 TIFF hardening ──────────────────────────────────────

    /// <summary>
    /// A CCITT Group 4 TIFF whose strip offset+length points beyond the end of the file.
    /// The strip-bounds check must throw InvalidDataException.
    /// </summary>
    [Fact]
    public void TiffG4_stripOffsetBeyondEof_throwsInvalidDataException()
    {
        var g4Data = CcittImageTests.BuildAllWhiteG4(8, 4);
        // Build a valid G4 TIFF, then corrupt the strip offset to point past the file.
        var tiff = BuildTiffG4ForHardening(8, 4, g4Data);
        // The StripOffset is a 4-byte LE LONG stored inline in the IFD entry value field.
        // Overwrite it with a huge value (far past EOF).
        PatchStripOffset(tiff, newOffset: 0x7FFFFFFF);
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    /// <summary>
    /// A CCITT Group 4 TIFF with two strips must throw NotSupportedException.
    /// (Exercises the multi-strip G4 rejection path via the hardening corpus.)
    /// </summary>
    [Fact]
    public void TiffG4_multiStrip_throwsNotSupportedException()
    {
        // Build a two-strip G4 TIFF by duplicating the strip arrays.
        var g4Data = CcittImageTests.BuildAllWhiteG4(8, 4);
        var tiff = BuildTiffG4MultiStripForHardening(8, 4, g4Data);
        Assert.Throws<NotSupportedException>(() => TiffImageLoader.Load(tiff));
    }

    // ── Images: interlaced PNG hardening ──────────────────────────────────────

    /// <summary>
    /// An Adam7-interlaced PNG whose IDAT is truncated so one or more passes cannot
    /// be fully read. The raw.Length < expectedRaw check must fire → InvalidDataException,
    /// not IndexOutOfRangeException.
    /// </summary>
    [Fact]
    public void Png_interlaced_truncatedIdat_throwsInvalidDataException()
    {
        // Build a valid 8×8 RGB interlaced PNG, then truncate its IDAT compressed payload
        // to about a third of its real size so the decompressed output is shorter than
        // the 7-pass Adam7 expected size.
        var fullIdat = ZlibCompress(BuildInterlacedRawBytes(8, 8, colorType: 2, bitDepth: 8));
        var truncatedIdat = fullIdat[..(fullIdat.Length / 3)];
        var png = BuildInterlacedPng(8, 8, bitDepth: 8, colorType: 2, idat: truncatedIdat);
        Assert.Throws<InvalidDataException>(() => PngImageLoader.Load(png));
    }

    /// <summary>
    /// An Adam7-interlaced PNG whose IDAT decompresses to far more bytes than the 7-pass
    /// expected total. The zlib-bomb cap (Inflate's cap guard) must throw InvalidDataException.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public void Png_interlaced_zlibBomb_throwsAndDoesNotExhaustMemory()
    {
        // 1×1 interlaced PNG: Adam7 pass 1 produces exactly 1 pixel, so expectedRaw is tiny.
        // IDAT expands to ~4 MB — far over the cap.
        var bomb = ZlibCompress(new byte[4_000_000]);
        var png = BuildInterlacedPng(1, 1, bitDepth: 8, colorType: 2, idat: bomb);
        Assert.Throws<InvalidDataException>(() => PngImageLoader.Load(png));
    }

    // ── Interlaced PNG builder helpers ────────────────────────────────────────

    /// <summary>
    /// Builds a minimal Adam7-interlaced PNG with the given IDAT payload already compressed.
    /// </summary>
    private static byte[] BuildInterlacedPng(int width, int height, byte bitDepth, byte colorType, byte[] idat)
    {
        var ihdr = new byte[13];
        WriteU32Be(ihdr, 0, (uint)width);
        WriteU32Be(ihdr, 4, (uint)height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        // compression=0, filter=0, interlace=1 (Adam7)
        ihdr[12] = 1;

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        ms.Write(PngChunk("IHDR", ihdr));
        ms.Write(PngChunk("IDAT", idat));
        ms.Write(PngChunk("IEND", []));
        return ms.ToArray();
    }

    /// <summary>
    /// Builds the uncompressed raw bytes (filter byte + row data for each pass) for an
    /// Adam7-interlaced image filled with zeros. Used to produce a valid compressed IDAT
    /// that can then be truncated for testing.
    /// </summary>
    private static byte[] BuildInterlacedRawBytes(int width, int height, byte colorType, byte bitDepth)
    {
        // Adam7 pass parameters.
        int[] xStart = [0, 4, 0, 2, 0, 1, 0];
        int[] yStart = [0, 0, 4, 0, 2, 0, 1];
        int[] xStep = [8, 8, 4, 4, 2, 2, 1];
        int[] yStep = [8, 8, 8, 4, 4, 2, 2];

        int samplesPerPixel = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 3 };

        using var ms = new MemoryStream();
        for (var pass = 0; pass < 7; pass++)
        {
            var rw = width > xStart[pass] ? (width - xStart[pass] + xStep[pass] - 1) / xStep[pass] : 0;
            var rh = height > yStart[pass] ? (height - yStart[pass] + yStep[pass] - 1) / yStep[pass] : 0;
            if (rw == 0 || rh == 0) continue;

            var passRowBits = (long)rw * samplesPerPixel * bitDepth;
            var passRowBytes = (int)((passRowBits + 7) / 8);

            for (var row = 0; row < rh; row++)
            {
                ms.WriteByte(0); // filter type None
                for (var b = 0; b < passRowBytes; b++)
                    ms.WriteByte(0);
            }
        }
        return ms.ToArray();
    }

    // ── TIFF-LZW / G4 builder helpers for hardening tests ────────────────────

    /// <summary>
    /// Builds a minimal TIFF with LZW compression (Compression=5), single strip.
    /// The strip data is the raw bytes passed in — no additional encoding.
    /// </summary>
    private static byte[] BuildTiffLzw(
        int w, int h, int bitsPerSample, int photometric, int samplesPerPixel, byte[] stripData)
    {
        using var ms = new MemoryStream();
        // Little-endian II
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        var stripOffset = 8u;
        var ifdOffset = stripOffset + (uint)stripData.Length;
        WriteTiffU32(ms, ifdOffset);
        ms.Write(stripData);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, (uint)bitsPerSample),
            (259, 3, 1, 5),                     // Compression = 5 (LZW)
            (262, 3, 1, (uint)photometric),
            (273, 4, 1, stripOffset),
            (277, 3, 1, (uint)samplesPerPixel),
            (278, 4, 1, (uint)h),
            (279, 4, 1, (uint)stripData.Length),
            (284, 3, 1, 1),
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteTiffU16(ms, (ushort)entries.Count);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteTiffU16(ms, tag);
            WriteTiffU16(ms, type);
            WriteTiffU32(ms, count);
            if (type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else
            {
                WriteTiffU32(ms, value);
            }
        }
        WriteTiffU32(ms, 0); // next IFD
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal single-strip G4 TIFF for the hardening tests.
    /// Returns the raw bytes so callers can locate and patch the strip offset field.
    /// </summary>
    private static byte[] BuildTiffG4ForHardening(int w, int h, byte[] stripData)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        var stripOffset = 8u;
        var ifdOffset = stripOffset + (uint)stripData.Length;
        WriteTiffU32(ms, ifdOffset);
        ms.Write(stripData);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, 1),
            (259, 3, 1, 4),              // Compression = 4 (CCITT G4)
            (262, 3, 1, 0),
            (273, 4, 1, stripOffset),
            (277, 3, 1, 1),
            (278, 4, 1, (uint)h),
            (279, 4, 1, (uint)stripData.Length),
            (284, 3, 1, 1),
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteTiffU16(ms, (ushort)entries.Count);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteTiffU16(ms, tag);
            WriteTiffU16(ms, type);
            WriteTiffU32(ms, count);
            if (type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else
            {
                WriteTiffU32(ms, value);
            }
        }
        WriteTiffU32(ms, 0);
        return ms.ToArray();
    }

    /// <summary>
    /// Patches the StripOffsets value (tag 273) in a little-endian TIFF IFD entry.
    /// Scans the IFD for tag 273 and overwrites its 4-byte inline value field.
    /// </summary>
    private static void PatchStripOffset(byte[] tiff, uint newOffset)
    {
        // IFD offset is at bytes 4-7 (LE).
        var ifdOffset = (int)(tiff[4] | (tiff[5] << 8) | (tiff[6] << 16) | (tiff[7] << 24));
        var entryCount = tiff[ifdOffset] | (tiff[ifdOffset + 1] << 8);
        for (var i = 0; i < entryCount; i++)
        {
            var entryBase = ifdOffset + 2 + i * 12;
            var tag = tiff[entryBase] | (tiff[entryBase + 1] << 8);
            if (tag == 273) // StripOffsets
            {
                tiff[entryBase + 8] = (byte)newOffset;
                tiff[entryBase + 9] = (byte)(newOffset >> 8);
                tiff[entryBase + 10] = (byte)(newOffset >> 16);
                tiff[entryBase + 11] = (byte)(newOffset >> 24);
                return;
            }
        }
    }

    /// <summary>
    /// Builds a two-strip G4 TIFF for hardening (exercises the multi-strip rejection path
    /// via the hardening corpus rather than the TiffImageTests coverage).
    /// </summary>
    private static byte[] BuildTiffG4MultiStripForHardening(int w, int h, byte[] stripData)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x49); ms.WriteByte(0x49);
        ms.WriteByte(0x2A); ms.WriteByte(0x00);

        // Two equal halves (or near-halves) of the strip data.
        var half = (stripData.Length + 1) / 2;
        var s1 = stripData[..half];
        var s2 = stripData[half..];
        var s1Offset = 8u;
        var s2Offset = s1Offset + (uint)s1.Length;

        // IFD layout: 2 + 10*12 + 4 = 126 bytes; strip arrays follow immediately.
        var ifdOffset = s2Offset + (uint)s2.Length;
        var stripOffsetsArrayOff = ifdOffset + 126u;
        var stripByteCountsArrayOff = stripOffsetsArrayOff + 8u;

        WriteTiffU32(ms, ifdOffset);
        ms.Write(s1);
        ms.Write(s2);

        var entries = new List<(ushort tag, ushort type, uint count, uint value)>
        {
            (256, 4, 1, (uint)w),
            (257, 4, 1, (uint)h),
            (258, 3, 1, 1),
            (259, 3, 1, 4),
            (262, 3, 1, 0),
            (273, 4, 2, stripOffsetsArrayOff),
            (277, 3, 1, 1),
            (278, 4, 1, (uint)(h / 2 + 1)),
            (279, 4, 2, stripByteCountsArrayOff),
            (284, 3, 1, 1),
        };
        entries.Sort((a, b) => a.tag.CompareTo(b.tag));

        WriteTiffU16(ms, (ushort)entries.Count);
        foreach (var (tag, type, count, value) in entries)
        {
            WriteTiffU16(ms, tag);
            WriteTiffU16(ms, type);
            WriteTiffU32(ms, count);
            if (count * (uint)(type == 3 ? 2 : 4) <= 4 && type == 3)
            {
                ms.WriteByte((byte)value); ms.WriteByte((byte)(value >> 8));
                ms.WriteByte(0); ms.WriteByte(0);
            }
            else
            {
                WriteTiffU32(ms, value);
            }
        }
        WriteTiffU32(ms, 0);

        WriteTiffU32(ms, s1Offset);
        WriteTiffU32(ms, s2Offset);
        WriteTiffU32(ms, (uint)s1.Length);
        WriteTiffU32(ms, (uint)s2.Length);

        return ms.ToArray();
    }

    // ── Minimal TIFF-variant LZW encoder (mirrors TiffImageTests.TiffLzwEncode) ─

    private static byte[] TiffLzwEncode(byte[] input)
    {
        const int clearCode = 256;
        const int eoiCode = 257;
        const int firstFreeCode = 258;
        const int maxTableSize = 4096;

        using var outMs = new MemoryStream();
        int codeWidth = 9;
        int nextCode = firstFreeCode;
        var table = new Dictionary<long, int>();
        int bitBuf = 0;
        int bitsInBuf = 0;

        void EmitCode(int code)
        {
            bitBuf = (bitBuf << codeWidth) | (code & ((1 << codeWidth) - 1));
            bitsInBuf += codeWidth;
            while (bitsInBuf >= 8)
            {
                bitsInBuf -= 8;
                outMs.WriteByte((byte)(bitBuf >> bitsInBuf));
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
                outMs.WriteByte((byte)(bitBuf << (8 - bitsInBuf)));
            return outMs.ToArray();
        }

        int w = input[0];
        for (var i = 1; i < input.Length; i++)
        {
            int k = input[i];
            long key = ((long)w << 8) | (byte)k;
            if (table.TryGetValue(key, out int existing))
            {
                w = existing;
            }
            else
            {
                EmitCode(w);
                if (nextCode < maxTableSize)
                {
                    table[key] = nextCode++;
                    if (nextCode == (1 << codeWidth) && codeWidth < 12)
                        codeWidth++;
                }
                else
                {
                    EmitCode(clearCode);
                    ResetTable();
                }
                w = k;
            }
        }
        EmitCode(w);
        EmitCode(eoiCode);
        if (bitsInBuf > 0)
            outMs.WriteByte((byte)(bitBuf << (8 - bitsInBuf)));
        return outMs.ToArray();
    }

    // ── Little-endian TIFF write helpers ─────────────────────────────────────

    private static void WriteTiffU16(Stream s, ushort v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
    }

    private static void WriteTiffU32(Stream s, uint v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 24));
    }

    // ── sfnt builders ────────────────────────────────────────────────────────

    private static byte[] BuildFont(params (string Tag, byte[] Data)[] tables)
    {
        var offsets = new int[tables.Length];
        var pos = 12 + tables.Length * 16;
        for (var i = 0; i < tables.Length; i++)
        {
            offsets[i] = pos;
            pos += (tables[i].Data.Length + 3) & ~3; // 4-byte aligned table data
        }

        var font = new byte[pos];
        WriteU32Be(font, 0, 0x00010000);                 // TrueType sfnt version
        WriteU16Be(font, 4, (ushort)tables.Length);      // numTables
        for (var i = 0; i < tables.Length; i++)
        {
            var rec = 12 + i * 16;
            Encoding.ASCII.GetBytes(tables[i].Tag).CopyTo(font, rec);
            WriteU32Be(font, rec + 8, (uint)offsets[i]);
            WriteU32Be(font, rec + 12, (uint)tables[i].Data.Length);
            tables[i].Data.CopyTo(font, offsets[i]);
        }
        return font;
    }

    private static byte[] BuildHead(short indexToLocFormat)
    {
        var head = new byte[54];
        WriteU16Be(head, 18, 1000);                        // unitsPerEm
        WriteU16Be(head, 50, (ushort)indexToLocFormat);    // indexToLocFormat
        return head;
    }

    private static byte[] BuildMaxp(int numGlyphs)
    {
        var maxp = new byte[6];
        WriteU16Be(maxp, 4, (ushort)numGlyphs);
        return maxp;
    }

    private static byte[] BuildLongLoca(params uint[] offsets)
    {
        var loca = new byte[offsets.Length * 4];
        for (var i = 0; i < offsets.Length; i++)
            WriteU32Be(loca, i * 4, offsets[i]);
        return loca;
    }

    private static byte[] BuildCmapFormat4((ushort End, ushort Start, short Delta, ushort RangeOffset)[] segs)
    {
        var segCount = segs.Length;
        var subtable = new byte[16 + segCount * 8]; // 14-byte header + 4 arrays + 2-byte reservedPad
        WriteU16Be(subtable, 0, 4);                         // format
        WriteU16Be(subtable, 2, (ushort)subtable.Length);  // length
        WriteU16Be(subtable, 6, (ushort)(segCount * 2));   // segCountX2

        var endBase = 14;
        var startBase = endBase + segCount * 2 + 2; // after endCode[] + reservedPad
        var deltaBase = startBase + segCount * 2;
        var rangeBase = deltaBase + segCount * 2;
        for (var i = 0; i < segCount; i++)
        {
            WriteU16Be(subtable, endBase + i * 2, segs[i].End);
            WriteU16Be(subtable, startBase + i * 2, segs[i].Start);
            WriteU16Be(subtable, deltaBase + i * 2, (ushort)segs[i].Delta);
            WriteU16Be(subtable, rangeBase + i * 2, segs[i].RangeOffset);
        }

        // cmap header: version(0), numTables(1), one Windows-BMP encoding record → subtable at 12.
        var cmap = new byte[12 + subtable.Length];
        WriteU16Be(cmap, 2, 1);   // numTables
        WriteU16Be(cmap, 4, 3);   // platformID = Windows
        WriteU16Be(cmap, 6, 1);   // encodingID = BMP
        WriteU32Be(cmap, 8, 12);  // subtable offset
        subtable.CopyTo(cmap, 12);
        return cmap;
    }

    // ── image builders ───────────────────────────────────────────────────────

    private static byte[] BuildPng(int width, int height, byte bitDepth, byte colorType, byte[] idat)
    {
        var ihdr = new byte[13];
        WriteU32Be(ihdr, 0, (uint)width);
        WriteU32Be(ihdr, 4, (uint)height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        // compression(10), filter(11), interlace(12) all 0

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        ms.Write(PngChunk("IHDR", ihdr));
        ms.Write(PngChunk("IDAT", idat));
        ms.Write(PngChunk("IEND", []));
        return ms.ToArray();
    }

    // PngImageLoader does not validate chunk CRCs, so the trailing CRC is left zero.
    private static byte[] PngChunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        WriteU32Be(chunk, 0, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] BuildBmpHeader(int width, int height, ushort bitCount, uint pixelOffset, int totalSize)
    {
        var bmp = new byte[totalSize];
        bmp[0] = 0x42; bmp[1] = 0x4D; // 'BM'
        WriteU32Le(bmp, 10, pixelOffset);
        WriteU32Le(bmp, 14, 40); // BITMAPINFOHEADER
        WriteS32Le(bmp, 18, width);
        WriteS32Le(bmp, 22, height);
        WriteU16Le(bmp, 28, bitCount);
        WriteU32Le(bmp, 30, 0); // BI_RGB
        return bmp;
    }

    // ── endian writers ───────────────────────────────────────────────────────

    private static void WriteU16Be(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void WriteU32Be(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteU16Le(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32Le(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteS32Le(byte[] buf, int offset, int value) => WriteU32Le(buf, offset, (uint)value);

    private static void WriteU16Le(Stream s, ushort value)
    {
        s.WriteByte((byte)value);
        s.WriteByte((byte)(value >> 8));
    }
}
