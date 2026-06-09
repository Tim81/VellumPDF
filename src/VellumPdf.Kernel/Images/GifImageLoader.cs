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
                var label = gifBytes[pos++];
                if (label == 0xF9) // Graphic Control Extension
                {
                    SkipBlock(gifBytes, ref pos, out var gceData);
                    // gceData[0] = packed, [1]+[2] = delay, [3] = transparent index
                    if (gceData is not null && gceData.Length >= 4 && (gceData[0] & 0x01) != 0)
                        transparentIndex = gceData[3];
                }
                else
                {
                    SkipSubBlocks(gifBytes, ref pos);
                }
                continue;
            }

            // Unknown block — try to skip sub-blocks
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

        var lzwMinCodeSize = data[pos++];
        if (lzwMinCodeSize < 2 || lzwMinCodeSize > 8)
            throw new InvalidDataException($"Invalid LZW minimum code size: {lzwMinCodeSize}.");

        // Gather sub-blocks into a single byte stream
        var lzwStream = GatherSubBlocks(data, ref pos);

        // LZW decode
        var indices = LzwDecode(lzwStream, lzwMinCodeSize, width * height);

        // Expand indices to RGB
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            var idx = indices[i] * 3;
            if (idx + 2 >= palette.Length) continue; // out-of-range index — leave black
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

        // Initialise root entries (palette indices 0..clearCode-1)
        for (var i = 0; i < clearCode; i++)
        {
            tablePrefix[i] = -1;
            tableSuffix[i] = (byte)i;
        }
        // Clear and EOI codes themselves aren't used as table entries
        tablePrefix[clearCode] = -1;
        tablePrefix[eoiCode] = -1;

        // Scratch stack for reversing a table-chain into output order
        var stack = new byte[maxTableSize];
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

            if (code == eoiCode) break;

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

            // If code == nextCode (KwKwK case), the new entry's first byte equals
            // the first byte of prevCode's sequence, which is already at the bottom of the stack.
            if (code == nextCode)
            {
                var firstByte = stack[stackTop - 1];
                stack[stackTop++] = firstByte;
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

                // Grow code size when table fills the current range
                if (nextCode > codeMask + 1 && codeSize < 12)
                {
                    codeSize++;
                    codeMask = (1 << codeSize) - 1;
                }
            }

            prevCode = code;
        }

        return output;
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
            pos += blockLen;
        }
    }
}
