// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Images;

/// <summary>
/// Decodes MMR (Modified Modified READ / ITU-T T.6 CCITT Group 4) compressed bilevel
/// image data into a 1-bpp packed raster.
///
/// <para>T.6 is a 2D encoding that describes each row relative to the preceding reference
/// row using pass, horizontal, and vertical mode codewords. Horizontal runs are encoded
/// with the T.4 (Modified Huffman) one-dimensional run-length tables.</para>
///
/// <para>All reads are bounded against the input span; truncation throws
/// <see cref="InvalidDataException"/>.</para>
/// </summary>
internal static class MmrDecoder
{
    /// <summary>
    /// Decodes an MMR-compressed stream into a 1-bpp raster.
    /// </summary>
    /// <param name="data">The raw MMR-compressed bytes (no EOL markers, no file header).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="rowBytes">Bytes per output row (= (width + 7) / 8).</param>
    /// <returns>A byte array of length rowBytes * height, MSB-first packed pixels.</returns>
    public static byte[] Decode(ReadOnlySpan<byte> data, int width, int height, int rowBytes)
    {
        var output = new byte[rowBytes * height];
        var reader = new BitReader(data);

        // Changing-element arrays. refCE[i] is the x-coordinate of the i-th transition
        // on the reference (previous) row. The virtual row above the image is all-white,
        // so it has one changing element at position `width`.
        var refCE = new int[width + 2];
        var curCE = new int[width + 2];
        refCE[0] = width; // sentinel: one "white→black" boundary at the end
        refCE[1] = width;

        for (var row = 0; row < height; row++)
        {
            var rowOffset = row * rowBytes;
            DecodeRow(ref reader, refCE, curCE, width, output, rowOffset);

            // Swap cur ↔ ref and reset cur for the next row.
            (refCE, curCE) = (curCE, refCE);
            Array.Clear(curCE, 0, curCE.Length);
        }

        return output;
    }

    // ── Row decoder ───────────────────────────────────────────────────────────

    private static void DecodeRow(
        ref BitReader reader,
        int[] refCE, int[] curCE,
        int width, byte[] output, int rowOffset)
    {
        var a0 = 0; // current x position
        var a0Col = 0; // color at a0 (0 = white, 1 = black); coding line starts white
        var ceIdx = 0; // index into curCE

        while (a0 < width)
        {
            var mode = ReadMode(ref reader);
            switch (mode)
            {
                case Mode.Pass:
                    {
                        // b1 = first CE on ref to the right of a0, opposite color to a0Col.
                        // b2 = next CE after b1 on ref.
                        var b1 = FindB1(refCE, a0, a0Col);
                        var b2 = NextCE(refCE, b1);
                        FillRun(output, rowOffset, a0, b2, a0Col);
                        a0 = b2;
                        // a0Col is unchanged in pass mode.
                        break;
                    }

                case Mode.Horizontal:
                    {
                        // Two consecutive run lengths follow, alternating color from a0Col.
                        var run1 = DecodeRun(ref reader, a0Col);
                        var run2 = DecodeRun(ref reader, 1 - a0Col);
                        var a1 = Math.Min(a0 + run1, width);
                        var a2 = Math.Min(a1 + run2, width);
                        FillRun(output, rowOffset, a0, a1, a0Col);
                        curCE[ceIdx++] = a1;
                        FillRun(output, rowOffset, a1, a2, 1 - a0Col);
                        curCE[ceIdx++] = a2;
                        a0 = a2;
                        // a0Col returns to original (two color transitions = net zero).
                        break;
                    }

                default:
                    {
                        // Vertical modes V(0), V(+1..+3), V(-1..-3).
                        var delta = (int)mode; // encoded as the delta value directly
                        var b1 = FindB1(refCE, a0, a0Col);
                        var a1 = Math.Clamp(b1 + delta, a0, width);
                        FillRun(output, rowOffset, a0, a1, a0Col);
                        if (a1 != a0)
                            curCE[ceIdx++] = a1;
                        a0 = a1;
                        a0Col ^= 1; // color flips at a1
                        break;
                    }
            }
        }

        // Terminate the changing-element list with the width sentinel.
        if (ceIdx < curCE.Length)
            curCE[ceIdx] = width;
        if (ceIdx + 1 < curCE.Length)
            curCE[ceIdx + 1] = width;
    }

    // ── Mode codes (T.6 Table 2) ──────────────────────────────────────────────

    // We encode vertical modes as their delta value (-3 .. +3) and use the
    // special constants below for Pass and Horizontal.
    private const int Mode_Pass = int.MinValue;
    private const int Mode_Horizontal = int.MinValue + 1;
    private const int Mode_Eofb = int.MinValue + 2;

    // Thin struct-like alias for readability in the switch.
    private static class Mode
    {
        public const int Pass = Mode_Pass;
        public const int Horizontal = Mode_Horizontal;
        public const int Eofb = Mode_Eofb;
    }

    /// <summary>Reads the next T.6 2D mode codeword (MSB-first).</summary>
    private static int ReadMode(ref BitReader r)
    {
        // T.6 mode table (ISO/IEC 11544 Table 2 / ITU-T T.6 §4):
        //  1             -> V(0)      delta = 0
        //  011           -> H
        //  010           -> V(-1)     delta = -1
        //  0011          -> V(+1)     delta = +1
        //  0010          -> V(-2)     delta = -2
        //  000011        -> V(+2)     delta = +2
        //  000010        -> V(-3)     delta = -3
        //  0000011       -> V(+3)     delta = +3
        //  0000001       -> Pass
        //  000000000001  -> EOFB

        if (r.ReadBit() == 1) return 0; // V(0)

        if (r.ReadBit() == 1)
        {
            // 01x
            return r.ReadBit() == 1 ? Mode.Horizontal : -1; // 011=H, 010=V(-1)
        }

        if (r.ReadBit() == 1)
        {
            // 001x
            return r.ReadBit() == 1 ? 1 : -2; // 0011=V(+1), 0010=V(-2)
        }

        if (r.ReadBit() == 1)
        {
            // 0001x — unexpected in T.6 table; treat as V(0) to avoid hang.
            _ = r.ReadBit();
            return 0;
        }

        if (r.ReadBit() == 1)
        {
            // 00001x
            return r.ReadBit() == 1 ? 2 : -3; // 000011=V(+2), 000010=V(-3)
        }

        if (r.ReadBit() == 1)
        {
            // 000001x
            return r.ReadBit() == 1 ? 3 : Mode.Pass; // 0000011=V(+3), 0000001=Pass
        }

        // 0000000... — could be EOFB (000000000001) or padding.
        // We've read 7 bits of 0s so far; EOFB is 12 bits = 00000000 00 01.
        // Read 5 more bits (total 12) checking for EOFB.
        for (var i = 0; i < 5; i++)
        {
            if (r.TryReadBit(out var b) && b == 1)
            {
                // This is the EOFB-terminating 1 (or any stray 1 after 0s).
                return Mode.Eofb;
            }
        }
        return Mode.Eofb;
    }

    // ── Run-length decoder (T.4 / Modified Huffman) ───────────────────────────

    /// <summary>
    /// Decodes one T.4 run length for the given <paramref name="color"/> (0=white, 1=black).
    /// A run may be made up of one or more makeup codes followed by a terminating code.
    /// </summary>
    private static int DecodeRun(ref BitReader r, int color)
    {
        var total = 0;
        while (true)
        {
            var (value, isMakeup) = color == 0 ? ReadWhite(ref r) : ReadBlack(ref r);
            total += value;
            if (!isMakeup) return total;
        }
    }

    // White run-length codes (ITU-T T.4 Table 2).
    // Returns (runLength, isMakeup).
    private static (int run, bool makeup) ReadWhite(ref BitReader r)
    {
        // Uses a lookup table indexed by up to 12 bits of lookahead.
        // We read one bit at a time, matching the prefix table.
        Span<int> b = stackalloc int[12];
        int n = 0;

        for (; n < 12; n++)
        {
            b[n] = r.ReadBit();

            if (n == 3) // 4 bits
            {
                int v = B(b, 4);
                switch (v)
                {
                    case 0b0111: return (2, false);
                    case 0b1000: return (3, false);
                    case 0b1011: return (4, false);
                    case 0b1100: return (5, false);
                    case 0b1110: return (6, false);
                    case 0b1111: return (7, false);
                }
            }

            if (n == 4) // 5 bits
            {
                int v = B(b, 5);
                switch (v)
                {
                    case 0b10011: return (8, false);
                    case 0b10100: return (9, false);
                    case 0b00111: return (10, false);
                    case 0b01000: return (11, false);
                    case 0b11011: return (64, true);
                    case 0b10010: return (128, true);
                }
            }

            if (n == 5) // 6 bits
            {
                int v = B(b, 6);
                switch (v)
                {
                    case 0b001000: return (12, false);
                    case 0b000011: return (13, false);
                    case 0b110100: return (14, false);
                    case 0b110101: return (15, false);
                    case 0b101010: return (16, false);
                    case 0b101011: return (17, false);
                    case 0b010111: return (192, true);
                    case 0b011000: return (1664, true);
                }
            }

            if (n == 6) // 7 bits
            {
                int v = B(b, 7);
                switch (v)
                {
                    case 0b0100111: return (18, false);
                    case 0b0001100: return (19, false);
                    case 0b0001000: return (20, false);
                    case 0b0010111: return (21, false);
                    case 0b0000011: return (22, false);
                    case 0b0000100: return (23, false);
                    case 0b0101000: return (24, false);
                    case 0b0101011: return (25, false);
                    case 0b0010011: return (26, false);
                    case 0b0100100: return (27, false);
                    case 0b0011000: return (28, false);
                    case 0b0110111: return (256, true);
                }
            }

            if (n == 7) // 8 bits
            {
                int v = B(b, 8);
                switch (v)
                {
                    case 0b00110101: return (0, false);
                    case 0b00000010: return (29, false);
                    case 0b00000011: return (30, false);
                    case 0b00011010: return (31, false);
                    case 0b00011011: return (32, false);
                    case 0b00010010: return (33, false);
                    case 0b00010011: return (34, false);
                    case 0b00010100: return (35, false);
                    case 0b00010101: return (36, false);
                    case 0b00010110: return (37, false);
                    case 0b00010111: return (38, false);
                    case 0b00101000: return (39, false);
                    case 0b00101001: return (40, false);
                    case 0b00101010: return (41, false);
                    case 0b00101011: return (42, false);
                    case 0b00101100: return (43, false);
                    case 0b00101101: return (44, false);
                    case 0b00000100: return (45, false);
                    case 0b00000101: return (46, false);
                    case 0b00001010: return (47, false);
                    case 0b00001011: return (48, false);
                    case 0b01010010: return (49, false);
                    case 0b01010011: return (50, false);
                    case 0b01010100: return (51, false);
                    case 0b01010101: return (52, false);
                    case 0b00100100: return (53, false);
                    case 0b00100101: return (54, false);
                    case 0b01011000: return (55, false);
                    case 0b01011001: return (56, false);
                    case 0b01011010: return (57, false);
                    case 0b01011011: return (58, false);
                    case 0b01001010: return (59, false);
                    case 0b01001011: return (60, false);
                    case 0b00110010: return (61, false);
                    case 0b00110011: return (62, false);
                    case 0b00110100: return (63, false);
                    case 0b00110110: return (320, true);
                    case 0b00110111: return (384, true);
                    case 0b01100100: return (448, true);
                    case 0b01100101: return (512, true);
                    case 0b01101000: return (576, true);
                    case 0b01100111: return (640, true);
                }
            }

            if (n == 8) // 9 bits
            {
                int v = B(b, 9);
                switch (v)
                {
                    case 0b011001100: return (704, true);
                    case 0b011001101: return (768, true);
                    case 0b011010010: return (832, true);
                    case 0b011010011: return (896, true);
                    case 0b011010100: return (960, true);
                    case 0b011010101: return (1024, true);
                    case 0b011010110: return (1088, true);
                    case 0b011010111: return (1152, true);
                    case 0b011011000: return (1216, true);
                    case 0b011011001: return (1280, true);
                    case 0b011011010: return (1344, true);
                    case 0b011011011: return (1408, true);
                    case 0b010011000: return (1472, true);
                    case 0b010011001: return (1536, true);
                    case 0b010011010: return (1600, true);
                    case 0b010011011: return (1728, true);
                }
            }
        }

        throw new InvalidDataException("JBIG2 MMR: unrecognised white run-length code.");
    }

    // Black run-length codes (ITU-T T.4 Table 3).
    private static (int run, bool makeup) ReadBlack(ref BitReader r)
    {
        Span<int> b = stackalloc int[13];
        int n = 0;

        for (; n < 13; n++)
        {
            b[n] = r.ReadBit();

            if (n == 1) // 2 bits
            {
                int v = B(b, 2);
                switch (v)
                {
                    case 0b10: return (3, false);
                    case 0b11: return (2, false);
                }
            }

            if (n == 2) // 3 bits
            {
                int v = B(b, 3);
                switch (v)
                {
                    case 0b010: return (1, false);
                    case 0b011: return (4, false);
                }
            }

            if (n == 3) // 4 bits
            {
                int v = B(b, 4);
                switch (v)
                {
                    case 0b0100: return (6, false);
                    case 0b0101: return (5, false);
                    case 0b0111: return (7, false);
                }
            }

            if (n == 4) // 5 bits
            {
                int v = B(b, 5);
                switch (v)
                {
                    case 0b00100: return (9, false);
                    case 0b00011: return (10, false);
                    case 0b01000: return (8, false);
                }
            }

            if (n == 5) // 6 bits
            {
                int v = B(b, 6);
                switch (v)
                {
                    case 0b000101: return (11, false);
                    case 0b000100: return (12, false);
                    case 0b001101: return (13, false);
                }
            }

            if (n == 6) // 7 bits
            {
                int v = B(b, 7);
                switch (v)
                {
                    case 0b0001101: return (14, false);
                    case 0b0001100: return (15, false);
                    case 0b0001000: return (16, false);
                    case 0b0000111: return (17, false);
                    case 0b0001111: return (0, false);
                }
            }

            if (n == 7) // 8 bits
            {
                int v = B(b, 8);
                switch (v)
                {
                    case 0b00001000: return (18, false);
                    case 0b00101000: return (19, false);
                    case 0b00010111: return (20, false);
                    case 0b00011000: return (21, false);
                    case 0b00100111: return (22, false);
                    case 0b00100000: return (23, false);
                    case 0b00010100: return (24, false);
                    case 0b00001111: return (64, true);
                    case 0b00001100: return (128, true);
                }
            }

            if (n == 8) // 9 bits
            {
                int v = B(b, 9);
                switch (v)
                {
                    case 0b000011011: return (27, false);
                    case 0b000011010: return (28, false);
                    case 0b000110111: return (29, false);
                    case 0b000110110: return (30, false);
                    case 0b001100100: return (31, false);
                    case 0b001100101: return (32, false);
                    case 0b001101000: return (33, false);
                    case 0b001101001: return (34, false);
                    case 0b001101010: return (35, false);
                    case 0b001101011: return (36, false);
                    case 0b001101100: return (37, false);
                    case 0b001101101: return (38, false);
                    case 0b000100000: return (39, false);
                    case 0b000100001: return (40, false);
                    case 0b000100010: return (41, false);
                    case 0b000100011: return (42, false);
                    case 0b000100100: return (43, false);
                    case 0b000100101: return (44, false);
                    case 0b000011000: return (45, false);
                    case 0b000010111: return (46, false);
                    case 0b000011100: return (47, false);
                    case 0b000011101: return (48, false);
                    case 0b000011110: return (49, false);
                    case 0b000011111: return (50, false);
                    case 0b000010000: return (51, false);
                    case 0b000010001: return (52, false);
                    case 0b000010010: return (53, false);
                    case 0b000010011: return (54, false);
                    case 0b000010100: return (55, false);
                    case 0b000010101: return (56, false);
                    case 0b000010110: return (57, false);
                    case 0b000001101: return (192, true);
                    case 0b000001100: return (256, true);
                }
            }

            if (n == 9) // 10 bits
            {
                int v = B(b, 10);
                switch (v)
                {
                    case 0b0001011011: return (25, false);
                    case 0b0001011010: return (26, false);
                    case 0b0000100110: return (63, false);
                    case 0b0000100111: return (62, false);
                    case 0b0000110011: return (320, true);
                }
            }

            if (n == 10) // 11 bits — remaining black codes and makeup
            {
                int v = B(b, 11);
                switch (v)
                {
                    case 0b00001101110: return (58, false);
                    case 0b00001101100: return (59, false);
                    case 0b00001101000: return (60, false);
                    case 0b00001101010: return (61, false);
                    case 0b00000110100: return (384, true);
                    case 0b00000110101: return (448, true);
                    case 0b00000110110: return (512, true);
                    case 0b00000110111: return (576, true);
                    case 0b00000011000: return (640, true);
                    case 0b00000011001: return (704, true);
                    case 0b00000011010: return (768, true);
                    case 0b00000011011: return (832, true);
                    case 0b00000010100: return (896, true);
                    case 0b00000010101: return (960, true);
                    case 0b00000010110: return (1024, true);
                    case 0b00000010111: return (1088, true);
                    case 0b00000011100: return (1152, true);
                    case 0b00000011101: return (1216, true);
                    case 0b00000001000: return (1280, true);
                    case 0b00000001100: return (1344, true);
                    case 0b00000001001: return (1408, true);
                    case 0b00000001101: return (1472, true);
                    case 0b00000001010: return (1536, true);
                    case 0b00000001110: return (1600, true);
                    case 0b00000001111: return (1664, true);
                    case 0b00000001011: return (1728, true);
                }
            }
        }

        throw new InvalidDataException("JBIG2 MMR: unrecognised black run-length code.");
    }

    // ── Changing-element helpers ──────────────────────────────────────────────

    /// <summary>
    /// Returns the first changing element on <paramref name="ce"/> to the right of
    /// <paramref name="a0"/> whose color is opposite to <paramref name="a0Col"/>.
    /// The CE array encodes color transitions starting from white at position 0.
    /// </summary>
    private static int FindB1(int[] ce, int a0, int a0Col)
    {
        // CE positions alternate white→black→white... The color at ce[0] is "start of
        // first black run" (i.e., ce[0] is the first pixel where color flips from white).
        // So ce[0] = start of first black run, ce[1] = end of first black run (start white), …
        // Color at the i-th boundary: before ce[i] = (i%2==0 ? white : black).
        // We want the first ce[i] > a0 such that the color AFTER ce[i] is opposite to a0Col.
        //   ce[i] separates color (i%2==0 ? white : black) from (i%2==0 ? black : white).
        //   The color just after ce[i] = (i%2==0 ? black : white) = 1 - (i%2).
        // We want 1 - (i%2) == 1 - a0Col, i.e. i%2 == a0Col.

        for (var i = 0; ; i++)
        {
            if (i >= ce.Length) return ce[ce.Length - 1];
            if (ce[i] > a0 && (i % 2) == a0Col)
                return ce[i];
        }
    }

    /// <summary>Returns the next CE value after index <paramref name="b1"/> in the array.</summary>
    private static int NextCE(int[] ce, int b1)
    {
        // b1 is a value in ce; find its index, then return the next.
        for (var i = 0; i < ce.Length - 1; i++)
        {
            if (ce[i] == b1)
                return ce[i + 1];
        }
        return ce[ce.Length - 1];
    }

    // ── Pixel fill ────────────────────────────────────────────────────────────

    private static void FillRun(byte[] output, int rowOffset, int from, int to, int color)
    {
        if (color == 0) return; // white = 0 bits, already zero
        for (var x = from; x < to; x++)
        {
            output[rowOffset + x / 8] |= (byte)(1 << (7 - (x % 8)));
        }
    }

    // ── Bit-assembly helper ───────────────────────────────────────────────────

    /// <summary>Packs the first <paramref name="count"/> bits of <paramref name="b"/> into an int (MSB first).</summary>
    private static int B(Span<int> b, int count)
    {
        var v = 0;
        for (var i = 0; i < count; i++)
            v = (v << 1) | b[i];
        return v;
    }

    // ── Bit reader ────────────────────────────────────────────────────────────

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bytePos;
        private int _bitPos; // 0 = MSB of current byte

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bytePos = 0;
            _bitPos = 0;
        }

        public int ReadBit()
        {
            if (_bytePos >= _data.Length)
                throw new InvalidDataException("JBIG2 MMR decoder: unexpected end of compressed data.");
            var bit = (_data[_bytePos] >> (7 - _bitPos)) & 1;
            if (++_bitPos == 8) { _bitPos = 0; _bytePos++; }
            return bit;
        }

        public bool TryReadBit(out int bit)
        {
            if (_bytePos >= _data.Length) { bit = 0; return false; }
            bit = (_data[_bytePos] >> (7 - _bitPos)) & 1;
            if (++_bitPos == 8) { _bitPos = 0; _bytePos++; }
            return true;
        }
    }
}
