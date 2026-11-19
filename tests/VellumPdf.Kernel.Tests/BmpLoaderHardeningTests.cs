// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Known-answer and bound tests for <see cref="BmpImageLoader"/> (#536, #535, #533). Every
/// fixture is built byte by byte so each offset is the one the test names; none comes from a
/// writer that might fill a field differently.
/// </summary>
public sealed class BmpLoaderHardeningTests
{
    // Allocation bound for a refusal. Measured per calling thread, as MalformedInputTests does for
    // PNG, because parallel classes allocate at the same time. 1 MiB is far under the 300,000,000
    // bytes the loader allocated before the pixel data was checked (#536), and far above the few
    // kilobytes a refusal costs.
    private const long RefusalAllocationBound = 1024 * 1024;

    // ── #536: the raster is sized after the pixel data is known to be there ───

    [Theory]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(32)]
    public void HeaderOnlyFile_declaringTenThousandSquare_isRefusedWithoutAllocatingTheRaster(int bitCount)
    {
        // A 10,000 x 10,000 header over one byte of pixel data: under the 100,000,000 pixel cap, so
        // the dimension check passes and the pixel-data check is the only thing standing between
        // the header and a 300 MB allocation.
        var palette = bitCount == 8 ? Palette(256) : [];
        var bmp = Bmp(bitCount, 10_000, 10_000, clrUsed: 0, palette, pixels: new byte[1]);

        // Warm-up, so one-time JIT and static-initialiser allocations are not charged below.
        Assert.Throws<InvalidDataException>(() => BmpImageLoader.Load(bmp));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<InvalidDataException>(() => BmpImageLoader.Load(bmp));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("pixel data extends beyond end of file", ex.Message);
        Assert.True(
            allocated < RefusalAllocationBound,
            $"expected under {RefusalAllocationBound} bytes allocated before refusing; measured {allocated}.");
    }

    // ── #535: the fourth byte of a 32-bit BI_RGB pixel is not alpha ───────────

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)128)]
    [InlineData((byte)255)]
    public void Bmp32BiRgb_ignoresTheUnusedByte_andEmitsNoSoftMask(byte unused)
    {
        // Blue 10, green 20, red 30 in every pixel. Measured on 2.3.3, the soft mask was present
        // for 0 and 128 (all zeros and all 128s) and absent for 255.
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < 16; i++)
        {
            pixels[i * 4] = 10;
            pixels[i * 4 + 1] = 20;
            pixels[i * 4 + 2] = 30;
            pixels[i * 4 + 3] = unused;
        }

        var img = BmpImageLoader.Load(Bmp(32, 4, 4, clrUsed: 0, [], pixels));

        Assert.Null(img.SMask);
        Assert.Equal(4, img.Width);
        Assert.Equal(4, img.Height);
        Assert.Equal(PixelsRgb(16, r: 30, g: 20, b: 10), RgbBytes(img));
    }

    [Fact]
    public void Bmp32BiRgb_mixedUnusedBytes_keepColoursPerPixel_andEmitNoSoftMask()
    {
        // Two pixels, bottom-up, so the stored first row is the image's last: pixel 0 of the file
        // is (B1,G2,R3) with 0x00 in the unused byte, pixel 1 is (B4,G5,R6) with 0x7F.
        byte[] pixels = [1, 2, 3, 0x00, 4, 5, 6, 0x7F];
        var img = BmpImageLoader.Load(Bmp(32, 2, 1, clrUsed: 0, [], pixels));

        Assert.Null(img.SMask);
        Assert.Equal<byte[]>([3, 2, 1, 6, 5, 4], RgbBytes(img));
    }

    // ── #533: biClrUsed sets the palette length ───────────────────────────────

    [Theory]
    [InlineData(4u, 86)]
    [InlineData(16u, 134)]
    [InlineData(256u, 1094)]
    [InlineData(0u, 1094)] // zero means 256
    public void Bmp8_clrUsed_setsHowManyPaletteEntriesTheFileMustHold(uint clrUsed, int expectedFileLength)
    {
        var entries = clrUsed == 0 ? 256 : (int)clrUsed;
        var bmp = Bmp(8, 4, 4, clrUsed, Palette(entries), pixels: new byte[16]);

        Assert.Equal(expectedFileLength, bmp.Length);
        var img = BmpImageLoader.Load(bmp);

        Assert.Equal(4, img.Width);
        Assert.Equal(4, img.Height);
    }

    [Fact]
    public void Bmp8_shortPalette_decodesEachIndexToItsOwnEntry()
    {
        // 2 x 2, three palette entries, file rows padded to four bytes. Bottom-up, so the first
        // stored row (0, 1) is the image's second row. Entry k is B=3k+1, G=5k+2, R=7k+3.
        byte[] pixels = [0, 1, 0, 0, 2, 0, 0, 0];
        var bmp = Bmp(8, 2, 2, clrUsed: 3, Palette(3), pixels);
        Assert.Equal(54 + 12 + 8, bmp.Length);

        var rgb = RgbBytes(BmpImageLoader.Load(bmp));

        // Output top row = stored row 1 = (2, 0); output bottom row = stored row 0 = (0, 1).
        Assert.Equal<byte[]>(
            [
                17, 12, 7,   // entry 2: R=7*2+3, G=5*2+2, B=3*2+1
                3, 2, 1,     // entry 0
                3, 2, 1,     // entry 0
                10, 7, 4,    // entry 1
            ],
            rgb);
    }

    [Theory]
    [InlineData(1000u)]
    [InlineData(uint.MaxValue)]
    public void Bmp8_clrUsedPastTheFile_isRefusedAsTruncated_withoutOverflow(uint clrUsed)
    {
        // Four entries are present, so a count of 1,000 or 4,294,967,295 is capped to 256 and the
        // 86-byte file is still too short for them.
        var bmp = Bmp(8, 4, 4, clrUsed, Palette(4), pixels: new byte[16]);

        var ex = Assert.Throws<InvalidDataException>(() => BmpImageLoader.Load(bmp));
        Assert.Contains("palette extends beyond end of file", ex.Message);
    }

    [Fact]
    public void Bmp8_paletteLongerThanTheFile_isRefusedAsTruncated()
    {
        // Declares four entries (16 bytes, ending at 70) but the file stops at 60, with the pixel
        // offset inside it so only the palette test can refuse.
        var bmp = Bmp(8, 1, 1, clrUsed: 4, [], pixels: new byte[4], pixelOffset: 56);
        Assert.Equal(60, bmp.Length);

        var ex = Assert.Throws<InvalidDataException>(() => BmpImageLoader.Load(bmp));
        Assert.Contains("palette extends beyond end of file", ex.Message);
    }

    [Fact]
    public void Bmp8_shortFile_pixelPointingPastTheDeclaredPalette_isRefused()
    {
        // Entries 0..3 are declared; the pixel says 5. The file is 86 bytes, so 2.3.3 refused it
        // as truncated and nothing that loaded before is affected.
        var bmp = Bmp(8, 4, 4, clrUsed: 4, Palette(4), pixels: [5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        var ex = Assert.Throws<InvalidDataException>(() => BmpImageLoader.Load(bmp));
        Assert.Contains("palette entry 5", ex.Message);
    }

    [Fact]
    public void Bmp8_shortFile_pixelDataStartingInsideThePalette_isRefused()
    {
        // The pixel offset is 54, the first palette byte. 2.3.3 refused this 86-byte file too.
        var bmp = Bmp(8, 4, 4, clrUsed: 4, Palette(4), pixels: new byte[16], pixelOffset: 54);

        var ex = Assert.Throws<InvalidDataException>(() => BmpImageLoader.Load(bmp));
        Assert.Contains("inside the 4-entry palette", ex.Message);
    }

    [Fact]
    public void Bmp8_fileOf1078BytesOrMore_keepsIndexingAs256Entries_whateverClrUsedSays()
    {
        // 2.3.3 never read biClrUsed, so this file loaded with index 200 reading the table slot
        // at 54 + 200*4. It must still load and read that slot (rule: what loaded keeps loading).
        // Slot 200 of Palette(256): B=601&255=89, G=1002&255=234, R=1403&255=123.
        var bmp = Bmp(8, 1, 1, clrUsed: 4, Palette(256), pixels: [200, 0, 0, 0]);
        Assert.Equal(1082, bmp.Length);

        var rgb = RgbBytes(BmpImageLoader.Load(bmp));

        Assert.Equal<byte[]>([123, 234, 89], rgb);
    }

    // ── Helpers for reading the result ────────────────────────────────────────

    // The image's raster bytes, recovered through the public surface: serialise the stream and
    // inflate what follows the "stream" keyword.
    private static byte[] RgbBytes(PdfImageXObject img)
    {
        using var ms = new MemoryStream();
        img.BuildStream().WriteTo(new VellumPdf.IO.PdfWriter(ms));
        var raw = ms.ToArray();
        var start = IndexOf(raw, "\nstream\n"u8) + 8;
        var end = IndexOf(raw, "\nendstream"u8);
        using var z = new System.IO.Compression.ZLibStream(
            new MemoryStream(raw[start..end]), System.IO.Compression.CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> needle)
    {
        var i = data.AsSpan().IndexOf(needle);
        Assert.True(i >= 0, "marker not found in the serialised stream");
        return i;
    }

    private static byte[] PixelsRgb(int count, byte r, byte g, byte b)
    {
        var data = new byte[count * 3];
        for (var i = 0; i < count; i++)
        {
            data[i * 3] = r;
            data[i * 3 + 1] = g;
            data[i * 3 + 2] = b;
        }
        return data;
    }

    // ── Builders ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a BITMAPINFOHEADER bitmap: the 14-byte file header, the 40-byte info header, the
    /// palette bytes, then the pixel bytes. <paramref name="pixelOffset"/> defaults to the first
    /// byte after the palette.
    /// </summary>
    internal static byte[] Bmp(
        int bitCount, int width, int height, uint clrUsed, byte[] palette, byte[] pixels,
        uint? pixelOffset = null)
    {
        var offset = pixelOffset ?? (uint)(54 + palette.Length);
        var bmp = new byte[Math.Max((int)offset + pixels.Length, 54 + palette.Length)];
        bmp[0] = 0x42;
        bmp[1] = 0x4D;
        BitConverter.TryWriteBytes(bmp.AsSpan(2), (uint)bmp.Length);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), offset);
        BitConverter.TryWriteBytes(bmp.AsSpan(14), 40u);       // biSize
        BitConverter.TryWriteBytes(bmp.AsSpan(18), width);
        BitConverter.TryWriteBytes(bmp.AsSpan(22), height);
        BitConverter.TryWriteBytes(bmp.AsSpan(26), (ushort)1); // biPlanes
        BitConverter.TryWriteBytes(bmp.AsSpan(28), (ushort)bitCount);
        BitConverter.TryWriteBytes(bmp.AsSpan(46), clrUsed);   // biClrUsed
        palette.CopyTo(bmp, 54);
        pixels.CopyTo(bmp, (int)offset);
        return bmp;
    }

    /// <summary>
    /// A palette of <paramref name="entries"/> RGBQUADs. Entry i is blue 3i+1, green 5i+2, red
    /// 7i+3 (mod 256), so every entry differs from its neighbours in every channel.
    /// </summary>
    internal static byte[] Palette(int entries)
    {
        var p = new byte[entries * 4];
        for (var i = 0; i < entries; i++)
        {
            p[i * 4] = (byte)(i * 3 + 1);
            p[i * 4 + 1] = (byte)(i * 5 + 2);
            p[i * 4 + 2] = (byte)(i * 7 + 3);
        }
        return p;
    }
}
