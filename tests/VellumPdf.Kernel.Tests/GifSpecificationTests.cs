// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// The GIF decoder and encoder against the Graphics Interchange Format, Version 89a, CompuServe
/// Incorporated, 31 July 1990, which the repository now holds.
///
/// Three defects motivated the file, and all three were invisible to tests that only asked
/// whether a file loaded, because a single flat colour survives all three.
///
/// Two kinds of case live here, and the distinction matters when reading a failure. The interlace
/// order, the encoder's block structure and its code stream are known answers: the expected value
/// is written out here. The rest compare the decoder's output with the indices a fixture was built
/// from, which is agreement between this file's encoder and the package's decoder rather than
/// against a third party. That agreement is only worth something because the two are independent
/// at the point that matters: <see cref="EncodeLzw"/> grows its code width one step ahead of the
/// decoder's bookkeeping, which is exactly the distinction the original defect erased. The older
/// fixture helper in <c>ImageFormatTests</c> shared the decoder's error and so agreed with it and
/// with nothing else.
///
/// No independent codec runs in this suite. Cross-checks against one are run outside it, against
/// the corpus the CHANGELOG entry for #490 reports.
/// </summary>
public sealed class GifSpecificationTests
{
    /// <summary>
    /// Appendix F, under COMPRESSION, item 4: "Whenever the LZW code value would exceed the
    /// current code length, the code length is increased by one." A width of n expresses 0..2^n-1,
    /// so the width must grow as the next code to assign reaches 2^n.
    ///
    /// The decoder grew one code later, so as soon as an image's dictionary passed that boundary it
    /// read a code too narrow, desynchronised, and threw "Invalid GIF LZW code". A flat colour
    /// never reaches a boundary, so it decoded; past that the outcome depended on the content.
    ///
    /// This fixture is 300 pixels over 8 palette entries, so the minimum code size is 3 and the
    /// first boundary is at 16, not at 512. Walking the stream it produces: the table peaks at 82
    /// entries and the width grows three times, at 16, 32 and 64. Three crossings is what makes
    /// the case discriminating; the count is measured on this fixture, not assumed from its size.
    /// </summary>
    [Fact]
    public void Decode_imageWhoseDictionaryPassesTheWidthBoundary_isNotRefused()
    {
        var pixels = new byte[300];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i * 7 + i / 5) % 8);

        var gif = BuildGif(pixels, width: 20, height: 15, paletteEntries: 8, interlaced: false);
        var rgb = DecodeToIndices(gif, 20, 15, paletteEntries: 8);

        Assert.Equal(pixels, rgb);
    }

    /// <summary>
    /// Appendix E gives the four-pass row order of an interlaced image: every 8th row from row 0,
    /// then every 8th from row 4, then every 4th from row 2, then every 2nd from row 1.
    ///
    /// The decoder never read the interlace flag, so it returned the rows in storage order. This
    /// fixture is one pixel wide and eight rows tall with a distinct index per row, so any
    /// permutation is visible directly.
    ///
    /// No row's expected value is its own row number, deliberately. The destination buffer starts
    /// zeroed, so with values 0..7 in display order a pass that never writes row 0 still reads
    /// back 0 there and the test passes: changing the first pass's start row from 0 to 1 left it
    /// green. Shifting every value by one removes that coincidence — row 0 expects 1, and an
    /// unwritten row reads 0.
    /// </summary>
    [Fact]
    public void Decode_interlacedImage_returnsRowsInDisplayOrder()
    {
        // Display row r holds index r+1 mod 8. Storage order for height 8 is rows 0, 4, 2, 6,
        // 1, 3, 5, 7 (Appendix E's four passes), so the stored sequence of those values is:
        byte[] storageOrder = [1, 5, 3, 7, 2, 4, 6, 0];

        var gif = BuildGif(storageOrder, width: 1, height: 8, paletteEntries: 8, interlaced: true);
        var got = DecodeToIndices(gif, 1, 8, paletteEntries: 8);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 0], got);
    }

    /// <summary>
    /// Appendix E's four passes step by 8, 8, 4 and 2, so at a height that is not a multiple of
    /// eight the last pass of each group stops part way and the pass boundaries fall differently.
    /// Height 8 alone leaves that arithmetic unexercised, and the round trip cannot cover it: the
    /// encoder and decoder share the pass tables, so a matched change to both stays green.
    ///
    /// Each row's expected index is again offset from its row number, for the reason above.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(15)]
    public void Decode_interlacedImageOfHeightNotAMultipleOfEight_returnsRowsInDisplayOrder(int height)
    {
        // Appendix E: every 8th row from 0, then every 8th from 4, then every 4th from 2,
        // then every 2nd from 1. Build the storage sequence the same way the spec reads.
        var order = new List<int>();
        int[] starts = [0, 4, 2, 1];
        int[] steps = [8, 8, 4, 2];
        for (var pass = 0; pass < 4; pass++)
            for (var row = starts[pass]; row < height; row += steps[pass])
                order.Add(row);

        Assert.Equal(height, order.Count);          // every row written exactly once
        Assert.Equal(height, order.Distinct().Count());

        var expected = new byte[height];
        for (var r = 0; r < height; r++) expected[r] = (byte)((r + 1) % 16);

        var storageOrder = order.Select(r => expected[r]).ToArray();
        var gif = BuildGif(storageOrder, width: 1, height: height, paletteEntries: 16, interlaced: true);

        Assert.Equal(expected, DecodeToIndices(gif, 1, height, paletteEntries: 16));
    }

    /// <summary>
    /// The code that is not yet in the table, conventionally KwKwK: its string is the previous
    /// string followed by that string's own first byte.
    ///
    /// The decoder appended that byte to the wrong end, emitting the byte before the string instead
    /// of after it. The damage is one pixel per occurrence, so it never threw and never scrambled a
    /// whole image; on a 48x48 image of three-pixel bars it was 120 pixels of 2304, each sitting on
    /// a bar boundary. A repeating run is what reaches the case, which is why this fixture repeats.
    /// </summary>
    [Fact]
    public void Decode_codeNotYetInTable_appendsTheRepeatedByteAtTheEnd()
    {
        // A long run of one index followed by a second index, repeated: the run builds entries and
        // the decoder meets a code one past the table on each lengthening.
        var pixels = new byte[120];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(i % 5 == 4 ? 1 : 0);

        var gif = BuildGif(pixels, width: 12, height: 10, paletteEntries: 4, interlaced: false);
        var got = DecodeToIndices(gif, 12, 10, paletteEntries: 4);

        Assert.Equal(pixels, got);
    }

    /// <summary>
    /// What the encoder writes, this decoder reads back unchanged, over content that reaches all
    /// three defects at once.
    ///
    /// The content is runs of three, not a stride. A stride of 13 over 251 colours, which is what
    /// this fixture held first, gives consecutive pixels that always differ, so there is never a
    /// repeated string to extend and the not-yet-in-table case is never reached — measured on the
    /// bytes the encoder produced for it: zero occurrences. Runs of three over 200 colours reach
    /// it 200 times in the same 37x23 frame, and still grow the code width once with the minimum
    /// code size at 8.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Encode_thenDecode_returnsTheOriginalPixels(bool interlaced)
    {
        const int w = 37, h = 23;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            var v = (byte)((i / 3) % 200);
            rgb[i * 3] = v;
            rgb[i * 3 + 1] = (byte)(v / 2);
            rgb[i * 3 + 2] = (byte)(255 - v);
        }

        var gif = GifEncoder.Encode(rgb, w, h, interlaced);
        var img = GifImageLoader.Load(gif);

        Assert.Equal(w, img.Width);
        Assert.Equal(h, img.Height);
        Assert.Equal(rgb, DecodeRgb(gif, w, h));
    }

    /// <summary>
    /// The encoder writes the block sequence of the Appendix B grammar:
    /// <c>Header &lt;Logical Screen&gt; &lt;Data&gt;* Trailer</c>, with the Data being one
    /// Table-Based Image. This pins the header, the separator and the trailer rather than trusting
    /// that a decoder somewhere accepts the result.
    /// </summary>
    [Fact]
    public void Encode_writesTheBlocksTheGrammarRequires()
    {
        var gif = GifEncoder.Encode([1, 2, 3, 250, 251, 252], width: 2, height: 1);

        Assert.Equal("GIF89a"u8.ToArray(), gif[..6]);
        Assert.Equal(0x2C, gif[13 + 3 * 2]);   // Image Descriptor, after a 2-entry global table
        Assert.Equal(0x3B, gif[^1]);           // Trailer
    }

    /// <summary>
    /// GIF carries at most 256 colours in a frame. The encoder refuses an image with more rather
    /// than choosing which to discard, in keeping with the rest of the image path, which never
    /// transcodes lossily without being asked.
    /// </summary>
    [Fact]
    public void Encode_moreThan256Colours_refusesRatherThanQuantising()
    {
        var rgb = new byte[257 * 3];
        for (var i = 0; i < 257; i++)
        {
            rgb[i * 3] = (byte)(i & 0xFF);
            rgb[i * 3 + 1] = (byte)(i >> 8);
            rgb[i * 3 + 2] = 0;
        }

        var ex = Assert.Throws<ArgumentException>(() => GifEncoder.Encode(rgb, 257, 1));
        Assert.Contains("at most 256 colours", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Appendix F, under COMPRESSION, item 1: the Clear code "can appear at any point in the
    /// image data stream and therefore requires the LZW algorithm to process succeeding codes as
    /// if a new data stream was starting" -- so the table and the code width both start again from
    /// it, not only at the beginning. Nothing in this package's own
    /// encoder emits one mid-stream, so the decoder's reset path was reachable by no test at all
    /// — removing the reset from the decoder left all 1,452 Kernel cases green while corrupting
    /// three quarters of a 512x512 file written elsewhere.
    ///
    /// The fixture emits a Clear after a set number of data codes, past the first width growth so
    /// the reset has a width to undo, and the decode must still be the original indices.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(37)]
    [InlineData(64)]
    public void Decode_midStreamClearCode_startsTheTableAgain(int clearAfter)
    {
        var pixels = new byte[600];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i / 3) % 8);

        var gif = BuildGif(pixels, width: 30, height: 20, paletteEntries: 8,
                           interlaced: false, clearAfter: clearAfter);

        // The fixture is only evidence if it really contains a second Clear.
        var codes = ReadCodes(gif);
        Assert.Equal(2, codes.Count(c => c == 1 << 3));

        Assert.Equal(pixels, DecodeToIndices(gif, 30, 20, paletteEntries: 8));
    }

    /// <summary>
    /// The code stream the encoder writes, read back as codes rather than as pixels. Appendix F,
    /// under COMPRESSION: a Clear code first (item 1, a should), an End of Information code last
    /// (item 2, "It must be the last code output by the encoder for an image"), and a width that
    /// starts at the code size plus one and runs "up to 12 bits per code. This defines a maximum
    /// code value of 4095" (item 4). Nothing there says what an encoder does when the table
    /// fills; item 1 lets a Clear code appear at any point, and starting the table again is the
    /// only way left to keep the width inside 12 bits, so that is what the assertion below reads.
    ///
    /// A round trip proves none of this: this decoder and this encoder share the convention, so a
    /// matched change to both stays green. Dropping the table-full Clear, dropping the leading
    /// Clear, moving the width growth either way and lowering the minimum code size each left the
    /// whole Kernel suite green while producing a file an independent decoder rejects.
    /// </summary>
    [Fact]
    public void Encode_writesACodeStreamAppendixFAccepts()
    {
        // A pseudo-random index per pixel: 150x150 of it fills the 4,096-entry table and so
        // forces the encoder past the point where it must start the table again.
        const int w = 150, h = 150;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            var v = (byte)((i * 1103515245 + 12345) >> 16);
            rgb[i * 3] = v;
            rgb[i * 3 + 1] = (byte)(v / 2);
            rgb[i * 3 + 2] = (byte)(255 - v);
        }

        var gif = GifEncoder.Encode(rgb, w, h);
        var minCodeSize = MinimumCodeSizeOf(gif);
        var clear = 1 << minCodeSize;
        var eoi = clear + 1;
        var codes = ReadCodes(gif);

        Assert.Equal(clear, codes[0]);                       // clause 1
        Assert.Equal(eoi, codes[^1]);                        // clause 2
        Assert.Equal(1, codes.Count(c => c == eoi));         // and nowhere else

        // Walk the table the way clause 4 describes and check every code fits the width in force
        // when it was written. A code wider than that is unreadable, which is the defect this
        // whole file exists for, in the other direction.
        var codeSize = minCodeSize + 1;
        var next = eoi + 1;
        var prev = -1;
        var tableFullResets = 0;
        foreach (var code in codes)
        {
            Assert.True(code < 1 << codeSize,
                $"code {code} needs more than {codeSize} bits");
            if (code == clear)
            {
                if (prev >= 0) tableFullResets++;
                codeSize = minCodeSize + 1;
                next = eoi + 1;
                prev = -1;
                continue;
            }
            if (code == eoi) break;
            if (prev >= 0 && next < 4096)
            {
                next++;
                if (next >= 1 << codeSize && codeSize < 12) codeSize++;
            }
            prev = code;
        }

        // The table really did fill, so the reset above is a measured event and not a guess.
        Assert.True(tableFullResets >= 1,
            "the fixture did not fill the table, so it proves nothing about the reset");
    }

    /// <summary>
    /// Appendix F, under ESTABLISH CODE SIZE: "black &amp; white images which have one color bit
    /// must be indicated as having a code size of 2." One bit would leave no room for the Clear
    /// and End of Information codes above the two palette entries, so the floor is not a rounding
    /// convenience.
    ///
    /// <c>Encode_writesTheBlocksTheGrammarRequires</c> reads the header, the separator and the
    /// trailer but never this byte, and lowering the floor to 1 left the whole Kernel suite green
    /// while producing a file an independent decoder calls truncated.
    /// </summary>
    [Fact]
    public void Encode_twoColourImage_writesMinimumCodeSizeTwo()
    {
        var gif = GifEncoder.Encode([0, 0, 0, 255, 255, 255], width: 2, height: 1);

        Assert.Equal(2, MinimumCodeSizeOf(gif));
    }

    /// <summary>
    /// Section 20.c.i: an image may carry its own colour table instead of using the screen's.
    /// No fixture anywhere in the tree set that flag, so the branch that reads a local table was
    /// exercised by nothing. This fixture omits the global table entirely, so taking the wrong
    /// one is a refusal rather than a wrong colour.
    /// </summary>
    [Fact]
    public void Decode_imageWithALocalColourTable_readsThatTable()
    {
        var pixels = new byte[64];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 8);

        var gif = BuildGif(pixels, width: 8, height: 8, paletteEntries: 8,
                           interlaced: false, localColorTable: true);

        Assert.Equal(pixels, DecodeToIndices(gif, 8, 8, paletteEntries: 8));
    }

    /// <summary>
    /// Section 17: the signature is followed by a version, and 87a files are still GIF. The
    /// loader accepts both, and nothing asserted the older one.
    /// </summary>
    [Fact]
    public void Decode_gif87aFile_isAccepted()
    {
        var pixels = new byte[16];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 4);

        var gif = BuildGif(pixels, width: 4, height: 4, paletteEntries: 4,
                           interlaced: false, version87a: true);

        Assert.Equal("GIF87a"u8.ToArray(), gif[..6]);
        Assert.Equal(pixels, DecodeToIndices(gif, 4, 4, paletteEntries: 4));
    }

    /// <summary>
    /// The fixture builder's own code stream, against the same reading of Appendix F the encoder
    /// is held to.
    ///
    /// This exists because of how the original defect survived. The fixture helper and the
    /// decoder shared one mistaken width rule, so they agreed with each other and with no other
    /// implementation, and every test built on that helper passed. <see cref="EncodeLzw"/> is
    /// written the other way round now, but its width rule is still derived from the decoder's
    /// bookkeeping, so agreement between them still proves less than it looks. Checking the
    /// fixture stream against the specification directly is what closes that: if the helper ever
    /// drifts back, this fails whether or not the decoder drifted with it.
    /// </summary>
    [Theory]
    [InlineData(4, 2)]
    [InlineData(8, 3)]
    [InlineData(256, 8)]
    public void BuildGif_writesACodeStreamAppendixFAccepts(int paletteEntries, int expectedMinCodeSize)
    {
        var pixels = new byte[600];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i / 3) % paletteEntries);

        var gif = BuildGif(pixels, width: 30, height: 20, paletteEntries: paletteEntries,
                           interlaced: false);

        var minCodeSize = MinimumCodeSizeOf(gif);
        Assert.Equal(expectedMinCodeSize, minCodeSize);

        var clear = 1 << minCodeSize;
        var eoi = clear + 1;
        var codes = ReadCodes(gif);

        Assert.Equal(clear, codes[0]);
        Assert.Equal(eoi, codes[^1]);
        Assert.Equal(1, codes.Count(c => c == eoi));

        var codeSize = minCodeSize + 1;
        var next = eoi + 1;
        var prev = -1;
        var growths = 0;
        foreach (var code in codes)
        {
            Assert.True(code < 1 << codeSize, $"code {code} needs more than {codeSize} bits");
            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = eoi + 1;
                prev = -1;
                continue;
            }
            if (code == eoi) break;
            Assert.True(code <= next, $"code {code} is beyond the table ({next})");
            if (prev >= 0 && next < 4096)
            {
                next++;
                if (next >= 1 << codeSize && codeSize < 12) { codeSize++; growths++; }
            }
            prev = code;
        }

        // A stream that never widens would satisfy everything above vacuously, which is exactly
        // what the old helper did.
        Assert.True(growths > 0, "the fixture never grew its code width, so it proves nothing");
    }

    // ── Malformed input ──────────────────────────────────────────────────────
    //
    // The class contract is InvalidDataException for anything malformed. Three reads walked
    // caller-supplied bytes without a bound and so raised IndexOutOfRangeException or
    // ArgumentOutOfRangeException instead, which a caller guarding on the documented type
    // cannot catch. Each case below is the smallest input that reaches its read.

    /// <summary>A file that ends on the extension separator, with no label byte behind it.</summary>
    [Fact]
    public void Decode_fileEndingOnAnExtensionSeparator_throwsInvalidDataException()
    {
        byte[] gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0, 0x21];

        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif));
        Assert.Contains("extension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A graphic control extension whose declared sub-block length runs past the end of the file.
    /// <c>GatherSubBlocks</c> made this check; the block-skipping path did not.
    /// </summary>
    [Fact]
    public void Decode_extensionSubBlockRunningPastTheEnd_throwsInvalidDataException()
    {
        byte[] gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0, 0x21, 0xF9, 0x40];

        Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif));
    }

    /// <summary>A file that ends exactly where the LZW minimum code size byte belongs.</summary>
    [Fact]
    public void Decode_fileEndingBeforeTheMinimumCodeSize_throwsInvalidDataException()
    {
        var full = BuildGif([0, 1, 2, 3], width: 2, height: 2, paletteEntries: 4, interlaced: false);
        // Everything up to and including the image descriptor's packed byte: header (6), screen
        // descriptor (7), a 4-entry global table (12), the separator (1) and the descriptor (9).
        var truncated = full[..(6 + 7 + 12 + 1 + 9)];

        Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(truncated));
    }

    /// <summary>
    /// Appendix F gives no way to say "fewer pixels than the descriptor promised" -- the End of
    /// Information code marks the end of the data, not a short count -- so a stream that stops
    /// early is malformed. The buffer is allocated at the promised size, and the pixels
    /// the stream never wrote stay at palette entry 0 — a colour the file chose for nothing.
    /// Discarding every byte of a 20x20 image's data used to return a full 400-pixel raster of
    /// entry 0 with no error at all.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(20)]
    public void Decode_imageDataEndingBeforeTheLastPixel_throwsInvalidDataException(int codeBytesKept)
    {
        var pixels = new byte[400];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 4);
        var full = BuildGif(pixels, width: 20, height: 20, paletteEntries: 4, interlaced: false);

        // The minimum code size byte sits after the header (6), the screen descriptor (7), a
        // 4-entry global table (12), the separator (1) and the descriptor (9). The sub-blocks
        // follow it, and the first one's length byte says how much data the file carries.
        var afterMinCodeSize = 6 + 7 + 12 + 1 + 9 + 1;
        Assert.Equal(2, full[afterMinCodeSize - 1]);        // the fixture's minimum code size

        // Cutting raw bytes here would leave a sub-block whose declared length runs past the end,
        // which an earlier guard refuses for a different reason. The short stream is reframed so
        // it stays well formed and the only thing wrong with it is that it stops early.
        var codeBytes = full[afterMinCodeSize..].AsSpan();
        var shortStream = codeBytesKept == 0
            ? (byte[])[0, 0x3B]                             // an empty sub-block chain, then the trailer
            : [(byte)codeBytesKept, .. codeBytes[1..(1 + codeBytesKept)], 0, 0x3B];

        byte[] truncated = [.. full[..afterMinCodeSize], .. shortStream];

        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(truncated));
        Assert.Contains("400 pixels", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Appendix F, under ESTABLISH CODE SIZE, puts the minimum code size at 2 or above; a palette
    /// index is a byte, so 8 is the ceiling. Below 2 there is no room for the Clear and End of
    /// Information codes above the palette entries. No test
    /// covered either end, and the loader's own message for it appeared in no assertion.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(12)]
    public void Decode_minimumCodeSizeOutsideTwoToEight_throwsInvalidDataException(int minCodeSize)
    {
        var gif = BuildGif([0, 1, 2, 3], width: 2, height: 2, paletteEntries: 4,
                           interlaced: false, minCodeSizeOverride: minCodeSize);

        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif));
        Assert.Contains("minimum code size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    /// <summary>The LZW minimum code size byte: the one the image descriptor is followed by.</summary>
    private static int MinimumCodeSizeOf(byte[] gif)
    {
        var pos = 13;
        if ((gif[10] & 0x80) != 0) pos += 3 * (2 << (gif[10] & 0x07));
        while (gif[pos] != 0x2C)
        {
            Assert.Equal(0x21, gif[pos]);   // the fixtures write no other block
            pos += 2;
            while (gif[pos] != 0) pos += gif[pos] + 1;
            pos++;
        }
        pos++;
        var packed = gif[pos + 8];
        pos += 9;
        if ((packed & 0x80) != 0) pos += 3 * (2 << (packed & 0x07));
        return gif[pos];
    }

    /// <summary>
    /// Reads the first image's LZW stream back as codes, tracking the width the way the decoder
    /// does, so a test can assert on the stream's structure rather than only on its pixels.
    /// </summary>
    private static List<int> ReadCodes(byte[] gif)
    {
        var pos = 13;
        if ((gif[10] & 0x80) != 0) pos += 3 * (2 << (gif[10] & 0x07));
        while (gif[pos] != 0x2C)
        {
            pos += 2;
            while (gif[pos] != 0) pos += gif[pos] + 1;
            pos++;
        }
        pos++;
        var packed = gif[pos + 8];
        pos += 9;
        if ((packed & 0x80) != 0) pos += 3 * (2 << (packed & 0x07));
        var minCodeSize = gif[pos++];

        using var data = new MemoryStream();
        while (gif[pos] != 0)
        {
            data.Write(gif, pos + 1, gif[pos]);
            pos += gif[pos] + 1;
        }
        var stream = data.ToArray();

        var clear = 1 << minCodeSize;
        var eoi = clear + 1;
        var codeSize = minCodeSize + 1;
        var next = eoi + 1;
        var prev = -1;
        var bitBuf = 0;
        var bitsLeft = 0;
        var at = 0;
        var codes = new List<int>();

        while (true)
        {
            while (bitsLeft < codeSize && at < stream.Length)
            {
                bitBuf |= stream[at++] << bitsLeft;
                bitsLeft += 8;
            }
            if (bitsLeft < codeSize) break;

            var code = bitBuf & ((1 << codeSize) - 1);
            bitBuf >>= codeSize;
            bitsLeft -= codeSize;
            codes.Add(code);

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = eoi + 1;
                prev = -1;
                continue;
            }
            if (code == eoi) break;
            if (prev >= 0 && next < 4096)
            {
                next++;
                if (next >= 1 << codeSize && codeSize < 12) codeSize++;
            }
            prev = code;
        }

        return codes;
    }


    /// <summary>
    /// Builds a GIF around the given palette indices, in storage order. A grey ramp palette is
    /// used so an index maps to a value the test can read back without a colour table of its own.
    /// </summary>
    private static byte[] BuildGif(
        byte[] indices, int width, int height, int paletteEntries, bool interlaced,
        int clearAfter = 0, bool localColorTable = false, bool version87a = false,
        int minCodeSizeOverride = -1)
    {
        var sizeField = 0;
        while ((2 << sizeField) < paletteEntries) sizeField++;
        var entries = 2 << sizeField;
        // A zero override has to reach the file, since zero is one of the values the loader must
        // refuse, so the sentinel for "not overridden" is negative rather than zero.
        var minCodeSize = minCodeSizeOverride >= 0 ? minCodeSizeOverride : Math.Max(2, sizeField + 1);

        using var ms = new MemoryStream();
        ms.Write(version87a ? "GIF87a"u8 : "GIF89a"u8);
        ms.WriteByte((byte)(width & 0xFF)); ms.WriteByte((byte)(width >> 8));
        ms.WriteByte((byte)(height & 0xFF)); ms.WriteByte((byte)(height >> 8));
        // Section 18: the global colour table flag, the colour resolution and the size field.
        // With a local table the global one is omitted entirely, so the decoder has to take the
        // palette from the image descriptor or produce nothing.
        ms.WriteByte((byte)(localColorTable ? (0x07 << 4) : (0x80 | (0x07 << 4) | sizeField)));
        ms.WriteByte(0);
        ms.WriteByte(0);
        if (!localColorTable)
            WriteGreyRamp(ms, entries);

        ms.WriteByte(0x2C);
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte((byte)(width & 0xFF)); ms.WriteByte((byte)(width >> 8));
        ms.WriteByte((byte)(height & 0xFF)); ms.WriteByte((byte)(height >> 8));
        // Section 20.c: bit 7 the local colour table flag, bit 6 interlace, bits 0-2 its size.
        ms.WriteByte((byte)((localColorTable ? 0x80 | sizeField : 0x00) | (interlaced ? 0x40 : 0x00)));
        if (localColorTable)
            WriteGreyRamp(ms, entries);

        ms.WriteByte((byte)minCodeSize);
        var lzw = EncodeLzw(indices, minCodeSize, clearAfter);
        for (var i = 0; i < lzw.Length; i += 255)
        {
            var take = Math.Min(255, lzw.Length - i);
            ms.WriteByte((byte)take);
            ms.Write(lzw, i, take);
        }
        ms.WriteByte(0);
        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    /// <summary>
    /// The grey ramp both colour tables use: entry i is (i, i, i), so a decoded red channel is
    /// the palette index the fixture was built from.
    /// </summary>
    private static void WriteGreyRamp(Stream ms, int entries)
    {
        for (var i = 0; i < entries; i++)
        {
            ms.WriteByte((byte)i); ms.WriteByte((byte)i); ms.WriteByte((byte)i);
        }
    }

    /// <summary>
    /// A conformant GIF LZW encoder. Its width rule is one step behind the decoder's on purpose:
    /// the decoder adds the entry for the previous code and so always trails by one, which is
    /// exactly the distinction the decoder defect came from.
    /// </summary>
    /// <param name="clearAfter">
    /// When positive, emit a Clear code after this many data codes and start the table again, as
    /// Appendix F, under COMPRESSION, item 1 permits at any point. Nothing in the package's own encoder produces
    /// one mid-stream, so the decoder's reset path has no other way to be reached.
    /// </param>
    private static byte[] EncodeLzw(byte[] indices, int minCodeSize, int clearAfter = 0)
    {
        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;
        var codeSize = minCodeSize + 1;
        var nextCode = eoiCode + 1;
        var emitted = 0;

        using var outMs = new MemoryStream();
        var bitBuf = 0;
        var bitsIn = 0;

        void Emit(int code)
        {
            bitBuf |= code << bitsIn;
            bitsIn += codeSize;
            while (bitsIn >= 8)
            {
                outMs.WriteByte((byte)(bitBuf & 0xFF));
                bitBuf >>= 8;
                bitsIn -= 8;
            }
        }

        var table = new Dictionary<(int, byte), int>();
        Emit(clearCode);

        int prefix = indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var k = indices[i];
            if (table.TryGetValue((prefix, k), out var found)) { prefix = found; continue; }
            Emit(prefix);
            emitted++;
            if (clearAfter > 0 && emitted == clearAfter)
            {
                // Flush the pending prefix, then reset exactly as a fresh stream starts.
                Emit(k);
                emitted++;
                Emit(clearCode);
                table.Clear();
                codeSize = minCodeSize + 1;
                nextCode = eoiCode + 1;
                if (i + 1 >= indices.Length) { Emit(eoiCode); goto done; }
                prefix = indices[++i];
                continue;
            }
            if (nextCode < 4096)
            {
                table[(prefix, k)] = nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            prefix = k;
        }

        Emit(prefix);
        Emit(eoiCode);
    done:
        if (bitsIn > 0) outMs.WriteByte((byte)(bitBuf & 0xFF));
        return outMs.ToArray();
    }

    /// <summary>Decodes and maps the grey-ramp palette back to the indices it stands for.</summary>
    private static byte[] DecodeToIndices(byte[] gif, int width, int height, int paletteEntries)
    {
        _ = paletteEntries;
        var rgb = DecodeRgb(gif, width, height);
        var indices = new byte[width * height];
        for (var i = 0; i < indices.Length; i++) indices[i] = rgb[i * 3];
        return indices;
    }

    private static byte[] DecodeRgb(byte[] gif, int width, int height)
    {
        var img = GifImageLoader.Load(gif);
        Assert.Equal(width, img.Width);
        Assert.Equal(height, img.Height);

        // The decoded raster is the image XObject's stream, deflated by PdfStream, and the only
        // way to its bytes from outside the package is to write the object and inflate what lands
        // between the stream keywords. ImageFormatTests reads it the same way.
        using var pdfMs = new MemoryStream();
        img.BuildStream().WriteTo(new VellumPdf.IO.PdfWriter(pdfMs));
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
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        throw new InvalidOperationException("stream marker not found");
    }
}
