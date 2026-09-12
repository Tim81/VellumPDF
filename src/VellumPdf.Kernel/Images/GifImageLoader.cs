// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Decodes the first frame of a GIF87a or GIF89a file and produces a FlateDecode Image XObject.
///
/// Features:
///   • LZW decompression (GIF variant, variable-width codes packed LSB-first).
///   • Global and local colour tables.
///   • Graphic Control Extension: transparent index → 8-bit /SMask.
///   • Animated GIFs: only the first image descriptor is decoded; subsequent frames are ignored.
///
/// Rejected: GIF with no image descriptor, malformed LZW streams (throws InvalidDataException).
/// </summary>
public static class GifImageLoader
{
    /// <summary>Decodes the first frame of a GIF into a FlateDecode Image XObject.</summary>
    public static PdfImageXObject Load(byte[] gifBytes)
    {
        if (gifBytes.Length < 13)
            throw new InvalidDataException("GIF data too small.");

        // Validate signature: "GIF87a" or "GIF89a"
        if (gifBytes[0] != 'G' || gifBytes[1] != 'I' || gifBytes[2] != 'F' ||
            gifBytes[3] != '8' || (gifBytes[4] != '7' && gifBytes[4] != '9') ||
            gifBytes[5] != 'a')
            throw new InvalidDataException("Not a GIF file.");

        // Logical screen descriptor
        var globalColorTableFlag = (gifBytes[10] & 0x80) != 0;
        var globalColorTableSize = 2 << (gifBytes[10] & 0x07); // 2^(n+1) entries

        byte[]? globalPalette = null;
        var pos = 13;

        if (globalColorTableFlag)
        {
            var tableBytes = globalColorTableSize * 3;
            if (pos + tableBytes > gifBytes.Length)
                throw new InvalidDataException("GIF global colour table extends beyond end of file.");
            globalPalette = gifBytes[pos..(pos + tableBytes)];
            pos += tableBytes;
        }

        // Walk the block stream until the first image descriptor
        int transparentIndex = -1;

        while (pos < gifBytes.Length)
        {
            var blockType = gifBytes[pos++];

            if (blockType == 0x3B) // Trailer
                break;

            if (blockType == 0x2C) // Image Descriptor
            {
                return DecodeImage(gifBytes, ref pos, globalPalette, transparentIndex);
            }

            if (blockType == 0x21) // Extension
            {
                // Every read below advances through caller-supplied bytes, so each one needs its
                // own bound. A 14-byte file ending on the 0x21 separator used to index past the
                // array and raise IndexOutOfRangeException. This class documents
                // InvalidDataException for malformed input, so a caller guarding on the documented
                // type could not catch that one.
                if (pos >= gifBytes.Length)
                    throw new InvalidDataException("Truncated GIF extension block.");

                var label = gifBytes[pos++];
                if (label == 0xF9) // Graphic Control Extension
                {
                    SkipBlock(gifBytes, ref pos, out var gceData);
                    // gceData[0] = packed, [1]+[2] = delay, [3] = transparent index. A clear
                    // transparency flag resets the index rather than leaving an earlier
                    // extension's value standing, for the same reason the scope-closing rule
                    // below exists: a stale value must not survive past the block it belonged to.
                    if (gceData is not null && gceData.Length >= 4 && (gceData[0] & 0x01) != 0)
                        transparentIndex = gceData[3];
                    else
                        transparentIndex = -1;
                }
                else
                {
                    SkipSubBlocks(gifBytes, ref pos);

                    // Section 12 sorts every labelled block into three ranges: 0x00-0x7F is
                    // Graphic-Rendering (the Trailer, 0x3B, is excluded from that range, but it is
                    // a top-level block type rather than an extension label and never reaches
                    // here), 0x80-0xF9 is Control, and 0xFA-0xFF is Special Purpose. The same
                    // section adds that a decoder "can handle block scope by appropriately
                    // identifying block labels, even when the block itself cannot be processed."
                    // A label in the rendering range therefore closes a pending Graphic Control
                    // Extension's scope whether or not this decoder acts on the block itself.
                    // Section 23 gives that extension's scope as "the first graphic rendering
                    // block to follow," so the Plain Text Extension (0x01), or any unrecognised
                    // label below 0x80, ends it here. Without this, a later and unrelated image
                    // could inherit transparency meant for a block that had already gone by.
                    //
                    // 0xFA-0xFF is Special Purpose: Comment (0xFE) and Application (0xFF). Section
                    // 12 states plainly that these "are transparent to the decoding process" and
                    // do not delimit scope, so this branch leaves transparentIndex untouched for
                    // them, deliberately. The same holds for 0x80-0xF8, Control other than the
                    // Graphic Control Extension itself: only a Graphic-Rendering block closes a
                    // Control block's scope, so an unrecognised Control label is transparent to it
                    // too.
                    if (label <= 0x7F)
                        transparentIndex = -1;
                }
                continue;
            }

            // Unknown block: try to skip sub-blocks.
            SkipSubBlocks(gifBytes, ref pos);
        }

        throw new InvalidDataException("GIF contains no image descriptor.");
    }

    // ── Image Descriptor + LZW decode ────────────────────────────────────────

    private static PdfImageXObject DecodeImage(
        byte[] data, ref int pos,
        byte[]? globalPalette, int transparentIndex)
    {
        if (pos + 9 > data.Length)
            throw new InvalidDataException("Truncated GIF image descriptor.");

        // Image descriptor layout (pos already pointing past the 0x2C separator):
        //   left(2), top(2), width(2), height(2), packed(1)
        var width = data[pos + 4] | (data[pos + 5] << 8);
        var height = data[pos + 6] | (data[pos + 7] << 8);
        var packed = data[pos + 8];
        pos += 9;

        // Reject hostile dimensions before allocating pixel buffers. width*height overflows
        // Int32 for a 65535×65535 descriptor; ValidateDimensions computes it as Int64.
        ImageLimits.ValidateDimensions("GIF", width, height);

        var hasLocalColorTable = (packed & 0x80) != 0;
        var localColorTableSize = 2 << (packed & 0x07);
        // Image Descriptor packed field, bit 6 (GIF89a §20.c.vii): set when the rows are stored
        // in the four-pass order of Appendix E rather than top to bottom.
        var interlaced = (packed & 0x40) != 0;

        byte[] palette;
        if (hasLocalColorTable)
        {
            var localTableBytes = localColorTableSize * 3;
            if (pos + localTableBytes > data.Length)
                throw new InvalidDataException("GIF local colour table extends beyond end of file.");
            palette = data[pos..(pos + localTableBytes)];
            pos += localTableBytes;
        }
        else
        {
            palette = globalPalette ?? throw new InvalidDataException("GIF has no colour table.");
        }

        if (pos >= data.Length)
            throw new InvalidDataException("GIF image data ends before the LZW minimum code size.");

        var lzwMinCodeSize = data[pos++];
        if (lzwMinCodeSize < 2 || lzwMinCodeSize > 8)
            throw new InvalidDataException($"Invalid LZW minimum code size: {lzwMinCodeSize}.");

        // Gather sub-blocks into a single byte stream
        var lzwStream = GatherSubBlocks(data, ref pos);

        // LZW decode
        var indices = LzwDecode(lzwStream, lzwMinCodeSize, width * height);

        // The LZW stream carries rows in storage order. For an interlaced image that is not
        // display order, so the rows are put back before anything reads a pixel. The colour
        // expansion and the transparency mask below both index this array positionally.
        if (interlaced)
            indices = Deinterlace(indices, width, height);

        // Expand indices to RGB
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            var idx = indices[i] * 3;
            if (idx + 2 >= palette.Length) continue; // out-of-range index, leave black
            rgb[i * 3] = palette[idx];
            rgb[i * 3 + 1] = palette[idx + 1];
            rgb[i * 3 + 2] = palette[idx + 2];
        }

        PdfStream? sMask = null;
        if (transparentIndex >= 0)
        {
            var alpha = new byte[width * height];
            for (var i = 0; i < width * height; i++)
                alpha[i] = indices[i] == transparentIndex ? (byte)0 : (byte)255;
            sMask = new PdfStream(alpha);
        }

        return new PdfImageXObject(width, height, rgb, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8, sMask);
    }

    // ── GIF LZW decoder ──────────────────────────────────────────────────────
    // GIF uses a variant of LZW where:
    //   • Codes are packed LSB-first into bytes.
    //   • The code table starts at 2^minCodeSize entries (colour palette).
    //   • Two special codes: Clear (2^minCodeSize) and EOI (2^minCodeSize + 1).
    //   • Code width starts at minCodeSize+1 and grows as the table fills.
    //   • Maximum table size is 4096 entries (12-bit codes).

    private static byte[] LzwDecode(byte[] stream, int minCodeSize, int pixelCount)
    {
        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;

        // Code table: each entry is a sequence of palette indices.
        // We store entries as linked list references for memory efficiency:
        //   tablePrefix[i] = parent code (-1 for root entries)
        //   tableSuffix[i] = last byte of this entry
        var maxTableSize = 4096;
        var tablePrefix = new int[maxTableSize];
        var tableSuffix = new byte[maxTableSize];

        // Output buffer
        var output = new byte[pixelCount];
        var outIdx = 0;

        // Bit-reading state
        int bitBuf = 0;
        int bitsLeft = 0;
        int streamPos = 0;

        // Decode state
        int codeSize = minCodeSize + 1;
        int nextCode = eoiCode + 1;
        int codeMask = (1 << codeSize) - 1;
        int prevCode = -1;
        var sawEndOfInformation = false;

        // Initialise root entries (palette indices 0..clearCode-1)
        for (var i = 0; i < clearCode; i++)
        {
            tablePrefix[i] = -1;
            tableSuffix[i] = (byte)i;
        }
        // Clear and EOI codes themselves aren't used as table entries
        tablePrefix[clearCode] = -1;
        tablePrefix[eoiCode] = -1;

        // Scratch stack for reversing a table-chain into output order.
        // Size is maxTableSize+1: the KwKwK case pushes one extra byte beyond the chain length.
        var stack = new byte[maxTableSize + 1];
        int stackTop;

        while (outIdx < pixelCount)
        {
            // Refill bit buffer from stream
            while (bitsLeft < codeSize && streamPos < stream.Length)
            {
                bitBuf |= stream[streamPos++] << bitsLeft;
                bitsLeft += 8;
            }
            if (bitsLeft < codeSize) break; // truncated stream

            var code = bitBuf & codeMask;
            bitBuf >>= codeSize;
            bitsLeft -= codeSize;

            if (code == eoiCode) { sawEndOfInformation = true; break; }

            if (code == clearCode)
            {
                // Reset table and code size
                codeSize = minCodeSize + 1;
                nextCode = eoiCode + 1;
                codeMask = (1 << codeSize) - 1;
                prevCode = -1;
                continue;
            }

            // Resolve the code into a sequence of palette indices.
            // Handle the special case where code == nextCode (not yet in table).
            int resolvedCode;
            if (code < nextCode)
            {
                resolvedCode = code;
            }
            else if (code == nextCode && prevCode >= 0)
            {
                // The new code will be prevCode's sequence + its own first byte.
                // We emit the previous entry and append its first byte at the end.
                resolvedCode = prevCode;
            }
            else
            {
                throw new InvalidDataException("Invalid GIF LZW code.");
            }

            // Walk the chain and push to stack (entries are stored tail-first).
            // Cap the walk to maxTableSize iterations to prevent OOB on a malformed/cyclic chain.
            stackTop = 0;
            var cur = resolvedCode;
            while (cur >= 0)
            {
                if (stackTop >= maxTableSize)
                    throw new InvalidDataException("GIF LZW chain exceeds maximum table size; data may be corrupt.");
                stack[stackTop++] = tableSuffix[cur];
                cur = tablePrefix[cur];
            }

            // The code that is not yet in the table, conventionally written KwKwK. Its string is
            // the previous string followed by that string's own first byte, so the extra byte
            // belongs at the END of the emitted run.
            //
            // The chain walk above pushes tail-first, and the pop loop below reads from the top
            // down, so the first byte of the string sits at stack[stackTop - 1] and the last byte
            // at stack[0]. Appending at the top therefore emitted the extra byte FIRST rather than
            // last, turning "Kw" + "K" into "K" + "Kw". It goes to the bottom instead.
            //
            // The visible cost was a run of wrong pixels as long as the string wherever this case arose: on a 48x48 image
            // of three-pixel vertical bars, 120 of 2304 pixels, each one sitting exactly on a bar
            // boundary where the pattern repeats.
            if (code == nextCode)
            {
                var firstByte = stack[stackTop - 1];
                Array.Copy(stack, 0, stack, 1, stackTop);
                stack[0] = firstByte;
                stackTop++;
            }

            // Pop stack into output
            for (var i = stackTop - 1; i >= 0 && outIdx < pixelCount; i--)
                output[outIdx++] = stack[i];

            // Add new table entry: prevCode's sequence + first byte of current code.
            // The chain walk pushes suffix bytes root-to-leaf reversed, so stack[stackTop-1]
            // is the first byte of the decoded string (emitted first in the pop loop).
            if (prevCode >= 0 && nextCode < maxTableSize)
            {
                // stack[stackTop-1] is the first byte of the resolved code's string.
                var firstByte = stack[stackTop - 1];
                tablePrefix[nextCode] = prevCode;
                tableSuffix[nextCode] = firstByte;
                nextCode++;

                // GIF89a Appendix F, under COMPRESSION, item 4: "Whenever the LZW code value
                // would exceed the current code length, the code length is increased by one."
                // Appendix F carries two numbered lists, each of four: the steps in its preamble,
                // under no subheading, and the items under COMPRESSION. So a bare "clause 4" is
                // ambiguous between two different rules, and the subheading has to be named.
                //
                // A code length of n expresses values 0..2^n-1, so the value 2^n is the first
                // that exceeds it, and the width has to grow when the next code to be assigned
                // reaches 2^n, which is codeMask + 1. This read `nextCode > codeMask + 1`, growing
                // one code later, so the decoder went on reading 9-bit codes where the encoder had
                // already moved to 10. Every file whose dictionary passed 2^n then desynchronised
                // and was refused as corrupt. The sibling TIFF decoder's own header states the GIF
                // rule correctly, describing TIFF's own early change as "one entry earlier than
                // GIF".
                if (nextCode >= codeMask + 1 && codeSize < 12)
                {
                    codeSize++;
                    codeMask = (1 << codeSize) - 1;
                }
            }

            prevCode = code;
        }

        // What the image descriptor promises and what the data delivers are two different numbers,
        // and nothing compared them. The buffer is allocated at the promised size and left at
        // palette entry 0 wherever the stream stopped early. Measured on the 20x20 fixture in
        // GifSpecificationTests: 36 bytes of header, screen descriptor, colour table, image
        // descriptor and minimum code size, then 38 bytes of code data. Cutting the file to those
        // 36 bytes discards every code byte, and the decode still reported success with a full
        // 400-pixel raster, every pixel invented.
        //
        // The loop above reaches here short in two ways, and they want telling apart. The bit
        // buffer running dry means the data simply stopped. An End of Information code arriving
        // early means the encoder said it was finished while the descriptor asked for more, which
        // is also the shape a code-width desynchronisation takes, the defect fixed above, so a
        // single message would let a decoder bug read as a bad file.
        //
        // The sibling TIFF decoder makes this check and names it "output length mismatch". It also
        // refuses an overrun, which this does not: a stream carrying more pixels than the
        // descriptor asks for is still truncated to the descriptor silently, because the
        // independent decoders tried accept it and refusing it would reject files that render.
        if (outIdx != pixelCount)
            throw new InvalidDataException(
                sawEndOfInformation
                    ? $"GIF image data ended after {outIdx} of {pixelCount} pixels: the stream's "
                      + "End of Information code arrived before the last pixel."
                    : $"GIF image data ended after {outIdx} of {pixelCount} pixels.");

        return output;
    }

    /// <summary>
    /// Puts the rows of an interlaced image back into display order, per GIF89a Appendix E:
    /// "Group 1 : Every 8th. row, starting with row 0", then every 8th from row 4, then every
    /// 4th from row 2, then every 2nd from row 1.
    ///
    /// Nothing here consulted the interlace flag before, so an interlaced image decoded with its
    /// rows in storage order: scrambled, silently, with no error raised. A flat colour survives
    /// that unchanged, which is why the defect could sit behind tests that only asked whether a
    /// file loaded.
    /// </summary>
    private static byte[] Deinterlace(byte[] storageOrder, int width, int height)
    {
        var display = new byte[storageOrder.Length];
        ReadOnlySpan<int> starts = [0, 4, 2, 1];
        ReadOnlySpan<int> steps = [8, 8, 4, 2];

        var src = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            for (var row = starts[pass]; row < height; row += steps[pass])
            {
                storageOrder.AsSpan(src * width, width).CopyTo(display.AsSpan(row * width, width));
                src++;
            }
        }

        return display;
    }

    // ── Sub-block helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Reads GIF sub-blocks (length-prefixed byte sequences terminated by a zero-length block)
    /// and returns their concatenated payload.
    /// </summary>
    private static byte[] GatherSubBlocks(byte[] data, ref int pos)
    {
        using var ms = new MemoryStream();
        while (pos < data.Length)
        {
            var blockLen = data[pos++];
            if (blockLen == 0) break;
            if (pos + blockLen > data.Length)
                throw new InvalidDataException("GIF sub-block extends beyond end of file.");
            ms.Write(data, pos, blockLen);
            pos += blockLen;
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Skips sub-blocks, optionally capturing the first block's payload for GCE parsing.
    /// </summary>
    private static void SkipBlock(byte[] data, ref int pos, out byte[]? firstBlockData)
    {
        firstBlockData = null;
        while (pos < data.Length)
        {
            var blockLen = data[pos++];
            if (blockLen == 0) break;
            // GatherSubBlocks makes this same check; this one was missing, so a graphic control
            // extension whose declared length ran past the end raised ArgumentOutOfRangeException
            // out of the range operator rather than the documented InvalidDataException.
            if (pos + blockLen > data.Length)
                throw new InvalidDataException("GIF sub-block extends beyond end of file.");
            if (firstBlockData is null)
                firstBlockData = data[pos..(pos + blockLen)];
            pos += blockLen;
        }
    }

    private static void SkipSubBlocks(byte[] data, ref int pos)
    {
        while (pos < data.Length)
        {
            var blockLen = data[pos++];
            if (blockLen == 0) break;
            // Unlike SkipBlock above, nothing here indexed out of range: this loop only advances
            // pos, so an overlong block walked past the end and the outer loop then stopped,
            // reporting "GIF contains no image descriptor" for a file whose real fault was a
            // malformed extension. The guard is for the message, not for an exception type.
            if (pos + blockLen > data.Length)
                throw new InvalidDataException("GIF sub-block extends beyond end of file.");
            pos += blockLen;
        }
    }
}
