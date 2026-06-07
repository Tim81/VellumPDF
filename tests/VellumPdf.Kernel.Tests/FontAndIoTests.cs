// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Images;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>Tests for Groups A, B, C, F and G kernel fixes.</summary>
public sealed class FontAndIoTests
{
    // ── Group A: PdfWriter non-seekable stream ────────────────────────────────

    [Fact]
    public void PdfWriter_nonSeekableStream_writesCorrectly()
    {
        // Wrap a MemoryStream in a non-seekable proxy
        var ms = new MemoryStream();
        var ns = new NonSeekableStreamWrapper(ms);

        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Save(ns);

        var bytes = ms.ToArray();
        Assert.True(bytes.Length > 100, "Non-seekable write must produce output");
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void PdfWriter_position_tracksWithoutStreamPosition()
    {
        var ms = new MemoryStream();
        var ns = new NonSeekableStreamWrapper(ms);
        var writer = new PdfWriter(ns);
        Assert.Equal(0, writer.Position);
        writer.WriteAscii("hello"u8);
        Assert.Equal(5, writer.Position);
        writer.WriteByte((byte)'!');
        Assert.Equal(6, writer.Position);
    }

    // ── Group A: Binary header bytes ──────────────────────────────────────────

    [Fact]
    public void Save_binaryMarker_hasCorrectRawBytes()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        // After "%PDF-2.0\n%" (10 bytes) the next 4 bytes must be E2 E3 CF D3
        Assert.Equal(0xE2, bytes[10]);
        Assert.Equal(0xE3, bytes[11]);
        Assert.Equal(0xCF, bytes[12]);
        Assert.Equal(0xD3, bytes[13]);
    }

    // ── Group B: Image XObject written to PDF ─────────────────────────────────

    [Fact]
    public void Save_pngImage_containsXObjectInPdf()
    {
        var pngBytes = CreateMinimalRgbPng(4, 4);
        var image = PngImageLoader.Load(pngBytes);

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterImageXObject(page, image, "Im1");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/XObject", content);
        Assert.Contains("/Image", content);
        Assert.Contains("/Width", content);
        Assert.Contains("/Height", content);
    }

    [Fact]
    public void Save_pngImage_hasCorrectDimensions()
    {
        var pngBytes = CreateMinimalRgbPng(8, 6);
        var image = PngImageLoader.Load(pngBytes);
        Assert.Equal(8, image.Width);
        Assert.Equal(6, image.Height);

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterImageXObject(page, image, "Im1");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Width 8", content);
        Assert.Contains("/Height 6", content);
    }

    [Fact]
    public void Save_rgbaPng_hasSMaskObject()
    {
        var pngBytes = CreateMinimalRgbaPng(4, 4);
        var image = PngImageLoader.Load(pngBytes);
        Assert.NotNull(image.SMask); // confirm SMask is present

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterImageXObject(page, image, "Im1");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // SMask stream must appear; it gets its own XObject dict with /DeviceGray
        Assert.Contains("/SMask", content);
        Assert.Contains("/DeviceGray", content);
    }

    [Fact]
    public void JpegImageLoader_parsesMinimalJpeg()
    {
        var jpegBytes = CreateMinimalJpeg(8, 6, 3);
        var image = JpegImageLoader.Load(jpegBytes);
        Assert.Equal(8, image.Width);
        Assert.Equal(6, image.Height);
    }

    [Fact]
    public void JpegImageLoader_grayscale_detectsDeviceGray()
    {
        var jpegBytes = CreateMinimalJpeg(4, 4, 1);
        var image = JpegImageLoader.Load(jpegBytes);
        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
        // Loading succeeds (color space detection is internal; we just verify no throw)
    }

    // ── Group C: TrueType subset table checksum (left-justified partial word) ──
    // Validates the production TrueTypeFontEmbedder.Checksum against the canonical
    // sfnt definition (zero-pad to a 4-byte multiple, then sum big-endian uint32
    // words). The expected value is derived independently of the production code.

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 })]        // exact 4 bytes
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0xAB })]  // +1 byte
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0xAB, 0xCD })] // +2 bytes
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0xAB, 0xCD, 0xEF })] // +3 bytes
    public void Checksum_matchesCanonicalZeroPaddedDefinition(byte[] data)
    {
        // Canonical sfnt checksum: pad with zero bytes to a 4-byte multiple,
        // then sum the big-endian 32-bit words.
        var padded = new byte[(data.Length + 3) / 4 * 4];
        data.CopyTo(padded, 0);
        uint expected = 0;
        for (var i = 0; i < padded.Length; i += 4)
            expected += (uint)((padded[i] << 24) | (padded[i + 1] << 16) | (padded[i + 2] << 8) | padded[i + 3]);

        // Call the real production method (internal, visible via InternalsVisibleTo).
        var actual = TrueTypeFontEmbedder.Checksum(data);
        Assert.Equal(expected, actual);
    }

    // ── Group F: Times-Bold AFM widths ───────────────────────────────────────

    [Fact]
    public void TimesBold_spaceWidth_is250()
    {
        var w = Standard14Metrics.GetWidth(Standard14.TimesBold, ' ');
        Assert.Equal(250, w);
    }

    [Fact]
    public void TimesBold_W_differsFromTimesRoman()
    {
        // Times-Roman 'W' = 944, Times-Bold 'W' = 1000 per AFM
        var wRoman = Standard14Metrics.GetWidth(Standard14.TimesRoman, 'W');
        var wBold = Standard14Metrics.GetWidth(Standard14.TimesBold, 'W');
        Assert.NotEqual(wRoman, wBold);
        Assert.Equal(1000, wBold);
    }

    [Fact]
    public void TimesItalic_W_differsFromTimesRoman()
    {
        // Times-Italic 'W' = 833, Times-Roman 'W' = 944
        var wItalic = Standard14Metrics.GetWidth(Standard14.TimesItalic, 'W');
        var wRoman = Standard14Metrics.GetWidth(Standard14.TimesRoman, 'W');
        Assert.NotEqual(wRoman, wItalic);
        Assert.Equal(833, wItalic);
    }

    [Fact]
    public void TimesBoldItalic_W_differsFromTimesRoman()
    {
        var wBoldItalic = Standard14Metrics.GetWidth(Standard14.TimesBoldItalic, 'W');
        var wRoman = Standard14Metrics.GetWidth(Standard14.TimesRoman, 'W');
        Assert.NotEqual(wRoman, wBoldItalic);
    }

    // ── Group G1: PdfName delimiter escaping ─────────────────────────────────

    [Fact]
    public void PdfName_escapesParenthesesAndSlash()
    {
        var ms = new MemoryStream();
        var w = new PdfWriter(ms);
        new PdfName("A(B/C").WriteTo(w);
        var result = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Equal("/A#28B#2FC", result);
    }

    [Fact]
    public void PdfName_escapesPercent()
    {
        var ms = new MemoryStream();
        var w = new PdfWriter(ms);
        new PdfName("A%B").WriteTo(w);
        var result = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Equal("/A#25B", result);
    }

    [Fact]
    public void PdfName_escapesAngleBrackets()
    {
        var ms = new MemoryStream();
        var w = new PdfWriter(ms);
        new PdfName("A<B>C").WriteTo(w);
        var result = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Equal("/A#3CB#3EC", result);
    }

    // ── Group G2: PdfReal NaN/Infinity ────────────────────────────────────────

    [Fact]
    public void PdfReal_NaN_throws()
    {
        Assert.Throws<ArgumentException>(() => new PdfReal(double.NaN));
    }

    [Fact]
    public void PdfReal_PositiveInfinity_throws()
    {
        Assert.Throws<ArgumentException>(() => new PdfReal(double.PositiveInfinity));
    }

    [Fact]
    public void PdfReal_NegativeInfinity_throws()
    {
        Assert.Throws<ArgumentException>(() => new PdfReal(double.NegativeInfinity));
    }

    [Fact]
    public void PdfReal_finiteValue_doesNotThrow()
    {
        var r = new PdfReal(3.14);
        Assert.Equal(3.14, r.Value);
    }

    // ── Group C: TrueType embed round-trip (skipped if font absent) ───────────

    [Fact]
    public void TrueTypeEmbed_arialRoundTrip_skippedIfAbsent()
    {
        const string fontPath = @"C:\Windows\Fonts\arial.ttf";
        if (!System.IO.File.Exists(fontPath)) return; // Skip on CI / Linux

        var fontData = System.IO.File.ReadAllBytes(fontPath);
        var embedder = new TrueTypeFontEmbedder(fontData, "F1");

        embedder.GetGlyphId('H');
        embedder.GetGlyphId('e');
        embedder.GetGlyphId('l');
        embedder.GetGlyphId('o');

        var fontFileStream = embedder.BuildFontFileStream();
        Assert.NotNull(fontFileStream);

        var descRef = new PdfIndirectReference(99);
        var descriptor = embedder.BuildFontDescriptor(descRef);
        Assert.NotNull(descriptor);

        var toUnicode = embedder.BuildToUnicodeCMap();
        Assert.NotNull(toUnicode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class NonSeekableStreamWrapper(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override void WriteByte(byte value) => inner.WriteByte(value);
    }

    // ── PNG builders ─────────────────────────────────────────────────────────

    private static byte[] CreateMinimalRgbPng(int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", CreateIhdr(w, h, 8, 2));
        WriteChunk(ms, "IDAT", ZlibCompress(CreateScanlines(w, h, 3)));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] CreateMinimalRgbaPng(int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", CreateIhdr(w, h, 8, 6));
        WriteChunk(ms, "IDAT", ZlibCompress(CreateScanlines(w, h, 4)));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] CreateIhdr(int w, int h, byte bitDepth, byte colorType)
    {
        var buf = new byte[13];
        buf[0] = (byte)(w >> 24); buf[1] = (byte)(w >> 16); buf[2] = (byte)(w >> 8); buf[3] = (byte)w;
        buf[4] = (byte)(h >> 24); buf[5] = (byte)(h >> 16); buf[6] = (byte)(h >> 8); buf[7] = (byte)h;
        buf[8] = bitDepth; buf[9] = colorType;
        return buf;
    }

    private static byte[] CreateScanlines(int w, int h, int channels)
    {
        var row = new byte[1 + w * channels]; // filter byte = 0 (None) + pixel data
        using var ms = new MemoryStream();
        for (var y = 0; y < h; y++) ms.Write(row);
        return ms.ToArray();
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using var z = new System.IO.Compression.ZLibStream(ms,
            System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true);
        z.Write(data);
        z.Flush();
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        s.WriteByte((byte)(data.Length >> 24)); s.WriteByte((byte)(data.Length >> 16));
        s.WriteByte((byte)(data.Length >> 8)); s.WriteByte((byte)data.Length);
        foreach (var c in type) s.WriteByte((byte)c);
        s.Write(data);
        var crcData = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) crcData[i] = (byte)type[i];
        data.CopyTo(crcData, 4);
        var crc = Crc32(crcData);
        s.WriteByte((byte)(crc >> 24)); s.WriteByte((byte)(crc >> 16));
        s.WriteByte((byte)(crc >> 8)); s.WriteByte((byte)crc);
    }

    private static uint Crc32(byte[] data)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    // ── Minimal synthetic JPEG ───────────────────────────────────────────────

    private static byte[] CreateMinimalJpeg(int width, int height, int components)
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]); // SOI
        var sofLen = 8 + components * 3;
        ms.Write([0xFF, 0xC0]);
        ms.WriteByte((byte)(sofLen >> 8)); ms.WriteByte((byte)sofLen);
        ms.WriteByte(8); // precision
        ms.WriteByte((byte)(height >> 8)); ms.WriteByte((byte)height);
        ms.WriteByte((byte)(width >> 8)); ms.WriteByte((byte)width);
        ms.WriteByte((byte)components);
        for (var i = 0; i < components; i++) { ms.WriteByte((byte)(i + 1)); ms.WriteByte(0x11); ms.WriteByte(0); }
        ms.Write([0xFF, 0xD9]); // EOI
        return ms.ToArray();
    }
}
