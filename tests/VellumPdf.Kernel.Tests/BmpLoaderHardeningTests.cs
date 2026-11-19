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
