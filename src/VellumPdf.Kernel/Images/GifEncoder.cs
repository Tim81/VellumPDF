// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Images;

/// <summary>
/// Writes a single-frame GIF89a file.
///
/// Implemented from the Graphics Interchange Format, Version 89a, CompuServe Incorporated,
/// 31 July 1990. The block order follows the grammar in Appendix B:
/// <c>Header &lt;Logical Screen&gt; &lt;Data&gt;* Trailer</c>, where the Data here is one
/// Table-Based Image, so the stream is Header, Logical Screen Descriptor, Global Color Table,
/// Image Descriptor, Image Data, Trailer.
///
/// The Graphics Interchange Format(c) is the Copyright property of CompuServe Incorporated.
/// GIF(sm) is a Service Mark property of CompuServe Incorporated. See the repository NOTICE.
///
/// <para><b>Lossless only.</b> GIF carries at most 256 colours per frame, and this encoder will
/// not choose which ones to discard. It builds the palette from the distinct colours actually
/// present and refuses an image holding more than 256 of them, rather than quantising silently.
/// That follows the same rule as the rest of the image path: preserve what the caller supplied,
/// and make any quality trade-off an explicit choice rather than a default.</para>
/// </summary>
public static class GifEncoder
{
    private const int MaxPaletteEntries = 256;
    private const int MaxCodeWidth = 12;
    private const int MaxTableSize = 1 << MaxCodeWidth;

    /// <summary>
    /// Encodes 8-bit RGB samples, three bytes per pixel in row order from the top-left, as a
    /// single-frame GIF89a.
    /// </summary>
    /// <param name="rgb">Pixel data, <paramref name="width"/> × <paramref name="height"/> × 3 bytes.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="interlaced">
    /// When true the rows are written in the four-pass order of Appendix E. The format allows it
    /// either way; it exists so a decoder can show a coarse image before the whole file arrives,
    /// which is of no benefit to a file read from disk.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="rgb"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="rgb"/> is not exactly <paramref name="width"/> × <paramref name="height"/> × 3
    /// bytes long; the image holds more than 256 distinct colours; or <paramref name="width"/> or
    /// <paramref name="height"/> exceeds 65535, the largest value the Logical Screen Descriptor
    /// (GIF89a §18) and Image Descriptor (§20) can hold.
    /// </exception>
    public static byte[] Encode(byte[] rgb, int width, int height, bool interlaced = false)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var expected = (long)width * height * 3;
        if (rgb.LongLength != expected)
        {
            throw new ArgumentException(
                $"Expected {expected} bytes of RGB for a {width}x{height} image, got {rgb.LongLength}.",
                nameof(rgb));
        }

        // Logical Screen Descriptor §18 and Image Descriptor §20 both hold the size as two
        // unsigned bytes, so neither dimension can exceed 65535. Checked ahead of the palette
        // below so that a raster too large names its own dimension rather than reporting the
        // colour-count constraint for an image that was going to be refused either way.
        if (width > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"GIF stores each dimension in two bytes, so a width of {width} cannot be written.",
                nameof(width));
        }
        if (height > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"GIF stores each dimension in two bytes, so a height of {height} cannot be written.",
                nameof(height));
        }

        var (indices, palette) = BuildExactPalette(rgb, width * height);

        // §18: the Size of Global Color Table field holds N, and the table holds 2^(N+1) entries.
        // The smallest N that covers the palette is used, and the table is padded out to it.
        var sizeField = 0;
        while ((2 << sizeField) < palette.Count && sizeField < 7) sizeField++;
        var tableEntries = 2 << sizeField;

        // Appendix F, under COMPRESSION, item 4: "The output codes are of variable length,
        // starting at <code size>+1 bits per
        // code". A code size of 1 would make the first available code 4 while the Clear code is 2,
        // which leaves one usable width; the format's own floor is 2, so a one- or two-colour
        // image is written with a code size of 2.
        var minCodeSize = Math.Max(2, sizeField + 1);

        using var ms = new MemoryStream();

        // Header §17.
        ms.Write("GIF89a"u8);

        // Logical Screen Descriptor §18.
        WriteUInt16(ms, width);
        WriteUInt16(ms, height);
        // Global Color Table Flag 1, Color Resolution 7 (8 bits per primary available), Sort Flag 0.
        ms.WriteByte((byte)(0x80 | (0x07 << 4) | sizeField));
        ms.WriteByte(0);   // Background Color Index
        ms.WriteByte(0);   // Pixel Aspect Ratio: 0 means no information given

        // Global Color Table §19, padded to the full 2^(N+1) entries.
        foreach (var c in palette)
        {
            ms.WriteByte((byte)(c >> 16));
            ms.WriteByte((byte)(c >> 8));
            ms.WriteByte((byte)c);
        }
        for (var i = palette.Count; i < tableEntries; i++)
        {
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
        }

        // Image Descriptor §20.
        ms.WriteByte(0x2C);
        WriteUInt16(ms, 0);        // Image Left Position
        WriteUInt16(ms, 0);        // Image Top Position
        WriteUInt16(ms, width);
        WriteUInt16(ms, height);
        // No Local Color Table, no Sort, interlace per the caller.
        ms.WriteByte((byte)(interlaced ? 0x40 : 0x00));

        // Table Based Image Data §22.
        ms.WriteByte((byte)minCodeSize);
        var ordered = interlaced ? ToInterlacedOrder(indices, width, height) : indices;
        WriteSubBlocks(ms, LzwEncode(ordered, minCodeSize));

        // Trailer §27.
        ms.WriteByte(0x3B);

        return ms.ToArray();
    }

    /// <summary>
    /// Maps every pixel to a palette index, building the palette from the distinct colours the
    /// image actually contains, in first-appearance order.
    /// </summary>
    private static (byte[] Indices, List<int> Palette) BuildExactPalette(byte[] rgb, int pixelCount)
    {
        var lookup = new Dictionary<int, byte>(MaxPaletteEntries);
        var palette = new List<int>(MaxPaletteEntries);
        var indices = new byte[pixelCount];

        for (var i = 0; i < pixelCount; i++)
        {
            var key = (rgb[i * 3] << 16) | (rgb[i * 3 + 1] << 8) | rgb[i * 3 + 2];
            if (!lookup.TryGetValue(key, out var index))
            {
                if (palette.Count == MaxPaletteEntries)
                {
                    throw new ArgumentException(
                        $"GIF holds at most {MaxPaletteEntries} colours per frame and this image has more. " +
                        "Reduce the image's colours before encoding; this encoder will not discard any.",
                        nameof(rgb));
                }
                index = (byte)palette.Count;
                palette.Add(key);
                lookup[key] = index;
            }
            indices[i] = index;
        }

        return (indices, palette);
    }

    /// <summary>
    /// Reorders rows into the four-pass storage order of Appendix E: every 8th row from row 0,
    /// then every 8th from row 4, then every 4th from row 2, then every 2nd from row 1. The
    /// inverse of <c>GifImageLoader</c>'s deinterlace.
    /// </summary>
    private static byte[] ToInterlacedOrder(byte[] display, int width, int height)
    {
        var storage = new byte[display.Length];
        ReadOnlySpan<int> starts = [0, 4, 2, 1];
        ReadOnlySpan<int> steps = [8, 8, 4, 2];

        var dst = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            for (var row = starts[pass]; row < height; row += steps[pass])
            {
                display.AsSpan(row * width, width).CopyTo(storage.AsSpan(dst * width, width));
                dst++;
            }
        }

        return storage;
    }

    /// <summary>
    /// GIF-variant LZW, Appendix F. Codes are packed least-significant-bit first.
    ///
    /// The width rule is the mirror of the decoder's and deliberately one step apart from it. The
    /// decoder adds the entry for the <em>previous</em> code, so it always trails the encoder by
    /// one: where the decoder grows as its next index reaches 2^width, the encoder has already
    /// assigned one more and grows as its own next index passes it. Writing the decoder with the
    /// encoder's condition is what made it read a code too narrow and reject ordinary files.
    /// </summary>
    private static byte[] LzwEncode(byte[] indices, int minCodeSize)
    {
        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;

        using var output = new MemoryStream();
        var bitBuf = 0;
        var bitsLeft = 0;
        var codeSize = minCodeSize + 1;

        void Emit(int code)
        {
            bitBuf |= code << bitsLeft;
            bitsLeft += codeSize;
            while (bitsLeft >= 8)
            {
                output.WriteByte((byte)(bitBuf & 0xFF));
                bitBuf >>= 8;
                bitsLeft -= 8;
            }
        }

        // The string table, keyed by (prefix code, next index). Root entries need no key.
        var table = new Dictionary<(int Prefix, byte Suffix), int>();
        var nextCode = eoiCode + 1;

        // Appendix F, under COMPRESSION, item 1: "Encoders should output a Clear code as the
        // first code of each image data stream." A should, not a shall: section 22, which defines
        // this block, says nothing about it, and its own Recommendations read "None". Both
        // decoders this was checked against, the one in this package and Pillow 12.3.0, read a
        // stream without it, so this follows the recommendation rather than guarding against a
        // refusal.
        Emit(clearCode);

        if (indices.Length == 0)
        {
            Emit(eoiCode);
            FlushBits();
            return output.ToArray();
        }

        int prefix = indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var k = indices[i];
            if (table.TryGetValue((prefix, k), out var combined))
            {
                prefix = combined;
                continue;
            }

            Emit(prefix);

            if (nextCode < MaxTableSize)
            {
                table[(prefix, k)] = nextCode;
                nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < MaxCodeWidth)
                    codeSize++;
            }
            else
            {
                // The table is full. The cover sheet to the specification says the encoder may
                // either keep using the table as it stands or clear it, and that a decoder must
                // not change its own table until a Clear arrives. Clearing is the older and more
                // widely understood behaviour, so that is what is written.
                Emit(clearCode);
                table.Clear();
                nextCode = eoiCode + 1;
                codeSize = minCodeSize + 1;
            }

            prefix = k;
        }

        Emit(prefix);
        Emit(eoiCode);
        FlushBits();
        return output.ToArray();

        void FlushBits()
        {
            if (bitsLeft > 0) output.WriteByte((byte)(bitBuf & 0xFF));
        }
    }

    /// <summary>
    /// Writes the compressed stream as the length-prefixed sub-blocks of §15, at most 255 data
    /// bytes each, terminated by a Block Terminator.
    /// </summary>
    private static void WriteSubBlocks(Stream target, byte[] data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var take = Math.Min(255, data.Length - offset);
            target.WriteByte((byte)take);
            target.Write(data, offset, take);
            offset += take;
        }
        target.WriteByte(0);
    }

    private static void WriteUInt16(Stream target, int value)
    {
        target.WriteByte((byte)(value & 0xFF));
        target.WriteByte((byte)((value >> 8) & 0xFF));
    }
}
