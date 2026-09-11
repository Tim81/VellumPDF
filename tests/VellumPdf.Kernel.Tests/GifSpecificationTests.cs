// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// The GIF decoder and encoder against the Graphics Interchange Format, Version 89a, CompuServe
/// Incorporated, 31 July 1990, which the repository now holds.
///
/// Every case here is a known answer: the expected pixels are written out in the test rather than
/// compared against another decoder. Three defects motivated the file, and all three were invisible
/// to tests that only asked whether a file loaded, because the one image that survives all of them
/// is a single flat colour.
///
/// The fixtures are built by <see cref="EncodeLzw"/>, a real encoder, because the older fixture
/// helper in <c>ImageFormatTests</c> emitted literal codes at a fixed width and so was not a
/// conformant stream at all.
/// </summary>
public sealed class GifSpecificationTests
{
    /// <summary>
    /// Appendix F, clause 4: "Whenever the LZW code value would exceed the current code length, the
    /// code length is increased by one." A width of n expresses 0..2^n-1, so the width must grow
    /// as the next code to assign reaches 2^n.
    ///
    /// The decoder grew one code later, so as soon as an image's dictionary passed that boundary it
    /// read a code too narrow, desynchronised, and threw "Invalid GIF LZW code". Only images whose
    /// dictionary never reached the boundary decoded, which is why a flat colour worked and
    /// anything with detail did not.
    ///
    /// 300 pixels over 8 palette entries takes the dictionary well past the first boundary at 512.
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
    /// fixture is one pixel wide and eight rows tall with a distinct index per row, so the expected
    /// display order is simply 0..7 and any permutation is visible directly.
    /// </summary>
    [Fact]
    public void Decode_interlacedImage_returnsRowsInDisplayOrder()
    {
        // Storage order for height 8: rows 0, 8.. -> {0}; 4, 12.. -> {4}; 2, 6 -> {2,6};
        // 1, 3, 5, 7 -> {1,3,5,7}. So the stored sequence of display-row numbers is:
        byte[] storageOrder = [0, 4, 2, 6, 1, 3, 5, 7];

        var gif = BuildGif(storageOrder, width: 1, height: 8, paletteEntries: 8, interlaced: true);
        var got = DecodeToIndices(gif, 1, 8, paletteEntries: 8);

        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], got);
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
    /// What the encoder writes, this decoder reads back unchanged, over content that exercises all
    /// three defects at once: enough distinct values to pass a width boundary, repetition to reach
    /// the not-yet-in-table case, and both row orders.
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
            var v = (byte)((i * 13) % 251);
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

    // ── Fixture helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds a GIF around the given palette indices, in storage order. A grey ramp palette is
    /// used so an index maps to a value the test can read back without a colour table of its own.
    /// </summary>
    private static byte[] BuildGif(byte[] indices, int width, int height, int paletteEntries, bool interlaced)
    {
        var sizeField = 0;
        while ((2 << sizeField) < paletteEntries) sizeField++;
        var entries = 2 << sizeField;
        var minCodeSize = Math.Max(2, sizeField + 1);

        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        ms.WriteByte((byte)(width & 0xFF)); ms.WriteByte((byte)(width >> 8));
        ms.WriteByte((byte)(height & 0xFF)); ms.WriteByte((byte)(height >> 8));
        ms.WriteByte((byte)(0x80 | (0x07 << 4) | sizeField));
        ms.WriteByte(0);
        ms.WriteByte(0);
        for (var i = 0; i < entries; i++)
        {
            ms.WriteByte((byte)i); ms.WriteByte((byte)i); ms.WriteByte((byte)i);
        }

        ms.WriteByte(0x2C);
        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        ms.WriteByte((byte)(width & 0xFF)); ms.WriteByte((byte)(width >> 8));
        ms.WriteByte((byte)(height & 0xFF)); ms.WriteByte((byte)(height >> 8));
        ms.WriteByte((byte)(interlaced ? 0x40 : 0x00));

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
    private static byte[] EncodeLzw(byte[] indices, int minCodeSize)
    {
        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;
        var codeSize = minCodeSize + 1;
        var nextCode = eoiCode + 1;

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
            if (nextCode < 4096)
            {
                table[(prefix, k)] = nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
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
