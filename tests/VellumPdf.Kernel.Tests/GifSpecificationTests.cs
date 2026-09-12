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
/// against a third party.
///
/// That agreement proves less than it looks, and it is worth being exact about why.
/// <see cref="EncodeLzw"/> keeps its table one entry ahead of the decoder's, and applies the
/// width rule one step behind it, and those two offsets cancel: both sides change width at the
/// same position in the stream. So a matched drift in both is expressible and would pass every
/// round trip. The older fixture helper in <c>ImageFormatTests</c> was a matched drift of exactly
/// that kind, which is why it agreed with the decoder and with nothing else. What actually pins
/// the rule is <see cref="EncodeLzw_writesTheCodeSequenceAppendixFRequires"/>, whose expected
/// codes and bytes are literals derived from the specification by hand.
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
    /// green. Shifting every value by one removes that coincidence: row 0 expects 1, and an
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
    /// The decoder appended that byte to the wrong end, emitting the byte before the string
    /// instead of after it. Rotating a string that way moves every position from its first
    /// differing byte on, so the cost is the string's length rather than a single pixel: on a
    /// 48x48 image of three-pixel vertical bars over two colours, 26 occurrences cost 120 pixels
    /// of 2304, 4.6 each, every one on a bar boundary. It stays a local fault rather than a
    /// scrambled image only because the strings involved are short; one-pixel bars on the same
    /// frame lose 1,104 of 2,304.
    ///
    /// A repeating run is what reaches the case, which is why this fixture repeats.
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
    /// What the encoder writes, this decoder reads back unchanged. Two contents, because one
    /// cannot carry everything: reaching the not-yet-in-table case needs repeated strings, and a
    /// wide palette suppresses repetition.
    ///
    /// <c>WidePalette</c> is runs of three over 200 colours. It holds the minimum code size at 8
    /// and grows the code width, which a four-colour fixture cannot do.
    ///
    /// <c>RepeatedPairs</c> is alternating pairs over four colours. This is the one that
    /// discriminates the not-yet-in-table defect, and the distinction is worth writing down
    /// because the first content does not, despite reaching the case 200 times. That case stands
    /// for the previous string followed by that string's own first byte; put the byte at the
    /// front instead and you get the first byte followed by the string. When the string is a run
    /// of one symbol those are the same string, so runs of three reach the case constantly and
    /// produce identical pixels either way. Measured on the encoder's own bytes: 200
    /// occurrences, 0 pixels different. A two-symbol string is the shortest for which the two
    /// differ, and alternating pairs give 18 occurrences and 200 differing pixels of 851
    /// progressive, 13 and 128 interlaced.
    /// </summary>
    [Theory]
    [InlineData(Content.WidePalette, false)]
    [InlineData(Content.WidePalette, true)]
    [InlineData(Content.RepeatedPairs, false)]
    [InlineData(Content.RepeatedPairs, true)]
    public void Encode_thenDecode_returnsTheOriginalPixels(Content content, bool interlaced)
    {
        const int w = 37, h = 23;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            var v = content switch
            {
                Content.WidePalette => (byte)((i / 3) % 200),
                Content.RepeatedPairs => (byte)(((i / 2) % 2) * 2 + (i % 2)),
                _ => throw new ArgumentOutOfRangeException(nameof(content)),
            };
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

    /// <summary>The two contents of <see cref="Encode_thenDecode_returnsTheOriginalPixels"/>.</summary>
    public enum Content
    {
        /// <summary>Runs of three over 200 colours: minimum code size 8, one width growth.</summary>
        WidePalette,

        /// <summary>Alternating pairs over four colours: discriminates the not-yet-in-table end.</summary>
        RepeatedPairs,
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
    /// Section 18 and section 20 both hold a dimension in two bytes, so 65536 cannot be written.
    /// The check runs before <see cref="GifEncoder"/> builds a palette, so an oversized raster is
    /// refused for its own size and names the dimension actually at fault rather than the
    /// unrelated <c>rgb</c> array.
    /// </summary>
    [Theory]
    [InlineData(65536, 1, "width")]
    [InlineData(1, 65536, "height")]
    public void Encode_dimensionExceeds65535_namesTheOffendingDimension(int width, int height, string expectedParamName)
    {
        var rgb = new byte[(long)width * height * 3];

        var ex = Assert.Throws<ArgumentException>(() => GifEncoder.Encode(rgb, width, height));

        Assert.Equal(expectedParamName, ex.ParamName);
    }

    /// <summary>
    /// Section 18's Size of Global Color Table field holds N so the table can hold 2^(N+1)
    /// entries, and <see cref="GifEncoder"/> grows N only as far as the palette needs: one entry
    /// short and the last colour has no slot. A palette of 2^k+1 colours is exactly that boundary,
    /// one past the table size a smaller N would give, and no fixture anywhere in this file used
    /// one: the encoder's own palettes elsewhere are 2, 4, 200, 256 and the 257 that gets refused,
    /// none of them 2^k+1. A table one bit short truncates the last colour, and this package's own
    /// decoder then refuses the file it just wrote.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(65)]
    [InlineData(129)]
    public void Encode_thenDecode_paletteSizeOneMoreThanAPowerOfTwo_roundTrips(int paletteSize)
    {
        var rgb = new byte[paletteSize * 3];
        for (var i = 0; i < paletteSize; i++)
        {
            rgb[i * 3] = (byte)i;
            rgb[i * 3 + 1] = (byte)(i / 2);
            rgb[i * 3 + 2] = (byte)(255 - i);
        }

        var gif = GifEncoder.Encode(rgb, paletteSize, 1);

        Assert.Equal(rgb, DecodeRgb(gif, paletteSize, 1));
    }

    /// <summary>
    /// Appendix F, under COMPRESSION, item 1: the Clear code "can appear at any point in the
    /// image data stream and therefore requires the LZW algorithm to process succeeding codes as
    /// if a new data stream was starting," so the table and the code width both start again from
    /// it, not only at the beginning.
    ///
    /// This package's encoder does emit one mid-stream, when the table fills, and
    /// <see cref="Encode_writesACodeStreamAppendixFAccepts"/> asserts that it does. What no test
    /// reached was the decoder's own reset. Measured before this file existed, on the commit that
    /// added the encoder: removing the reset left all 1,456 Kernel cases green, while the decode
    /// of a 512x512 image that encoder wrote lost 230,850 of its 262,144 pixels. Repeating that
    /// experiment now fails the three cases below instead, which is the point of them.
    ///
    /// The fixture emits a Clear after a set number of data codes, past the first width growth so
    /// the reset has a width to undo, and the decode must still be the original indices.
    ///
    /// The first four values are chosen, not arbitrary. Swept over 1 to 130 on this fixture, the
    /// helper's own earlier defect, skipping the table entry and the width growth before the
    /// Clear, produces a stream that fails to decode at exactly eight values: 6, 7, 22, 23, 54,
    /// 55, 118 and 119. Every other value produces different bytes that still decode, so a test
    /// using one of those cannot fail if the defect returns. The last value, 20, is one of those
    /// and is kept deliberately, as the ordinary case where a mid-stream Clear is simply read.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(23)]
    [InlineData(55)]
    [InlineData(119)]
    [InlineData(20)]
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
    /// code value of 4095" (item 4). The cover sheet gives an encoder two options once the table
    /// fills: hold it at the maximum code size and keep using it as it stands, or clear it, and
    /// item 1 lets a Clear code appear at any point. This package's encoder takes the second
    /// option, the older and more widely understood one, and that is what the assertion below reads.
    ///
    /// A round trip proves none of this: this decoder and this encoder share the convention, so a
    /// matched change to both stays green. Dropping the table-full Clear, dropping the leading
    /// Clear, moving the width growth either way and lowering the minimum code size each left the
    /// whole Kernel suite green while producing a file Pillow rejects.
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

        Assert.Equal(clear, codes[0]);                       // COMPRESSION item 1
        Assert.Equal(eoi, codes[^1]);                        // COMPRESSION item 2
        Assert.Equal(1, codes.Count(c => c == eoi));         // and nowhere else

        // Walk the table the way COMPRESSION item 4 describes and check every code fits the width in force
        // when it was written. A code wider than that is unreadable, which is the defect this
        // whole file exists for, in the other direction.
        // What can and cannot be asserted here is worth stating, because the obvious assertion
        // is worthless. ReadCodes masks each code to the width it is itself tracking, so
        // "no code is wider than the current width" is true however the encoder behaves, and a
        // matched drift in both would pass it. What does discriminate is the table bound: a code
        // at or above the next free entry cannot be resolved by any decoder, and it fires when
        // GifEncoder's width rule moves in either direction: measured, five failures each way.
        //
        // No literal known answer covers GifEncoder. The one in
        // EncodeLzw_writesTheCodeSequenceAppendixFRequires goes through BuildGif, so it pins the
        // fixture encoder in this file and is blind to the production one. The table bound below
        // is what stands between GifEncoder and a silent width drift.
        var next = eoi + 1;
        var prev = -1;
        var tableFullResets = 0;
        var codeSize = minCodeSize + 1;
        foreach (var code in codes)
        {
            if (code == clear)
            {
                if (prev >= 0) tableFullResets++;
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
    /// must be indicated as having a code size of 2." A code size of 1 starts the width at 2
    /// bits, which does fit Clear (2) and End of Information (3); what does not fit is the first
    /// free code, 4, which needs a third bit. So the floor is not a rounding convenience.
    ///
    /// <c>Encode_writesTheBlocksTheGrammarRequires</c> reads the header, the separator and the
    /// trailer but never this byte, and lowering the floor to 1 left the whole Kernel suite green
    /// while producing a file Pillow calls truncated.
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

    // ── Graphic Control Extension scope (section 12, section 23) ────────────

    /// <summary>
    /// Section 23: a Graphic Control Extension's scope is "the first graphic rendering block to
    /// follow". Section 12 sorts block labels into three ranges so a decoder can tell a scope's
    /// end even from a block it does not otherwise parse, and the Plain Text Extension, 0x01,
    /// sits in the Graphic-Rendering range that closes it.
    ///
    /// Nothing reset the pending transparent index when that intervening block went by, so a
    /// transparency meant for one image leaked onto the next. The fixture puts a transparent
    /// index of 1 on a Graphic Control Extension, a Plain Text Extension after it, and an image
    /// descriptor whose pixels include that index: without the fix this decodes with a soft mask
    /// alternating 0/255, and with it there is no soft mask at all, because nothing between the
    /// extension and this image descriptor still claims that index.
    /// </summary>
    [Fact]
    public void Decode_graphicRenderingExtension_closesAPendingTransparentIndex()
    {
        byte[] indices = [1, 2, 1, 2, 1, 2, 1];
        var gif = BuildGifWithGceThenPlainTextThenImage(indices, transparentFlag: 1, transparentIndex: 1, secondGce: false);

        var img = GifImageLoader.Load(gif);

        Assert.Null(img.SMask);
    }

    /// <summary>
    /// The same leak survives a second, conforming Graphic Control Extension placed right before
    /// the image descriptor, if that extension's own transparency flag is clear and nothing resets
    /// the index on that path either: the stale value from the first extension keeps winning.
    /// This and the case above are the two measured failures behind the fix; both must be gone.
    /// </summary>
    [Fact]
    public void Decode_gceWithTransparencyFlagClear_resetsAStaleIndexFromAnEarlierExtension()
    {
        byte[] indices = [1, 2, 1, 2, 1, 2, 1];
        var gif = BuildGifWithGceThenPlainTextThenImage(indices, transparentFlag: 1, transparentIndex: 1, secondGce: true);

        var img = GifImageLoader.Load(gif);

        Assert.Null(img.SMask);
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

        // No assertion on the code width here, for the reason given in the sibling test: the
        // reader masks each code to the width it is tracking, so such an assertion cannot fail.
        // The table bound can, and does.
        var codeSize = minCodeSize + 1;
        var next = eoi + 1;
        var prev = -1;
        var growths = 0;
        foreach (var code in codes)
        {
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

    /// <summary>
    /// The exact codes, and the exact bytes, that Appendix F requires for one short input.
    ///
    /// Every other assertion about the code width in this file re-derives the width with the same
    /// state machine it is checking, so it cannot fail. This one does not derive anything: the
    /// sequence below was worked out from the specification by hand and is written in literally.
    ///
    /// The derivation, for four palette entries, so Clear is 4, End of Information is 5 and the
    /// first free code is 6 (COMPRESSION items 1 to 3), starting at three bits (item 4):
    /// <code>
    ///   Clear                                                         ->  4
    ///   [0]      emit 0,  add [0,1]   as 6
    ///   [1]      emit 1,  add [1,0]   as 7
    ///   [0,1]    emit 6,  add [0,1,0] as 8; next code 9 exceeds 8, so the width becomes 4
    ///   [0,1]    emit 6,  add [0,1,2] as 9
    ///   [2]      emit 2,  add [2,2]   as 10
    ///   [2,2]    emit 10, add [2,2,1] as 11
    ///   [1,0]    emit 7,  add [1,0,1] as 12
    ///   [1,0]    emit 7,  add [1,0,2] as 13
    ///   [2]      emit 2
    ///   End of Information                                            ->  5
    /// </code>
    /// The byte assertion is the stronger of the two, because it pins the width each code went
    /// out at as well as its value: the first four codes occupy three bits each and the rest
    /// four, and a rule that widened one code earlier or later would pack them differently even
    /// where the code values happened to agree.
    /// </summary>
    [Fact]
    public void EncodeLzw_writesTheCodeSequenceAppendixFRequires()
    {
        byte[] indices = [0, 1, 0, 1, 0, 1, 2, 2, 2, 1, 0, 1, 0, 2];

        var gif = BuildGif(indices, width: 14, height: 1, paletteEntries: 4, interlaced: false);

        Assert.Equal(2, MinimumCodeSizeOf(gif));
        Assert.Equal([4, 0, 1, 6, 6, 2, 10, 7, 7, 2, 5], ReadCodes(gif));
        Assert.Equal([0x44, 0x6C, 0xA2, 0x77, 0x52], ImageDataOf(gif));

        // And it is a real GIF, not only the right bytes.
        Assert.Equal(indices, DecodeToIndices(gif, 14, 1, paletteEntries: 4));
    }

    /// <summary>
    /// A sub-block chain whose declared length runs past the end of the file, in each of the two
    /// places that walk one. <c>SkipSubBlocks</c> handles an extension this decoder does not
    /// parse, and <c>GatherSubBlocks</c> handles the image data itself; the guard in the first
    /// was added without a test and the guard in the second had none either.
    /// </summary>
    [Theory]
    [InlineData(0xFE)]   // comment extension: skipped, so SkipSubBlocks walks it
    [InlineData(0xFF)]   // application extension: likewise
    [InlineData(0x01)]   // plain text extension: likewise
    public void Decode_skippedExtensionRunningPastTheEnd_throwsInvalidDataException(int label)
    {
        // Header, screen descriptor with no global table, then an extension whose first
        // sub-block claims 200 bytes and supplies two.
        byte[] gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0, 0x21, (byte)label, 200, 0x61, 0x62];

        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif));
        Assert.Contains("sub-block", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The image data's own sub-block chain, whose first block claims more bytes than the file
    /// holds. This is the read <c>Decode_extensionSubBlockRunningPastTheEnd</c> cites as its
    /// reference, and nothing exercised it.
    /// </summary>
    [Fact]
    public void Decode_imageSubBlockRunningPastTheEnd_throwsInvalidDataException()
    {
        var full = BuildGif([0, 1, 2, 3], width: 2, height: 2, paletteEntries: 4, interlaced: false);
        var afterMinCodeSize = 6 + 7 + 12 + 1 + 9 + 1;

        // Replace the data with a single block claiming 200 bytes and carrying three.
        byte[] gif = [.. full[..afterMinCodeSize], 200, 0x11, 0x22, 0x33, 0x3B];

        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif));
        Assert.Contains("sub-block", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two ways a stream can end short, told apart by the message.
    ///
    /// The decoder reaches its output-length guard from two exits: the bit buffer running dry,
    /// which means the data simply stopped, and an End of Information code arriving before the
    /// last pixel, which means the encoder said it was finished while the descriptor asked for
    /// more. The second is also the shape a code-width desynchronisation takes, so conflating
    /// them would let a decoder bug read as a bad file.
    ///
    /// Both messages carry the pixel counts, so asserting on those cannot tell them apart:
    /// swapping the two arms of the ternary that chooses between them left the whole suite green.
    /// This asserts the distinguishing clause.
    /// </summary>
    [Fact]
    public void Decode_earlyEndOfInformation_saysSoRatherThanBlamingTheData()
    {
        // A 2x2 descriptor, four pixels promised, and a stream of exactly Clear, one index, EOI.
        var full = BuildGif([0, 1, 2, 3], width: 2, height: 2, paletteEntries: 4, interlaced: false);
        var afterMinCodeSize = 6 + 7 + 12 + 1 + 9 + 1;

        // minimum code size 2, so codes are three bits: Clear = 4, index 0 = 0, EOI = 5.
        // Packed least-significant-bit first: 100 000 101 -> 0b01000100, 0b00000001.
        byte[] gif = [.. full[..afterMinCodeSize], 2, 0b0100_0100, 0b0000_0001, 0, 0x3B];

        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif));
        Assert.Contains("1 of 4 pixels", ex.Message, StringComparison.Ordinal);
        Assert.Contains("End of Information", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other exit: a well-formed but short sub-block chain with no End of Information code at
    /// all. The message must <em>not</em> mention one, or the two exits are indistinguishable.
    /// </summary>
    [Fact]
    public void Decode_dataRunningOut_doesNotBlameAnEndOfInformationCode()
    {
        var full = BuildGif([0, 1, 2, 3], width: 2, height: 2, paletteEntries: 4, interlaced: false);
        var afterMinCodeSize = 6 + 7 + 12 + 1 + 9 + 1;

        // Clear then one index, and then nothing: 100 000 -> 0b00000100.
        byte[] gif = [.. full[..afterMinCodeSize], 1, 0b0000_0100, 0, 0x3B];

        var ex = Assert.Throws<InvalidDataException>(() => GifImageLoader.Load(gif));
        Assert.Contains("of 4 pixels", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("End of Information", ex.Message, StringComparison.Ordinal);
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
    /// Appendix F gives no way to say "fewer pixels than the descriptor promised." The End of
    /// Information code marks the end of the data, not a short count, so a stream that stops
    /// early is malformed. The buffer is allocated at the promised size, and the pixels
    /// the stream never wrote stay at palette entry 0, a colour the file chose for nothing.
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
    /// index is a byte, so 8 is the ceiling. Below 2, the starting width still fits Clear and End
    /// of Information; what does not fit is the first free code, one bit wider than either of
    /// them. No test covered either end, and the loader's own message for it appeared in no
    /// assertion.
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

    /// <summary>The concatenated image-data sub-blocks, without their length prefixes.</summary>
    private static byte[] ImageDataOf(byte[] gif)
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
        pos++;   // the minimum code size byte

        using var ms = new MemoryStream();
        while (gif[pos] != 0)
        {
            ms.Write(gif, pos + 1, gif[pos]);
            pos += gif[pos] + 1;
        }
        return ms.ToArray();
    }

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
    /// A hand-assembled fixture for the scope tests above: a Graphic Control Extension, a Plain
    /// Text Extension after it, optionally a second Graphic Control Extension right before the
    /// image descriptor, and then the image itself. <see cref="BuildGif"/> has no way to insert
    /// an extension at all, so this is built directly rather than through it.
    /// </summary>
    private static byte[] BuildGifWithGceThenPlainTextThenImage(
        byte[] indices, int transparentFlag, int transparentIndex, bool secondGce)
    {
        const int width = 7, height = 1;
        const int paletteEntries = 4; // covers indices 0-2, rounded to the nearest power of two
        const int minCodeSize = 2;

        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        ms.WriteByte((byte)(width & 0xFF)); ms.WriteByte((byte)(width >> 8));
        ms.WriteByte((byte)(height & 0xFF)); ms.WriteByte((byte)(height >> 8));
        ms.WriteByte((byte)(0x80 | (0x07 << 4) | 1)); // global table, 4 entries (size field 1)
        ms.WriteByte(0);
        ms.WriteByte(0);
        WriteGreyRamp(ms, paletteEntries);

        // Graphic Control Extension: the transparency this fixture means to test the scope of.
        ms.WriteByte(0x21); ms.WriteByte(0xF9);
        ms.WriteByte(4);
        ms.WriteByte((byte)transparentFlag);
        ms.WriteByte(0); ms.WriteByte(0); // delay time
        ms.WriteByte((byte)transparentIndex);
        ms.WriteByte(0); // block terminator

        // Plain Text Extension: a Graphic-Rendering block (section 12) this decoder does not
        // parse, whose scope-closing effect on the extension above is what these tests measure.
        // An empty sub-block chain is syntactically complete per section 15's own description of
        // a sub-block, and carries no data this decoder would read regardless.
        ms.WriteByte(0x21); ms.WriteByte(0x01);
        ms.WriteByte(0);

        if (secondGce)
        {
            // A second, conforming Graphic Control Extension with the transparency flag clear,
            // immediately before the image descriptor its own scope covers.
            ms.WriteByte(0x21); ms.WriteByte(0xF9);
            ms.WriteByte(4);
            ms.WriteByte(0);
            ms.WriteByte(0); ms.WriteByte(0);
            ms.WriteByte(0);
            ms.WriteByte(0);
        }

        ms.WriteByte(0x2C);
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte((byte)(width & 0xFF)); ms.WriteByte((byte)(width >> 8));
        ms.WriteByte((byte)(height & 0xFF)); ms.WriteByte((byte)(height >> 8));
        ms.WriteByte(0);

        ms.WriteByte((byte)minCodeSize);
        var lzw = EncodeLzw(indices, minCodeSize);
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
    /// A conformant GIF LZW encoder. Its width rule is one step behind the decoder's on purpose:
    /// the decoder adds the entry for the previous code and so always trails by one, which is
    /// exactly the distinction the decoder defect came from.
    /// </summary>
    /// <param name="clearAfter">
    /// When positive, emit a Clear code after this many data codes and start the table again, as
    /// Appendix F, under COMPRESSION, item 1 permits at any point. The package's own encoder does
    /// emit one when the table fills, so this is not the only way such a stream can arise, but
    /// it is the only way a test can produce one at a chosen point, early enough to be read
    /// without a 4,096-entry fixture.
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

            // The table entry and the width growth belong to the code just emitted, and a
            // decoder performs both whatever comes next, so they have to happen before any
            // Clear is written. Skipping them, which this did, left the decoder reading a code
            // wider than the width the fixture had reached, and clearAfter values 6, 7, 22, 23,
            // 54, 55, 118 and 119 produced a stream neither this decoder nor Pillow could read.
            if (nextCode < 4096)
            {
                table[(prefix, k)] = nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
            }

            if (clearAfter > 0 && emitted == clearAfter)
            {
                Emit(clearCode);
                table.Clear();
                codeSize = minCodeSize + 1;
                nextCode = eoiCode + 1;
                // k has not been emitted, so it begins the string after the reset, exactly as
                // the first index begins the string after the leading Clear.
                prefix = k;
                continue;
            }

            prefix = k;
        }

        Emit(prefix);
        Emit(eoiCode);
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
