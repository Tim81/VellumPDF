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
        Assert.Equal((1, 1), (b.Width, b.Height));   // the declared 1x1 frame; the screen is not read by default
    }

    /// <summary>
    /// Without the option, no allocation depends on the logical screen: a valid 2x2 frame on a
    /// screen declaring 10,000 x 10,000 loads for the same allocation as on a 2x2 screen, on the
    /// order of the frame. (The screen is bytes 6 to 9 of the header; the file is the same 42-byte
    /// size.)
    /// </summary>
    [Fact]
    public void Load_defaultPath_doesNotAllocateForTheScreen()
    {
        var gif = TinyFileDeclaring(2, 2, interlaced: false);
        gif[6] = 0x10; gif[7] = 0x27; gif[8] = 0x10; gif[9] = 0x27;   // 10,000 x 10,000
        Assert.True(gif.Length < 64, $"fixture is {gif.Length} bytes");
        _ = GifImageLoader.Load(gif);   // warm up

        var before = GC.GetAllocatedBytesForCurrentThread();
        var img = GifImageLoader.Load(gif);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal((2, 2), (img.Width, img.Height));
        Assert.True(allocated < 1_000_000, $"allocated {allocated} bytes for a 2x2 frame on a 10,000 x 10,000 screen");
    }

    /// <summary>
    /// With the option, the canvas is the screen: allocation is bounded by 4 bytes a pixel (RGB
    /// plus the alpha of the /SMask) plus a constant for the frame and the decoder, and the screen
    /// itself is bounded by the 100M-pixel limit, so the most a file can cost is about 400 MB. The
    /// bound is checked on a 16M-pixel screen, 5 bytes a pixel allowed, so a second copy of the
    /// canvas (another 4 bytes a pixel) fails it.
    /// </summary>
    [Fact]
    public void Load_placed_allocatesAtMostFourBytesAPixelOfScreen()
    {
        var gif = TinyFileDeclaring(2, 2, interlaced: false);
        gif[6] = 0xA0; gif[7] = 0x0F; gif[8] = 0xA0; gif[9] = 0x0F;   // 4,000 x 4,000
        var options = new GifLoadOptions { PlaceOnLogicalScreen = true };
        _ = GifImageLoader.Load(gif, options);   // warm up

        var before = GC.GetAllocatedBytesForCurrentThread();
        var img = GifImageLoader.Load(gif, options);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var pixels = 4_000L * 4_000L;
        Assert.Equal((4_000, 4_000), (img.Width, img.Height));
        Assert.True(allocated >= 4 * pixels, $"allocated {allocated} bytes, under the canvas's {4 * pixels}");
        Assert.True(allocated <= 5 * pixels, $"allocated {allocated} bytes, over 5 bytes a pixel ({5 * pixels})");
    }

    /// <summary>A screen over the limit is refused before the canvas exists, so the refusal allocates no canvas.</summary>
    [Fact]
    public void Load_placedOverTheLimit_isRefusedWithoutAllocatingTheCanvas()
    {
        var gif = TinyFileDeclaring(2, 2, interlaced: false);
        gif[6] = 0xFF; gif[7] = 0xFF; gif[8] = 0xFF; gif[9] = 0xFF;   // 65,535 x 65,535
        var options = new GifLoadOptions { PlaceOnLogicalScreen = true };
        Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif, options));   // warm up

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif, options));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_000_000, $"allocated {allocated} bytes refusing an over-limit screen");
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
