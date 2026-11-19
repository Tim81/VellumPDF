// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Where <see cref="GifImageLoader"/> puts the first frame: on the logical screen of GIF89a
/// section 18, at the Image Left and Top Position of section 20.c.ii and 20.c.iii (#498).
///
/// Releases through 2.3.3 decoded at the frame's own size. A file whose frame fills the screen
/// from the origin, which is every file in the #490 sweep, could not tell the difference, which is
/// why nothing caught it. Every case here therefore uses a frame that is not the whole screen,
/// apart from the one that pins that a file that is the whole screen is unchanged.
///
/// Each fixture is a real file from <see cref="GifEncoder"/> with the screen size and the frame
/// position patched in the header and the image descriptor, so the LZW stream is a conforming one
/// and only the fields under test are hand-written. The expected canvas is written out as a map
/// of characters, not computed: <c>.</c> is a pixel the frame does not cover, a letter is the
/// frame colour that lands there.
/// </summary>
public sealed class GifLogicalScreenTests
{
    // Four distinct colours, chosen so no two share a channel value, so a mixed-up pixel shows.
    private static readonly Dictionary<char, byte[]> Colours = new()
    {
        ['A'] = [200, 10, 20],
        ['B'] = [30, 220, 40],
        ['C'] = [50, 60, 230],
        ['D'] = [240, 250, 70],
    };

    /// <summary>
    /// A frame that fills the screen from the origin decodes as it did on 2.3.3: the frame's own
    /// pixels, no soft mask. This is the file every ordinary encoder writes.
    /// </summary>
    [Fact]
    public void Decode_frameFillsScreenAtOrigin_isTheFrameWithNoSoftMask()
    {
        var gif = Build(["ABA", "CDC"], screenWidth: 3, screenHeight: 2, left: 0, top: 0);

        var img = GifImageLoader.Load(gif);

        Assert.Equal((3, 2), (img.Width, img.Height));
        Assert.Null(img.SMask);
        Assert.Equal(["ABA", "CDC"], ReadMap(img));
    }

    /// <summary>
    /// The first row of #498's measurement table: a 5x4 frame at (3, 6) on a 20x20 screen. The
    /// canvas is the screen's size and the frame sits where the descriptor says. Only the
    /// edge rows and the row the frame starts on are written out; the whole map is also checked
    /// against <see cref="ExpectedMap"/>.
    /// </summary>
    [Fact]
    public void Decode_offsetFrameOnLargerScreen_isTheScreenSizeWithTheFrameAtItsPosition()
    {
        string[] frame = ["ABCDA", "BCDAB", "CDABC", "DABCD"];
        var gif = Build(frame, screenWidth: 20, screenHeight: 20, left: 3, top: 6);

        var img = GifImageLoader.Load(gif);

        Assert.Equal((20, 20), (img.Width, img.Height));
        var map = ReadMap(img);
        Assert.Equal(ExpectedMap(frame, 20, 20, 3, 6), map);
        Assert.Equal("...ABCDA............", map[6]);
        Assert.Equal(new string('.', 20), map[0]);
        Assert.Equal(new string('.', 20), map[19]);
    }

    /// <summary>The second row of #498's table: 5x4 at (4, 3) on a 9x7 screen, flush to the far edges.</summary>
    [Fact]
    public void Decode_frameFlushWithTheFarCorner_isTheScreenSizeWithTheFrameAtItsPosition()
    {
        string[] frame = ["ABCDA", "BCDAB", "CDABC", "DABCD"];
        var gif = Build(frame, screenWidth: 9, screenHeight: 7, left: 4, top: 3);

        var map = ReadMap(GifImageLoader.Load(gif));

        Assert.Equal(
            [".........", ".........", ".........", "....ABCDA", "....BCDAB", "....CDABC", "....DABCD"],
            map);
    }

    /// <summary>
    /// A frame at the origin that is smaller than the screen. The uncovered area is to the right
    /// and below, and it is transparent.
    /// </summary>
    [Fact]
    public void Decode_frameAtOriginSmallerThanScreen_leavesTheRestTransparent()
    {
        var gif = Build(["AB", "CD"], screenWidth: 4, screenHeight: 3, left: 0, top: 0);

        var img = GifImageLoader.Load(gif);

        Assert.Equal((4, 3), (img.Width, img.Height));
        Assert.Equal(["AB..", "CD..", "...."], ReadMap(img));
        Assert.Equal(
            [255, 255, 0, 0, 255, 255, 0, 0, 0, 0, 0, 0],
            ReadAlpha(img));
    }

    /// <summary>
    /// The colour stored under an uncovered pixel is black. It is not the Background Color Index's
    /// colour (section 18.f), and it does not change when that index changes, which is what the
    /// remarks on <see cref="GifImageLoader.Load"/> say.
    /// </summary>
    [Fact]
    public void Decode_backgroundColourIndex_playsNoPart()
    {
        var plain = Build(["AB", "CD"], screenWidth: 4, screenHeight: 3, left: 0, top: 0);
        var withBackground = (byte[])plain.Clone();
        withBackground[11] = 2;   // Background Color Index, section 18.f: a non-black table entry

        var a = GifImageLoader.Load(plain);
        var b = GifImageLoader.Load(withBackground);

        Assert.Equal(Inflate(a), Inflate(b));
        Assert.Equal(ReadAlpha(a), ReadAlpha(b));
        // The uncovered corner (3, 2) is black under alpha 0.
        var corner = (2 * 4 + 3) * 3;
        Assert.Equal([0, 0, 0], Inflate(b)[corner..(corner + 3)]);
    }

    /// <summary>
    /// A screen smaller than the frame does not crop it: every pixel the file holds is kept, and
    /// with the frame at the origin the result is exactly the frame, as it was on 2.3.3.
    /// </summary>
    [Fact]
    public void Decode_screenSmallerThanFrameAtOrigin_keepsTheWholeFrame()
    {
        var gif = Build(["ABCD", "DCBA", "ABCD"], screenWidth: 2, screenHeight: 2, left: 0, top: 0);

        var img = GifImageLoader.Load(gif);

        Assert.Equal((4, 3), (img.Width, img.Height));
        Assert.Null(img.SMask);
        Assert.Equal(["ABCD", "DCBA", "ABCD"], ReadMap(img));
    }

    /// <summary>
    /// A screen smaller than the frame's far edge is widened to that edge, with the frame at its
    /// position and the margin it leaves transparent.
    /// </summary>
    [Fact]
    public void Decode_screenSmallerThanOffsetFrame_isWidenedToTheFramesFarEdge()
    {
        var gif = Build(["ABC", "CBA"], screenWidth: 2, screenHeight: 2, left: 1, top: 2);

        var img = GifImageLoader.Load(gif);

        Assert.Equal((4, 4), (img.Width, img.Height));
        Assert.Equal(["....", "....", ".ABC", ".CBA"], ReadMap(img));
    }

    /// <summary>
    /// A screen of zero by zero is a screen the frame overflows, so the same widening applies. A
    /// frame at the origin comes back at its own size with no soft mask, as on 2.3.3.
    /// </summary>
    [Fact]
    public void Decode_zeroSizedScreen_isTreatedAsOverflowedByTheFrame()
    {
        var atOrigin = GifImageLoader.Load(Build(["AB", "CD"], screenWidth: 0, screenHeight: 0, left: 0, top: 0));
        var offset = GifImageLoader.Load(Build(["AB", "CD"], screenWidth: 0, screenHeight: 0, left: 1, top: 1));

        Assert.Equal(["AB", "CD"], ReadMap(atOrigin));
        Assert.Null(atOrigin.SMask);
        Assert.Equal(["...", ".AB", ".CD"], ReadMap(offset));
    }

    /// <summary>
    /// A transparent colour inside the frame (Graphic Control Extension, section 23) and the
    /// uncovered area are both alpha 0, and the opaque frame pixels are 255. The two must not be
    /// confused: a frame pixel is opaque unless it carries the index.
    /// </summary>
    [Fact]
    public void Decode_transparentIndexInsideAFrameOnAScreen_isAlphaZeroOnlyWhereItIsUsed()
    {
        var gif = Build(["AB", "BA"], screenWidth: 4, screenHeight: 3, left: 1, top: 1, transparentColour: 'B');

        var img = GifImageLoader.Load(gif);

        Assert.Equal((4, 3), (img.Width, img.Height));
        Assert.Equal(
            [0, 0, 0, 0,
             0, 255, 0, 0,
             0, 0, 255, 0],
            ReadAlpha(img));
        // The transparent pixel keeps its palette colour under alpha 0, as it does without a screen.
        Assert.Equal(["....", ".AB.", ".BA."], ReadMap(img));
    }

    /// <summary>
    /// Rows stored in the four-pass order of Appendix E are put in display order before they are
    /// placed, so an interlaced frame lands on the screen the same way as a sequential one.
    /// </summary>
    [Fact]
    public void Decode_interlacedFrameOnAScreen_isPlacedInDisplayOrder()
    {
        string[] frame = ["AA", "BB", "CC", "DD", "AB", "CD", "BA"];
        var sequential = Build(frame, screenWidth: 5, screenHeight: 9, left: 2, top: 1);
        var interlaced = Build(frame, screenWidth: 5, screenHeight: 9, left: 2, top: 1, interlaced: true);

        var seqMap = ReadMap(GifImageLoader.Load(sequential));
        var intMap = ReadMap(GifImageLoader.Load(interlaced));

        Assert.Equal(ExpectedMap(frame, 5, 9, 2, 1), seqMap);
        Assert.Equal(seqMap, intMap);
    }

    /// <summary>
    /// The largest screen the descriptor can hold, 65535 by 65535, is far over the pixel limit,
    /// so the frame comes back at its own size, as on 2.3.3. This pins the documented fallback; it
    /// is not a statement that the fallback is the right answer for a later major.
    /// </summary>
    [Fact]
    public void Decode_screenOverThePixelLimit_returnsTheFrameAtItsOwnSize()
    {
        var gif = Build(["AB", "CD"], screenWidth: 65535, screenHeight: 65535, left: 0, top: 0);

        var img = GifImageLoader.Load(gif);

        Assert.Equal((2, 2), (img.Width, img.Height));
        Assert.Null(img.SMask);
        Assert.Equal(["AB", "CD"], ReadMap(img));
    }

    /// <summary>
    /// The canvas is checked against the same pixel limit as the frame, at the limit and one over,
    /// without allocating either: the frame is 2x2 and only the screen changes. 10,000 by 10,000 is
    /// exactly 100,000,000 pixels and is placed; one more column is over and falls back.
    /// </summary>
    [Fact]
    public void Decode_screenAtAndOverThePixelLimit_isPlacedThenFallsBack()
    {
        var atLimit = Build(["AB", "CD"], screenWidth: 10_000, screenHeight: 10_000, left: 9_998, top: 9_998);
        var over = Build(["AB", "CD"], screenWidth: 10_001, screenHeight: 10_000, left: 9_998, top: 9_998);

        var placed = GifImageLoader.Load(atLimit);
        var fallback = GifImageLoader.Load(over);

        Assert.Equal((10_000, 10_000), (placed.Width, placed.Height));
        Assert.NotNull(placed.SMask);
        Assert.Equal((2, 2), (fallback.Width, fallback.Height));
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// A GIF whose frame is <paramref name="rows"/> and whose screen and frame position are the
    /// given values. The file starts as <see cref="GifEncoder"/> output, which has a global colour
    /// table and the descriptor at a known offset, and the fields under test are overwritten.
    /// </summary>
    private static byte[] Build(
        string[] rows, int screenWidth, int screenHeight, int left, int top,
        bool interlaced = false, char? transparentColour = null)
    {
        var w = rows[0].Length;
        var h = rows.Length;
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
                Colours[rows[y][x]].CopyTo(rgb, (y * w + x) * 3);
        }

        var gif = GifEncoder.Encode(rgb, w, h, interlaced);
        PutUInt16(gif, 6, screenWidth);
        PutUInt16(gif, 8, screenHeight);

        var tableBytes = 3 * (2 << (gif[10] & 0x07));
        var descriptor = 13 + tableBytes;
        Assert.Equal(0x2C, gif[descriptor]);
        PutUInt16(gif, descriptor + 1, left);
        PutUInt16(gif, descriptor + 3, top);

        if (transparentColour is char t)
        {
            // Find the palette index the encoder gave the colour, rather than assuming its order.
            var want = Colours[t];
            var index = -1;
            for (var i = 0; i < tableBytes / 3; i++)
            {
                if (gif[13 + i * 3] == want[0] && gif[14 + i * 3] == want[1] && gif[15 + i * 3] == want[2])
                    index = i;
            }
            Assert.True(index >= 0);

            // Graphic Control Extension, section 23, directly before the image descriptor. It is an
            // 89a block, so the header version moves with it (section 17.d.ii).
            byte[] gce = [0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, (byte)index, 0x00];
            gif = [.. gif[..descriptor], .. gce, .. gif[descriptor..]];
            gif[4] = (byte)'9';
        }

        return gif;
    }

    private static void PutUInt16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)(value >> 8);
    }

    /// <summary>The map the loader's output should read as: the frame at (left, top), dots elsewhere.</summary>
    private static string[] ExpectedMap(string[] frame, int width, int height, int left, int top)
    {
        var rows = new string[height];
        for (var y = 0; y < height; y++)
        {
            var sb = new StringBuilder(new string('.', width));
            var fy = y - top;
            if (fy >= 0 && fy < frame.Length)
            {
                for (var x = 0; x < frame[fy].Length; x++)
                    sb[left + x] = frame[fy][x];
            }
            rows[y] = sb.ToString();
        }
        return rows;
    }

    /// <summary>
    /// Reads the decoded raster back as the character map the fixtures are written in. A pixel
    /// that is black and alpha 0, or black with no soft mask at all, reads as <c>.</c>; a pixel that
    /// matches a fixture colour reads as its letter, and anything else fails the test by name.
    /// </summary>
    private static string[] ReadMap(PdfImageXObject img)
    {
        var rgb = Inflate(img);
        var rows = new string[img.Height];
        for (var y = 0; y < img.Height; y++)
        {
            var sb = new StringBuilder();
            for (var x = 0; x < img.Width; x++)
            {
                var o = (y * img.Width + x) * 3;
                var px = rgb[o..(o + 3)];
                var letter = Colours.FirstOrDefault(c => c.Value.SequenceEqual(px)).Key;
                if (letter != '\0')
                    sb.Append(letter);
                else if (px.All(b => b == 0))
                    sb.Append('.');
                else
                    throw new Xunit.Sdk.XunitException($"unexpected colour {px[0]},{px[1]},{px[2]} at ({x},{y})");
            }
            rows[y] = sb.ToString();
        }
        return rows;
    }

    private static byte[] ReadAlpha(PdfImageXObject img)
    {
        Assert.NotNull(img.SMask);
        return InflateStream(img.SMask);
    }

    private static byte[] Inflate(PdfImageXObject img) => InflateStream(img.BuildStream());

    private static byte[] InflateStream(PdfStream stream)
    {
        // The raster is the stream's deflated body, and the only way to its bytes from outside
        // the package is to write the object and inflate what lands between the stream keywords.
        using var pdfMs = new MemoryStream();
        stream.WriteTo(new VellumPdf.IO.PdfWriter(pdfMs));
        var raw = pdfMs.ToArray();
        var start = IndexOf(raw, "\nstream\n"u8) + 8;
        var end = IndexOf(raw, "\nendstream"u8);

        using var input = new MemoryStream(raw[start..end]);
        using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        throw new InvalidOperationException("stream marker not found");
    }
}
