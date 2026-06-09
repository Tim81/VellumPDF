// Copyright 2026 Timothy van der Ham (@Tim81)
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

    [Fact]
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

    [Fact]
    public void Png_zlibBomb_throwsAndDoesNotExhaustMemory()
    {
        // A 1×1 image whose IDAT decompresses to ~4 MB — far beyond the dimensions' expected size.
        var bomb = ZlibCompress(new byte[4_000_000]);
        var png = BuildPng(1, 1, bitDepth: 8, colorType: 2, idat: bomb);
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
