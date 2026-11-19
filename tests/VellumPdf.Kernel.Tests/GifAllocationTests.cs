// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// The decoder must not size its raster from the image descriptor before the file has shown it
/// holds that many pixels (#536's class: allocation ahead of validation). A 42-byte file can
/// declare 10,000 x 10,000, which passes the 100M-pixel limit exactly. On 2.3.3 it was refused
/// only after a 100,000,000-byte buffer had been allocated, measured as 100.0 MB with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> around <c>Load</c>.
///
/// The bound is a byte count, not a time, so it does not depend on the machine (#400). The
/// threshold, 1,000,000 bytes, is far below the 100,000,000 the unfixed decoder allocates and far
/// above what the refusal needs (0.0 MB at the precision the probe printed, which is 100 kB).
/// </summary>
public sealed class GifAllocationTests
{
    [Theory]
    [InlineData(10_000, 10_000, false)]
    [InlineData(9_999, 9_999, false)]
    [InlineData(10_000, 10_000, true)]
    public void Load_streamShorterThanTheDescriptorPromises_isRefusedBeforeTheRasterIsAllocated(
        int declaredWidth, int declaredHeight, bool interlaced)
    {
        var gif = TinyFileDeclaring(declaredWidth, declaredHeight, interlaced);
        Assert.True(gif.Length < 64, $"fixture is {gif.Length} bytes");

        // Warm up so JIT and static initialisation are not counted against the load.
        Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains($"ended after 4 of {(long)declaredWidth * declaredHeight} pixels", ex.Message);
        Assert.True(allocated < 1_000_000, $"allocated {allocated} bytes refusing a {gif.Length}-byte file");
    }

    /// <summary>
    /// The counting pass must make exactly the decisions the real one does: a stream that carries
    /// more pixels than the descriptor promises still loads, as on 2.3.3, and a file that fills the
    /// raster exactly still decodes to every pixel.
    /// </summary>
    [Fact]
    public void Load_exactAndOverlongStreams_stillDecode()
    {
        byte[] rgb = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120];   // 2x2, four colours
        var exact = GifEncoder.Encode(rgb, 2, 2);
        var overlong = Declare(exact, 1, 1);

        var a = GifImageLoader.Load(exact);
        var b = GifImageLoader.Load(overlong);

        Assert.Equal((2, 2), (a.Width, a.Height));
        Assert.Equal((2, 2), (b.Width, b.Height));   // 1x1 frame on the 2x2 screen, placed on it (#498)
    }

    private static byte[] TinyFileDeclaring(int width, int height, bool interlaced)
    {
        byte[] rgb = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120];
        return Declare(GifEncoder.Encode(rgb, 2, 2, interlaced), width, height);
    }

    /// <summary>The same file with the image descriptor's frame size rewritten (the screen is untouched).</summary>
    private static byte[] Declare(byte[] gif, int width, int height)
    {
        var copy = (byte[])gif.Clone();
        var descriptor = 13 + 3 * (2 << (copy[10] & 0x07));
        copy[descriptor + 5] = (byte)width; copy[descriptor + 6] = (byte)(width >> 8);
        copy[descriptor + 7] = (byte)height; copy[descriptor + 8] = (byte)(height >> 8);
        return copy;
    }
}
