// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The smallest well-formed file of each format the image factories read. Every one is built
/// from the format's own layout, so a test that loads it through a loader exercises the real
/// reading code, and none is a file on disk.
/// </summary>
internal static class ImageFixtures
{
    /// <summary>A 2 by 2 opaque white RGB PNG.</summary>
    public static byte[] Png() => PdfTestUtil.CreateMinimalRgbPng();

    /// <summary>A 2 by 1 greyscale PNG with 16 bits per sample (PNG 3rd edition, 11.2.2).</summary>
    public static byte[] Png16()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        ihdr[3] = 2;
        ihdr[7] = 1;
        ihdr[8] = 16;
        ihdr[9] = 0;
        PngChunk(ms, "IHDR", ihdr);
        PngChunk(ms, "IDAT", Zlib([0, 0x12, 0x34, 0xAB, 0xCD]));
        PngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>
    /// The 2 by 2 PNG with a tEXt chunk of <paramref name="extra"/> pattern bytes after the header,
    /// which a PNG reader skips (PNG 3rd edition, 5.4, ancillary chunks), so the file is large and
    /// loads to the same image.
    /// </summary>
    public static byte[] PngWithTextChunk(int extra)
    {
        var png = Png();
        var text = new byte[extra];
        for (var i = 0; i < text.Length; i++)
            text[i] = (byte)(i * 31 + (i >> 8) + 1);
        text[0] = (byte)'k';
        text[1] = 0;
        using var ms = new MemoryStream();
        ms.Write(png, 0, 8 + 25);                  // signature and the 25-byte IHDR chunk
        PngChunk(ms, "tEXt", text);
        ms.Write(png, 8 + 25, png.Length - 8 - 25);
        return ms.ToArray();
    }

    /// <summary>A 3-component baseline JPEG header: start of image, SOF0, end of image.</summary>
    public static byte[] Jpeg(int width = 8, int height = 4)
    {
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write([0xFF, 0xC0, 0x00, 0x11, 0x08]);
        ms.Write([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        ms.Write([0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00]);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    /// <summary>An uncompressed greyscale TIFF, 2 by 1, 8 or 16 bits per sample.</summary>
    public static byte[] TiffGrey(bool littleEndian, int bitsPerSample)
    {
        var pixels = bitsPerSample == 16
            ? (littleEndian ? new byte[] { 0x34, 0x12, 0xCD, 0xAB } : new byte[] { 0x12, 0x34, 0xAB, 0xCD })
            : new byte[] { 0x10, 0xF0 };
        return TiffStrip(littleEndian, bitsPerSample, compression: 1, photometric: 1, 2, 1, pixels);
    }

    /// <summary>A bilevel TIFF whose one strip is Group 4 (Compression 4): 8 by 8, all one colour.</summary>
    public static byte[] TiffGroup4() =>
        TiffStrip(true, 1, compression: 4, photometric: 0, 8, 8, [0xFF]);

    /// <summary>A greyscale TIFF whose one strip is a JPEG stream (Compression 7).</summary>
    public static byte[] TiffJpegStrip() =>
        TiffStrip(true, 8, compression: 7, photometric: 1, 8, 4, JpegGrey(8, 4));

    /// <summary>A 1-component baseline JPEG header: start of image, SOF0, end of image.</summary>
    public static byte[] JpegGrey(int width, int height) =>
    [
        0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08,
        (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9,
    ];

    // One strip, one sample per pixel, tags in the order TIFF 6.0 section 2 requires (ascending).
    private static byte[] TiffStrip(
        bool littleEndian, int bitsPerSample, int compression, int photometric,
        int width, int height, byte[] strip)
    {
        const int entries = 9;
        var dataOffset = 8 + 2 + entries * 12 + 4;
        using var ms = new MemoryStream();
        ms.Write(littleEndian ? "II"u8 : "MM"u8);
        U16(ms, 42, littleEndian);
        U32(ms, 8, littleEndian);
        U16(ms, entries, littleEndian);
        Entry(ms, 256, 3, width, littleEndian);             // ImageWidth
        Entry(ms, 257, 3, height, littleEndian);            // ImageLength
        Entry(ms, 258, 3, bitsPerSample, littleEndian);     // BitsPerSample
        Entry(ms, 259, 3, compression, littleEndian);       // Compression
        Entry(ms, 262, 3, photometric, littleEndian);       // PhotometricInterpretation
        Entry(ms, 273, 4, dataOffset, littleEndian);        // StripOffsets
        Entry(ms, 277, 3, 1, littleEndian);                 // SamplesPerPixel
        Entry(ms, 278, 3, height, littleEndian);            // RowsPerStrip
        Entry(ms, 279, 4, strip.Length, littleEndian);      // StripByteCounts
        U32(ms, 0, littleEndian);
        ms.Write(strip);
        return ms.ToArray();
    }

    /// <summary>A 24-bit uncompressed BMP, 1 by 1.</summary>
    public static byte[] Bmp()
    {
        var bmp = new byte[58];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(58).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(1).CopyTo(bmp, 18);
        BitConverter.GetBytes(1).CopyTo(bmp, 22);
        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
        BitConverter.GetBytes(4).CopyTo(bmp, 34);
        bmp[54] = 0x20;
        bmp[55] = 0x80;
        bmp[56] = 0xFF;
        return bmp;
    }

    /// <summary>
    /// A GIF whose single frame is 1 by 1 pixel and sits at (<paramref name="left"/>,
    /// <paramref name="top"/>) on a logical screen of <paramref name="screen"/> pixels square.
    /// </summary>
    public static byte[] Gif(string version = "GIF89a", int screen = 1, int left = 0, int top = 0)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(version));
        ms.Write([(byte)screen, 0, (byte)screen, 0, 0x80, 0, 0]);       // screen, 2-entry global table
        ms.Write([0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF]);                 // black, white
        ms.Write([0x2C, (byte)left, 0, (byte)top, 0, 1, 0, 1, 0, 0x00]); // image descriptor 1 x 1
        ms.Write([0x02, 0x02, 0x44, 0x01, 0x00]);                       // LZW: clear, index 0, end
        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    /// <summary>
    /// A JBIG2 file as <c>Jbig2ImageLoader</c> reads one: the 8-byte ID string, a page-information
    /// segment and one MMR-coded generic region.
    /// </summary>
    public static byte[] Jbig2(int width = 16, int height = 16)
    {
        var pageInfo = new byte[19];
        I32(pageInfo, 0, width);
        I32(pageInfo, 4, height);
        var region = new byte[20];
        I32(region, 0, width);
        I32(region, 4, height);
        region[17] = 0x01;
        region[18] = 0xFF;
        region[19] = 0xFF;
        return
        [
            0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Segment(0, 48, 1, pageInfo),
            .. Segment(1, 39, 1, region),
        ];
    }

    /// <summary>A bare JPEG 2000 codestream: SOC, a SIZ segment and EOC.</summary>
    public static byte[] JpxCodestream(int width = 16, int height = 16, int components = 3)
    {
        var buf = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
        Be16(buf, 38 + 3 * components);
        Be16(buf, 0);
        foreach (var v in new[] { width, height, 0, 0, width, height, 0, 0 })
            Be32(buf, v);
        Be16(buf, components);
        for (var c = 0; c < components; c++)
            buf.AddRange([7, 1, 1]);
        buf.AddRange([0xFF, 0xD9]);
        return [.. buf];
    }

    /// <summary>A JP2 file: signature box, ftyp, jp2h with ihdr and colr, and a jp2c box.</summary>
    public static byte[] Jp2(int width = 16, int height = 16, byte signatureBoxLength = 0x0C)
    {
        var buf = new List<byte>();
        buf.AddRange([0x00, 0x00, 0x00, signatureBoxLength, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A]);
        // A longer signature box carries that many extra payload bytes, so the boxes still tile.
        buf.AddRange(new byte[signatureBoxLength - 0x0C]);
        buf.AddRange(Box("ftyp"u8, [.. "jp2 "u8, 0, 0, 0, 0, .. "jp2 "u8]));
        var ihdr = new List<byte>();
        Be32(ihdr, height);
        Be32(ihdr, width);
        Be16(ihdr, 3);
        ihdr.AddRange([7, 7, 0, 0]);
        var colr = new List<byte> { 1, 0, 0 };
        Be32(colr, 16);
        buf.AddRange(Box("jp2h"u8, [.. Box("ihdr"u8, [.. ihdr]), .. Box("colr"u8, [.. colr])]));
        buf.AddRange(Box("jp2c"u8, JpxCodestream(width, height)));
        return [.. buf];
    }

    private static byte[] Box(ReadOnlySpan<byte> type, byte[] payload)
    {
        var box = new List<byte>();
        Be32(box, 8 + payload.Length);
        box.AddRange(type.ToArray());
        box.AddRange(payload);
        return [.. box];
    }

    private static byte[] Segment(int number, int type, int page, byte[] data)
    {
        var seg = new byte[11 + data.Length];
        I32(seg, 0, number);
        seg[4] = (byte)(type & 0x3F);
        seg[6] = (byte)page;
        I32(seg, 7, data.Length);
        data.CopyTo(seg, 11);
        return seg;
    }

    private static void I32(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    private static void Be16(List<byte> b, int v) => b.AddRange([(byte)(v >> 8), (byte)v]);

    private static void Be32(List<byte> b, int v) =>
        b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    private static void U16(Stream s, int v, bool le) =>
        s.Write(le ? [(byte)v, (byte)(v >> 8)] : [(byte)(v >> 8), (byte)v]);

    private static void U32(Stream s, int v, bool le) =>
        s.Write(le
            ? [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]
            : [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    // TIFF 6.0 section 2: a SHORT value is left-justified in the four-byte value field.
    private static void Entry(Stream s, int tag, int type, int value, bool le)
    {
        U16(s, tag, le);
        U16(s, type, le);
        U32(s, 1, le);
        if (type == 3)
        {
            U16(s, value, le);
            U16(s, 0, le);
        }
        else
        {
            U32(s, value, le);
        }
    }

    private static void PngChunk(Stream s, string type, byte[] data)
    {
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        var len = new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
        s.Write(len);
        s.Write(body);
        var crc = 0xFFFFFFFFu;
        foreach (var b in body)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        crc ^= 0xFFFFFFFFu;
        s.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Fastest))
            z.Write(data);
        return output.ToArray();
    }
}
