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
///   • Interlaced images, deinterlaced to display order (Appendix E).
///   • Graphic Control Extension: transparent index → 8-bit /SMask.
///   • Logical screen, opt-in through <see cref="GifLoadOptions.PlaceOnLogicalScreen"/>: the frame
///     is placed on the screen at its Image Left/Top Position and the pixels it does not cover are
///     transparent (8-bit /SMask). Without the option the frame comes back at its own size.
///   • Animated GIFs: only the first image descriptor is decoded; subsequent frames are ignored.
///
/// Rejected: a file with no image descriptor, an image descriptor truncated before its own fields
/// end, a malformed or truncated LZW stream, a minimum code size outside 2 to 8, an image with no
/// colour table to read from, a colour table or sub-block chain that runs past the end of the
/// file, and dimensions that are invalid or exceed the decoder's safety limit. Every one of these
/// throws <see cref="InvalidDataException"/>, never the exception type the failing read would
/// otherwise raise.
/// </summary>
public static class GifImageLoader
{
    /// <summary>
    /// Decodes the first frame of a GIF into a FlateDecode Image XObject, at the frame's own size.
    /// </summary>
    /// <remarks>
    /// Malformed input is refused. This method throws <see cref="InvalidDataException"/> naming
    /// the fault for a file with no image descriptor, an image descriptor truncated before its own
    /// fields end, an LZW stream that is corrupt or ends before every pixel the image descriptor
    /// promises is produced, a minimum code size outside 2 to 8, an image with no colour table to
    /// read from, a colour table or sub-block chain that runs past the end of the file, a truncated
    /// extension, or dimensions <see cref="ImageLimits.ValidateDimensions"/> refuses. A caller
    /// can guard on the one exception type for all of them rather than several.
    /// <para><b>The Logical Screen Descriptor and the frame's position are not read.</b> The result
    /// is the frame at its own size, with an /SMask only where a Graphic Control Extension names a
    /// transparent index. A file whose frame is smaller than its screen, or sits away from the
    /// screen's origin, therefore decodes to a different size and aspect ratio from the one a
    /// viewer shows. <see cref="Load(byte[], GifLoadOptions)"/> with
    /// <see cref="GifLoadOptions.PlaceOnLogicalScreen"/> set places the frame on the screen. <b>Do
    /// not rely on the default ignoring the screen:</b> a later major may place the frame by
    /// default.</para>
    /// <para>A stream carrying more pixels than the image descriptor promises is <b>not</b>
    /// refused: decoding stops once the promised count is produced, so trailing codes are never
    /// read. A pixel whose colour index is past the active colour table is left black, not
    /// refused. Do not rely on either being accepted: <see cref="Load(byte[], GifLoadOptions)"/>
    /// with <see cref="GifLoadOptions.PlaceOnLogicalScreen"/> set refuses both, and a later major
    /// may refuse them by default.</para>
    /// </remarks>
    /// <param name="gifBytes">The GIF file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="gifBytes"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">
    /// <paramref name="gifBytes"/> is too small to be a GIF, does not carry the GIF87a/GIF89a
    /// signature, has no image descriptor, the image descriptor itself is truncated, a colour
    /// table or sub-block chain runs past the end of the array, the active image has no colour
    /// table to read from, an extension is truncated, the LZW minimum code size is outside 2 to 8,
    /// the LZW stream is corrupt or ends before every promised pixel is produced, or the image
    /// dimensions are invalid or exceed the safety limit named in the message.
    /// </exception>
    public static PdfImageXObject Load(byte[] gifBytes) => Load(gifBytes, GifLoadOptions.Default);

    /// <summary>
    /// Decodes the first frame of a GIF into a FlateDecode Image XObject, placing it on the logical
    /// screen when <paramref name="options"/> asks for that.
    /// </summary>
    /// <remarks>
    /// With <see cref="GifLoadOptions.PlaceOnLogicalScreen"/> unset this is
    /// <see cref="Load(byte[])"/>. The rest of this section describes the option set.
    /// <para><b>The frame is placed on the logical screen.</b> The result is as large as the
    /// Logical Screen Descriptor says (GIF89a section 18), with the frame at its Image Left and Top
    /// Position (section 20.c.ii and 20.c.iii). The pixels the frame does not cover are
    /// transparent: the result carries an /SMask whose alpha is 0 there, and the colour stored
    /// under them is black. The Background Color Index (section 18.c.vii) is not read and plays no
    /// part, because it names a colour for those pixels and the transparent answer invents none.
    /// The frame is placed when its left or top is not 0 or when the screen is wider or taller
    /// than the frame. A frame that fills the screen from the origin gets no /SMask it did not
    /// already have from a Graphic Control Extension.</para>
    /// <para><b>A screen the frame does not fit is refused.</b> Section 20.a says each image must
    /// fit within the boundaries of the Logical Screen, so under this option a screen with a
    /// width or height of 0, a frame whose far edge lies beyond the screen, and a screen of more
    /// than 100 million pixels (the limit <see cref="ImageLimits.ValidateDimensions"/> applies to a
    /// frame) all throw <see cref="InvalidDataException"/>. None falls back to the frame's own
    /// size, because the option was asked for and an answer that silently ignored it would be
    /// the defect it exists to fix.</para>
    /// <para><b>Data that breaks a GIF89a rule is refused.</b> Under the option the loader throws
    /// <see cref="InvalidDataException"/> naming the rule and its section for a pixel whose colour
    /// index is past the active colour table (section 22.a), image data that decodes more pixels
    /// than the descriptor declares or ends without the End of Information code that Appendix F
    /// says must be the last code, image data not ended by a Block Terminator (section 22.c), a
    /// Graphic Control Extension that is not one sub-block with a Block Size of 4 or a second one
    /// before the same graphic-rendering block (sections 23.a and 23.c.iii), and a top-level block
    /// that is not an extension, an image descriptor or the trailer (Appendix B). The default path
    /// accepts each of these.</para>
    /// <para><b>Cost.</b> The canvas is allocated at the screen's size, 4 bytes a pixel (RGB plus
    /// the alpha of the /SMask), however small the frame is. A 42-byte file can therefore claim a
    /// screen that allocates about 400 MB, the largest the limit allows. Callers loading
    /// untrusted files should check the screen size in bytes 6 to 9 before enabling the option.
    /// With the option unset no allocation depends on the screen.</para>
    /// </remarks>
    /// <param name="gifBytes">The GIF file.</param>
    /// <param name="options">What to do with the logical screen.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="gifBytes"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Every case <see cref="Load(byte[])"/> documents, and, with
    /// <see cref="GifLoadOptions.PlaceOnLogicalScreen"/> set, a screen with a zero dimension, a
    /// frame that extends past the screen, a screen over 100 million pixels, or any of the
    /// GIF89a rule breaks listed above.
    /// </exception>
    public static PdfImageXObject Load(byte[] gifBytes, GifLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(gifBytes);
        ArgumentNullException.ThrowIfNull(options);
        var place = options.PlaceOnLogicalScreen;
        if (gifBytes.Length < 13)
            throw new InvalidDataException("GIF data too small.");

        // Validate signature: "GIF87a" or "GIF89a"
        if (gifBytes[0] != 'G' || gifBytes[1] != 'I' || gifBytes[2] != 'F' ||
            gifBytes[3] != '8' || (gifBytes[4] != '7' && gifBytes[4] != '9') ||
            gifBytes[5] != 'a')
            throw new InvalidDataException("Not a GIF file.");

        // Logical screen descriptor, section 18: width(2), height(2), packed(1), background(1),
        // aspect(1). The background index (byte 11) is deliberately not read; see the remarks on
        // Load for why the uncovered area is transparent rather than that colour. The size is
        // used only when placement was asked for.
        var screenWidth = gifBytes[6] | (gifBytes[7] << 8);
        var screenHeight = gifBytes[8] | (gifBytes[9] << 8);
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
        // True from a Graphic Control Extension until the graphic-rendering block that closes its
        // scope. Read only under the option, to refuse a second one (section 23.a).
        var graphicControlPending = false;

        while (pos < gifBytes.Length)
        {
            var blockType = gifBytes[pos++];

            if (blockType == 0x3B) // Trailer
                break;

            if (blockType == 0x2C) // Image Descriptor
            {
                return DecodeImage(gifBytes, ref pos, globalPalette, transparentIndex, screenWidth, screenHeight, place);
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
                    SkipBlock(gifBytes, ref pos, out var gceData, out var gceSubBlocks);
                    if (place)
                    {
                        // Section 23.a: "at most one Graphic Control Extension may precede a graphic
                        // rendering block", and it "contains only one data sub-block"; section
                        // 23.c.iii: the Block Size "contains the fixed value 4". Without the option
                        // a block that breaks these is read as far as it goes, as on 2.3.3.
                        if (graphicControlPending)
                            throw new InvalidDataException(
                                "GIF has two Graphic Control Extensions before one graphic rendering block (section 23.a: at most one may precede it).");
                        if (gceSubBlocks != 1 || gceData is null || gceData.Length != 4)
                            throw new InvalidDataException(
                                "GIF Graphic Control Extension is not one sub-block with a Block Size of 4 (section 23.a and 23.c.iii).");
                    }
                    graphicControlPending = true;
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
                    // Graphic-Rendering (the Trailer, 0x3B, is excluded from that range as a
                    // top-level block type; the top-level loop above matches it before an
                    // extension is ever considered, so a conformant stream never presents 0x3B as
                    // an extension label here, but this branch does not distinguish the two, so a
                    // non-conformant 0x21 0x3B pair would still be read as inside 0x00-0x7F (with
                    // no pixels at risk, since no such stream is conformant), 0x80-0xF9 is Control,
                    // and 0xFA-0xFF is Special Purpose. The same
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
                    // Graphic Control Extension itself, on different grounds: section 23 only ever
                    // gives a Graphic-Rendering block as what closes a pending scope, so an
                    // unrecognised Control label simply is not one of the blocks that can close it.
                    if (label <= 0x7F)
                    {
                        transparentIndex = -1;
                        graphicControlPending = false;
                    }
                }
                continue;
            }

            // Appendix B gives a Data Stream three kinds of top-level block: an extension (0x21),
            // an image descriptor (0x2C) and the trailer (0x3B). Anything else is outside the
            // grammar; without the option it is skipped as a run of sub-blocks, as on 2.3.3.
            if (place)
                throw new InvalidDataException(
                    $"GIF has the block introducer 0x{blockType:X2} at offset {pos - 1}; Appendix B allows only an extension (0x21), an image descriptor (0x2C) or the trailer (0x3B).");

            // Unknown block: try to skip sub-blocks.
            SkipSubBlocks(gifBytes, ref pos);
        }

        throw new InvalidDataException("GIF contains no image descriptor.");
    }

    // ── Image Descriptor + LZW decode ────────────────────────────────────────

    private static PdfImageXObject DecodeImage(
        byte[] data, ref int pos,
        byte[]? globalPalette, int transparentIndex, int screenWidth, int screenHeight, bool place)
    {
        if (pos + 9 > data.Length)
            throw new InvalidDataException("Truncated GIF image descriptor.");

        // Image descriptor layout (pos already pointing past the 0x2C separator):
        //   left(2), top(2), width(2), height(2), packed(1)
        var left = data[pos] | (data[pos + 1] << 8);
        var top = data[pos + 2] | (data[pos + 3] << 8);
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
        var lzwStream = GatherSubBlocks(data, ref pos, out var terminated);

        // LZW decode. The raster buffer is sized from the image descriptor, which is attacker
        // text: a 42-byte file can declare 10,000 x 10,000 and the code stream behind it is
        // refused as short, but only after the buffer exists (about 100 MB on 2.3.3 for that
        // file, #536's class, against about 27 KB now). A first pass runs the same decoder with
        // no buffer and so makes the same refusals, with the same messages, in the same order, and counts
        // the pixels the stream delivers. The buffer is allocated only once that count has
        // matched, so what is allocated is bounded by what the file contained.
        LzwDecode(lzwStream, lzwMinCodeSize, width * height, produce: false, strict: place);
        var indices = LzwDecode(lzwStream, lzwMinCodeSize, width * height, produce: true, strict: false)!;

        // The LZW stream carries rows in storage order. For an interlaced image that is not
        // display order, so the rows are put back before anything reads a pixel. The colour
        // expansion and the transparency mask below both index this array positionally.
        if (interlaced)
            indices = Deinterlace(indices, width, height);

        if (!place)
            return FrameOnly(indices, width, height, palette, transparentIndex);

        // Placement was asked for, so the data must meet the rules the default path lets pass. The
        // checks run after the LZW decode, so a file that is malformed in the ways Load documents
        // is refused with those messages first.
        //
        // Section 16 defines the Block Terminator as the zero-length sub-block that ends a sequence
        // of Data Sub-blocks (section 22.c gives the image data as such a sequence), and the
        // Appendix F raster-data diagram shows it after the code that "must appear before Block
        // Terminator". Running off the end of the file without one is accepted by default.
        if (!terminated)
            throw new InvalidDataException(
                "GIF image data is not ended by a Block Terminator (section 22.c, section 16).");

        // Section 22.a: "Each index must be within the range of the size of the active color
        // table, starting at 0." The default path leaves such a pixel black.
        var entries = palette.Length / 3;
        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= entries)
                throw new InvalidDataException(
                    $"GIF pixel ({i % width},{i / width}) has colour index {indices[i]}, past the {entries}-entry active colour table (section 22.a: \"Each index must be within the range of the size of the active color table\").");
        }

        // A screen the frame cannot be placed on is refused rather than answered with the frame
        // alone. Section 20.a: "Each image must fit within the boundaries of the Logical Screen".
        if (screenWidth == 0 || screenHeight == 0)
            throw new InvalidDataException(
                $"GIF logical screen is {screenWidth}x{screenHeight}; the frame cannot be placed on it.");
        if (left + width > screenWidth || top + height > screenHeight)
            throw new InvalidDataException(
                $"GIF frame {width}x{height} at ({left},{top}) does not fit its {screenWidth}x{screenHeight} logical screen (section 20.a).");
        if ((long)screenWidth * screenHeight > ImageLimits.MaxPixels)
            throw new InvalidDataException(
                $"GIF logical screen {screenWidth}x{screenHeight} exceeds the {ImageLimits.MaxPixels}-pixel limit.");

        // A frame that fills the screen from the origin is the frame as it is.
        if (screenWidth == width && screenHeight == height)
            return FrameOnly(indices, width, height, palette, transparentIndex);

        return FrameOnScreen(
            indices, width, height, left, top, screenWidth, screenHeight, palette, transparentIndex);
    }

    private static PdfImageXObject FrameOnly(
        byte[] indices, int width, int height, byte[] palette, int transparentIndex)
    {
        // Expand indices to RGB. Section 22.a requires every index to be within the active
        // colour table's range, so one that is not makes this a malformed file, and the class
        // contract is InvalidDataException for that. This is a deliberate departure from it: the
        // out-of-range pixel is left black rather than the whole image refused, because a single
        // bad index costs one pixel and a caller who only wants the rest of the frame has no way
        // to ask for that leniency if this throws instead.
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

    /// <summary>
    /// Composes the frame onto a larger canvas. Uncovered pixels keep alpha 0 and black colour
    /// (both arrays start zeroed); covered pixels are opaque unless they carry the transparent
    /// index. The /SMask is always written here, since the uncovered area needs it.
    /// </summary>
    private static PdfImageXObject FrameOnScreen(
        byte[] indices, int width, int height, int left, int top,
        int canvasWidth, int canvasHeight, byte[] palette, int transparentIndex)
    {
        var rgb = new byte[canvasWidth * canvasHeight * 3];
        var alpha = new byte[canvasWidth * canvasHeight];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var src = y * width + x;
                var dst = (top + y) * canvasWidth + left + x;
                alpha[dst] = indices[src] == transparentIndex ? (byte)0 : (byte)255;
                var idx = indices[src] * 3;
                if (idx + 2 >= palette.Length) continue; // out-of-range index, leave black
                rgb[dst * 3] = palette[idx];
                rgb[dst * 3 + 1] = palette[idx + 1];
                rgb[dst * 3 + 2] = palette[idx + 2];
            }
        }

        return new PdfImageXObject(
            canvasWidth, canvasHeight, rgb, PdfName.FlateDecode, ImageColorSpace.DeviceRgb, 8, new PdfStream(alpha));
    }

    // ── GIF LZW decoder ──────────────────────────────────────────────────────
    // GIF uses a variant of LZW where:
    //   • Codes are packed LSB-first into bytes.
    //   • The code table starts at 2^minCodeSize entries (colour palette).
    //   • Two special codes: Clear (2^minCodeSize) and EOI (2^minCodeSize + 1).
    //   • Code width starts at minCodeSize+1 and grows as the table fills.
    //   • Maximum table size is 4096 entries (12-bit codes).

    private static byte[]? LzwDecode(byte[] stream, int minCodeSize, int pixelCount, bool produce, bool strict)
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

        // Output buffer; null on the counting pass (see the caller).
        var output = produce ? new byte[pixelCount] : null;
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
        var lastCodeWidened = false;

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
            lastCodeWidened = false;

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
            // The visible cost was a run of wrong pixels as long as the string wherever this case
            // arose: on a 48x48 image of three-pixel vertical bars, 120 of 2304 pixels, each one
            // sitting exactly on a bar boundary where the pattern repeats.
            if (code == nextCode)
            {
                var firstByte = stack[stackTop - 1];
                Array.Copy(stack, 0, stack, 1, stackTop);
                stack[0] = firstByte;
                stackTop++;
            }

            // Pop stack into output
            // Strict mode keeps counting past the descriptor's pixel count so an overrun shows
            // as a count above it; the default stops there and drops the rest, as on 2.3.3.
            for (var i = stackTop - 1; i >= 0 && (strict || outIdx < pixelCount); i--)
            {
                if (output is not null && outIdx < pixelCount)
                    output[outIdx] = stack[i];
                outIdx++;
            }

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
                // Appendix F carries two numbered lists, each of four items: the steps in its
                // preamble, under no subheading, and the items under COMPRESSION. A bare item
                // number is therefore ambiguous between the two lists, hence "COMPRESSION, item
                // 4" throughout rather than a number alone.
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
                    lastCodeWidened = true;
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
        // descriptor asks for is still truncated to the descriptor silently, because Pillow 12.3.0,
        // the independent decoder this was checked against, accepts such a stream, and refusing it
        // here would reject files that render.
        if (strict && outIdx > pixelCount)
            throw new InvalidDataException(
                $"GIF image data decodes more pixels than the {pixelCount} its descriptor declares: the last code runs past it (section 22.a, Appendix F).");

        if (outIdx != pixelCount)
            throw new InvalidDataException(
                sawEndOfInformation
                    ? $"GIF image data ended after {outIdx} of {pixelCount} pixels: the stream's "
                      + "End of Information code arrived before the last pixel."
                    : $"GIF image data ended after {outIdx} of {pixelCount} pixels.");

        if (strict)
            RequireEndOfInformation(stream, minCodeSize, pixelCount, bitBuf, bitsLeft, streamPos, codeSize, lastCodeWidened);

        return output;
    }

    /// <summary>
    /// Appendix F, COMPRESSION item 2: the End of Information code "must be the last code output by
    /// the encoder for an image". After the last pixel only Clear codes may come before it (a Clear
    /// carries no pixel); a data code is more pixels than declared, and no End of Information at
    /// all is a missing terminating code. Padding bits after it in its last byte are not codes.
    ///
    /// The first code after the last pixel is read at the decoder's current width. When the table
    /// entry added for the last data code is what widened that width, it is also read one bit
    /// narrower: the specification does not say which width the final code takes at a widening,
    /// and <see cref="GifEncoder"/> writes the narrower one, so both are accepted rather than
    /// refusing the library's own output. Anywhere else the width is not in doubt, and a narrower
    /// read would take a data code whose low bits equal the End of Information code for that
    /// code.
    /// </summary>
    private static void RequireEndOfInformation(
        byte[] stream, int minCodeSize, int pixelCount, int bitBuf, int bitsLeft, int streamPos, int codeSize,
        bool lastCodeWidened)
    {
        var clearCode = 1 << minCodeSize;
        var eoiCode = clearCode + 1;
        var first = true;
        while (true)
        {
            while (bitsLeft < codeSize && streamPos < stream.Length)
            {
                bitBuf |= stream[streamPos++] << bitsLeft;
                bitsLeft += 8;
            }

            var narrowest = first && lastCodeWidened ? codeSize - 1 : codeSize;
            var consumed = 0;
            for (var width = codeSize; width >= narrowest && consumed == 0; width--)
            {
                if (bitsLeft < width)
                    continue;
                var code = bitBuf & ((1 << width) - 1);
                if (code == eoiCode)
                    return;
                if (code == clearCode)
                    consumed = width;
            }

            if (consumed == 0)
            {
                throw new InvalidDataException(
                    bitsLeft < narrowest
                        ? "GIF image data ends without an End of Information code (Appendix F, COMPRESSION item 2: it \"must be the last code output by the encoder for an image\")."
                        : $"GIF image data decodes more pixels than the {pixelCount} its descriptor declares: a code follows the last pixel (section 22.a, Appendix F).");
            }

            bitBuf >>= consumed;
            bitsLeft -= consumed;
            codeSize = minCodeSize + 1;
            first = false;
        }
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
    private static byte[] GatherSubBlocks(byte[] data, ref int pos, out bool terminated)
    {
        using var ms = new MemoryStream();
        terminated = false;
        while (pos < data.Length)
        {
            var blockLen = data[pos++];
            if (blockLen == 0)
            {
                terminated = true;
                break;
            }
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
    private static void SkipBlock(byte[] data, ref int pos, out byte[]? firstBlockData, out int subBlocks)
    {
        firstBlockData = null;
        subBlocks = 0;
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
            subBlocks++;
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
