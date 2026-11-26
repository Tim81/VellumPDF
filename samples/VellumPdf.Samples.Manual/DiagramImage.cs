// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;

namespace VellumPdf.Samples.Manual;

/// <summary>
/// Draws the exploded-view figure into a pixel buffer and encodes it as a PNG, so the sample ships
/// no binary asset. The layers match the legend table in chapter 1.
/// </summary>
public static class DiagramImage
{
    /// <summary>Width of the figure in pixels.</summary>
    public const int Width = 360;

    /// <summary>
    /// Height of the figure in pixels. At the width of the content box the figure is taller than
    /// an A4 page, so the sample asks the image element to fit it to the page.
    /// </summary>
    public const int Height = 640;

    /// <summary>
    /// The fill colour of each layer, top to bottom, used by the figure and its legend.
    /// </summary>
    public static IReadOnlyList<(byte R, byte G, byte B)> LayerColours { get; } =
    [
        (60, 66, 74),
        (22, 120, 132),
        (238, 240, 236),
        (74, 78, 70),
        (150, 190, 214),
        (96, 102, 110),
    ];

    /// <summary>Returns the figure as the bytes of an 8-bit RGB PNG.</summary>
    public static byte[] Png()
    {
        var pixels = new byte[Width * Height * 3];
        Fill(pixels, 0, 0, Width, Height, (246, 248, 249));

        // Airflow arrows on both sides, drawn first so the layers cover their stems.
        var flow = (R: (byte)40, G: (byte)130, B: (byte)190);
        foreach (var x in new[] { 28, 332 })
        {
            Fill(pixels, x - 3, 70, 6, 480, flow);
            Triangle(pixels, x, 560, 14, flow);
        }

        // Layer 1: the top cover, with the outlet ring.
        RoundRect(pixels, 70, 30, 220, 58, 14, LayerColours[0]);
        Ring(pixels, 180, 59, 30, 22, (230, 232, 235));
        Fill(pixels, 150, 57, 60, 4, (230, 232, 235));

        // Layer 2: the fan and sensor module, with two sensor windows.
        RoundRect(pixels, 85, 118, 190, 70, 10, LayerColours[1]);
        Circle(pixels, 130, 153, 20, (10, 70, 80));
        Circle(pixels, 130, 153, 7, (200, 230, 234));
        Fill(pixels, 190, 140, 60, 10, (10, 70, 80));
        Fill(pixels, 190, 158, 60, 10, (10, 70, 80));

        // Layers 3 to 5: the HEPA filter, the carbon filter and the pre-filter, each with a
        // pattern that tells them apart in a greyscale print.
        RoundRect(pixels, 80, 218, 200, 78, 6, LayerColours[2]);
        for (var x = 92; x < 270; x += 12)
            Fill(pixels, x, 226, 3, 62, (190, 194, 188));

        RoundRect(pixels, 80, 326, 200, 78, 6, LayerColours[3]);
        for (var y = 336; y < 396; y += 10)
            Fill(pixels, 90, y, 180, 3, (122, 126, 118));

        RoundRect(pixels, 80, 434, 200, 52, 6, LayerColours[4]);
        for (var x = 90; x < 270; x += 14)
            Fill(pixels, x, 440, 2, 40, (96, 140, 170));
        for (var y = 446; y < 480; y += 14)
            Fill(pixels, 90, y, 180, 2, (96, 140, 170));

        // Layer 6: the base and its feet.
        RoundRect(pixels, 65, 516, 230, 64, 16, LayerColours[5]);
        Fill(pixels, 90, 580, 30, 12, (40, 44, 48));
        Fill(pixels, 240, 580, 30, 12, (40, 44, 48));

        return Encode(pixels);
    }

    private static void Fill(byte[] px, int x, int y, int w, int h, (byte R, byte G, byte B) c)
    {
        for (var j = Math.Max(y, 0); j < Math.Min(y + h, Height); j++)
        {
            for (var i = Math.Max(x, 0); i < Math.Min(x + w, Width); i++)
                Put(px, i, j, c);
        }
    }

    private static void Put(byte[] px, int x, int y, (byte R, byte G, byte B) c)
    {
        var o = (y * Width + x) * 3;
        px[o] = c.R;
        px[o + 1] = c.G;
        px[o + 2] = c.B;
    }

    private static void Circle(byte[] px, int cx, int cy, int r, (byte R, byte G, byte B) c) =>
        Ring(px, cx, cy, r, 0, c);

    private static void Ring(byte[] px, int cx, int cy, int outer, int inner, (byte R, byte G, byte B) c)
    {
        for (var j = cy - outer; j <= cy + outer; j++)
        {
            for (var i = cx - outer; i <= cx + outer; i++)
            {
                var d = ((i - cx) * (i - cx)) + ((j - cy) * (j - cy));
                if (d <= outer * outer && d >= inner * inner && i >= 0 && j >= 0 && i < Width && j < Height)
                    Put(px, i, j, c);
            }
        }
    }

    private static void RoundRect(
        byte[] px, int x, int y, int w, int h, int r, (byte R, byte G, byte B) c)
    {
        for (var j = y; j < y + h; j++)
        {
            for (var i = x; i < x + w; i++)
            {
                // Distance from the nearest corner centre, only inside a corner square.
                var dx = i < x + r ? x + r - i : i >= x + w - r ? i - (x + w - r - 1) : 0;
                var dy = j < y + r ? y + r - j : j >= y + h - r ? j - (y + h - r - 1) : 0;
                if ((dx * dx) + (dy * dy) <= r * r)
                    Put(px, i, j, c);
            }
        }
    }

    private static void Triangle(byte[] px, int cx, int tipY, int half, (byte R, byte G, byte B) c)
    {
        for (var k = 0; k <= half; k++)
            Fill(px, cx - (half - k), tipY - k, 2 * (half - k) + 1, 1, c);
    }

    private static byte[] Encode(byte[] pixels)
    {
        // Each scanline starts with filter type 0 (none).
        var raw = new byte[(Width * 3 + 1) * Height];
        for (var y = 0; y < Height; y++)
            Buffer.BlockCopy(pixels, y * Width * 3, raw, y * (Width * 3 + 1) + 1, Width * 3);

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteInt(header, 0, Width);
        WriteInt(header, 4, Height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type 2: RGB
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, idat.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(MemoryStream s, ReadOnlySpan<byte> type, byte[] data)
    {
        var len = new byte[4];
        WriteInt(len, 0, data.Length);
        s.Write(len);
        var body = new byte[4 + data.Length];
        type.CopyTo(body);
        data.CopyTo(body, 4);
        s.Write(body);
        var crc = new byte[4];
        WriteInt(crc, 0, unchecked((int)Crc32(body)));
        s.Write(crc);
    }

    private static void WriteInt(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }
}
