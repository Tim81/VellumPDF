// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Under <see cref="GifLoadOptions.PlaceOnLogicalScreen"/> the loader refuses a file that breaks a
/// GIF89a rule the default path accepts (rule R4: a new member refuses non-compliant input from
/// the first release). Every case here pairs the refusal with the default load of the same bytes
/// succeeding, because the default must stay what 2.3.3 returned, and each refusal is a separate
/// check in the loader with its own mutation.
///
/// The files are written by hand rather than by <see cref="GifEncoder"/>, which only writes
/// compliant data: a four-entry Global Color Table, LZW minimum code size 8 (so a literal code is 9
/// bits and the code width does not grow in these short streams), and codes given as integers.
/// </summary>
public sealed class GifPlacedRuleTests
{
    private const int Clear = 256;
    private const int EndOfInformation = 257;

    private static readonly GifLoadOptions Placed = new() { PlaceOnLogicalScreen = true };

    private static byte[] Gce(byte blockSize = 4, byte[]? payload = null)
    {
        // Extension introducer, Graphic Control label, Block Size, data, Block Terminator. The
        // default payload has the transparency flag clear, so a valid block changes no pixel.
        var data = payload ?? [0x00, 0x00, 0x00, 0x00];
        return [0x21, 0xF9, blockSize, .. data, 0x00];
    }

    private static byte[] PackNineBitCodes(int[] codes)
    {
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var code in codes)
        {
            buffer |= code << bits;
            bits += 9;
            while (bits >= 8)
            {
                bytes.Add((byte)buffer);
                buffer >>= 8;
                bits -= 8;
            }
        }

        if (bits > 0)
            bytes.Add((byte)buffer);
        return [.. bytes];
    }

    /// <summary>A one-frame file: a 4-entry palette, the given blocks before the image, and the given codes.</summary>
    private static byte[] File(
        int width, int height, int[] codes, byte[]? beforeImage = null, bool blockTerminator = true, bool trailer = true)
    {
        var data = PackNineBitCodes(codes);
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8)]);
        bytes.AddRange([0x81, 0x00, 0x00]);                                   // 4-entry Global Color Table
        bytes.AddRange([200, 10, 20, 30, 220, 40, 50, 60, 230, 240, 250, 70]);
        if (beforeImage is not null)
            bytes.AddRange(beforeImage);
        bytes.Add(0x2C);
        bytes.AddRange([0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x00]);
        bytes.Add(8);                                                          // LZW minimum code size
        bytes.Add((byte)data.Length);
        bytes.AddRange(data);
        if (blockTerminator)
            bytes.Add(0x00);
        if (trailer)
            bytes.Add(0x3B);
        return [.. bytes];
    }

    private static int[] Stream(params int[] literals) => [Clear, .. literals, EndOfInformation];

    private static void AssertRefusedOnlyWithTheOption(byte[] gif, string expectedFragment)
    {
        // The default path returns an image for the same bytes, as it does on 2.3.3.
        var plain = GifImageLoader.Load(gif);
        Assert.True(plain.Width > 0 && plain.Height > 0);

        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif, Placed));
        Assert.Contains(expectedFragment, ex.Message);
    }

    /// <summary>The control for every refusal below: a compliant file of the same shape loads both ways.</summary>
    [Fact]
    public void CompliantFile_loadsWithAndWithoutTheOption()
    {
        var gif = File(2, 2, Stream(0, 1, 2, 3));

        var plain = GifImageLoader.Load(gif);
        var placed = GifImageLoader.Load(gif, Placed);

        Assert.Equal((2, 2), (plain.Width, plain.Height));
        Assert.Equal((2, 2), (placed.Width, placed.Height));
    }

    // ------------------------------------------------ section 22.a: index within the colour table

    /// <summary>Four entries: index 3 is the last valid one (loads), 4 is past the table (refused).</summary>
    [Fact]
    public void Place_indexAtTheEndOfTheColourTable_loads()
    {
        var gif = File(2, 2, Stream(0, 1, 2, 3));

        Assert.Equal((2, 2), (GifImageLoader.Load(gif, Placed).Width, GifImageLoader.Load(gif, Placed).Height));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(200)]
    [InlineData(255)]
    public void Place_indexPastTheColourTable_isRefusedAndDefaultLoads(int index)
    {
        var gif = File(2, 2, Stream(0, 1, 2, index));

        AssertRefusedOnlyWithTheOption(gif, $"pixel (1,1) has colour index {index}, past the 4-entry active colour table (section 22.a");
    }

    /// <summary>A local table is the active one, so the range is its size, not the global table's.</summary>
    [Fact]
    public void Place_indexIsCheckedAgainstTheLocalColourTable()
    {
        // The same 4-entry global table, but a 2-entry local table: index 2 is valid for the global
        // table and past the local one.
        var global = File(2, 1, Stream(0, 2));
        var descriptor = 13 + 12;
        Assert.Equal(0x2C, global[descriptor]);
        byte[] localTable = [1, 2, 3, 4, 5, 6];
        var local = global[..(descriptor + 10)].Concat(localTable).Concat(global[(descriptor + 10)..]).ToArray();
        local[descriptor + 9] = 0x80;   // Local Color Table flag, size field 0: 2 entries

        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(local, Placed));

        Assert.Contains("colour index 2, past the 2-entry active colour table", ex.Message);
        Assert.Equal((2, 1), (GifImageLoader.Load(local).Width, GifImageLoader.Load(local).Height));
    }

    /// <summary>
    /// A 5 x 4 frame of four colours (20 pixels) is the file whose last code widens the decoder's
    /// table: <see cref="GifEncoder"/> writes the End of Information code one bit narrower than that
    /// width. The specification does not fix the width at that boundary, so the loader reads both,
    /// and the library's own output loads under the option.
    /// </summary>
    [Fact]
    public void Place_encoderOutputWhoseLastCodeWidensTheTable_loads()
    {
        string[] rows = ["ABCDA", "BCDAB", "CDABC", "DABCD"];
        byte[][] colours = [[200, 10, 20], [30, 220, 40], [50, 60, 230], [240, 250, 70]];
        var rgb = new byte[5 * 4 * 3];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 5; x++)
                colours[rows[y][x] - 'A'].CopyTo(rgb, (y * 5 + x) * 3);
        }

        var gif = GifEncoder.Encode(rgb, 5, 4);

        var placed = GifImageLoader.Load(gif, Placed);
        Assert.Equal((5, 4), (placed.Width, placed.Height));
    }

    // ------------------------------------------------ section 22.a and Appendix F: pixel count

    /// <summary>The reviewer's file: a 1x1 frame whose stream codes 4 pixels.</summary>
    [Fact]
    public void Place_streamCodingMorePixelsThanDeclared_isRefusedAndDefaultLoads()
    {
        var gif = File(1, 1, Stream(0, 1, 2, 3));

        AssertRefusedOnlyWithTheOption(gif, "decodes more pixels than the 1 its descriptor declares: a code follows the last pixel");
        Assert.Equal((1, 1), (GifImageLoader.Load(gif).Width, GifImageLoader.Load(gif).Height));
    }

    /// <summary>
    /// The overrun can sit inside one code: 0, 1, then the table entry 258 ("0 1") makes 4 pixels
    /// for a 3-pixel frame, with the 4th produced by the last code rather than by a code after it.
    /// </summary>
    [Fact]
    public void Place_lastCodeRunsPastTheDeclaredCount_isRefusedAndDefaultLoads()
    {
        var gif = File(3, 1, Stream(0, 1, 258));

        AssertRefusedOnlyWithTheOption(gif, "decodes more pixels than the 3 its descriptor declares: the last code runs past it");
    }

    /// <summary>A Clear code carries no pixel, so it may precede the End of Information code.</summary>
    [Fact]
    public void Place_clearCodeBeforeEndOfInformation_loads()
    {
        var gif = File(2, 1, [Clear, 0, 1, Clear, EndOfInformation]);

        Assert.Equal((2, 1), (GifImageLoader.Load(gif, Placed).Width, GifImageLoader.Load(gif, Placed).Height));
    }

    // ------------------------------------------------ Appendix F: End of Information, section 22.c: terminator

    [Fact]
    public void Place_noEndOfInformationCode_isRefusedAndDefaultLoads()
    {
        var gif = File(2, 1, [Clear, 0, 1]);

        AssertRefusedOnlyWithTheOption(gif, "ends without an End of Information code (Appendix F");
    }

    [Fact]
    public void Place_noBlockTerminator_isRefusedAndDefaultLoads()
    {
        var gif = File(2, 2, Stream(0, 1, 2, 3), blockTerminator: false, trailer: false);

        AssertRefusedOnlyWithTheOption(gif, "not ended by a Block Terminator (section 22.c");
    }

    // ------------------------------------------------ section 23: Graphic Control Extension

    [Fact]
    public void Place_oneWellFormedGraphicControlExtension_loads()
    {
        var gif = File(2, 2, Stream(0, 1, 2, 3), beforeImage: Gce());

        Assert.Equal((2, 2), (GifImageLoader.Load(gif, Placed).Width, GifImageLoader.Load(gif, Placed).Height));
    }

    /// <summary>
    /// A comment extension is not a graphic-rendering block and does not close the Graphic Control
    /// Extension's scope; a Plain Text Extension is one, and does. So a second Graphic Control
    /// Extension after the plain text is the grammar's next <c>Graphic Block</c>.
    /// </summary>
    [Fact]
    public void Place_secondGraphicControlExtensionAfterAPlainText_loads()
    {
        byte[] plainText = [0x21, 0x01, 0x0C, 0, 0, 0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 0x00];
        var gif = File(2, 2, Stream(0, 1, 2, 3), beforeImage: [.. Gce(), .. plainText, .. Gce()]);

        Assert.Equal((2, 2), (GifImageLoader.Load(gif, Placed).Width, GifImageLoader.Load(gif, Placed).Height));
    }

    [Fact]
    public void Place_twoGraphicControlExtensionsBeforeOneImage_isRefusedAndDefaultLoads()
    {
        byte[] comment = [0x21, 0xFE, 0x01, (byte)'x', 0x00];   // does not end the first extension's scope
        var gif = File(2, 2, Stream(0, 1, 2, 3), beforeImage: [.. Gce(), .. comment, .. Gce()]);

        AssertRefusedOnlyWithTheOption(gif, "two Graphic Control Extensions before one graphic rendering block (section 23.a");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void Place_graphicControlExtensionOfTheWrongBlockSize_isRefusedAndDefaultLoads(int size)
    {
        var gif = File(2, 2, Stream(0, 1, 2, 3), beforeImage: Gce((byte)size, new byte[size]));

        AssertRefusedOnlyWithTheOption(gif, "not one sub-block with a Block Size of 4 (section 23.a and 23.c.iii)");
    }

    [Fact]
    public void Place_graphicControlExtensionWithTwoSubBlocks_isRefusedAndDefaultLoads()
    {
        // A valid first sub-block, then a second one before the terminator.
        byte[] twoSubBlocks = [0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00];
        var gif = File(2, 2, Stream(0, 1, 2, 3), beforeImage: twoSubBlocks);

        AssertRefusedOnlyWithTheOption(gif, "not one sub-block with a Block Size of 4");
    }

    [Fact]
    public void Place_graphicControlExtensionWithNoData_isRefusedAndDefaultLoads()
    {
        byte[] empty = [0x21, 0xF9, 0x00];
        var gif = File(2, 2, Stream(0, 1, 2, 3), beforeImage: empty);

        AssertRefusedOnlyWithTheOption(gif, "not one sub-block with a Block Size of 4");
    }

    // ------------------------------------------------ Appendix B: the grammar's top-level blocks

    [Fact]
    public void Place_blockIntroducerOutsideTheGrammar_isRefusedAndDefaultLoads()
    {
        byte[] stray = [0x99, 0x00];
        var gif = File(2, 2, Stream(0, 1, 2, 3), beforeImage: stray);

        AssertRefusedOnlyWithTheOption(gif, "block introducer 0x99 at offset 25; Appendix B allows only");
    }
}
