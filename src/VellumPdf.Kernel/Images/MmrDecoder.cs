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
        // The changing-element scratch below is sized to the row width (8 bytes per pixel of
        // width). A width within MaxPixels but with an extreme aspect ratio (e.g. 100M×1)
        // could otherwise drive a ~800 MB scratch allocation from a few bytes of compressed
        // input, so bound each dimension before allocating.
        if (width > ImageLimits.MaxRasterDecodeDimension || height > ImageLimits.MaxRasterDecodeDimension)
            throw new InvalidDataException(
                $"JBIG2 MMR: dimension {width}×{height} exceeds the raster-decode safety limit " +
                $"of {ImageLimits.MaxRasterDecodeDimension} per side.");

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
                        // Runs are capped at the image width: a single run cannot exceed the
                        // line, and the cap also prevents integer overflow of the accumulated
                        // make-up total on malformed input.
                        var run1 = DecodeRun(ref reader, a0Col, width);
                        var run2 = DecodeRun(ref reader, 1 - a0Col, width);
                        var a1 = Math.Min(a0 + run1, width);
                        var a2 = Math.Min(a1 + run2, width);
                        FillRun(output, rowOffset, a0, a1, a0Col);
                        AppendCe(curCE, ref ceIdx, a1);
                        FillRun(output, rowOffset, a1, a2, 1 - a0Col);
                        AppendCe(curCE, ref ceIdx, a2);
                        a0 = a2;
                        // a0Col returns to original (two color transitions = net zero).
                        break;
                    }

                case Mode.Eofb:
                    // End-of-facsimile-block before all rows were decoded — the stream is
                    // truncated relative to the declared region height.
                    throw new InvalidDataException(
                        "JBIG2 MMR: unexpected EOFB before all rows were decoded.");

                default:
                    {
                        // Vertical modes V(0), V(+1..+3), V(-1..-3).
                        var delta = (int)mode; // encoded as the delta value directly
                        var b1 = FindB1(refCE, a0, a0Col);
                        var a1 = Math.Clamp(b1 + delta, a0, width);
                        FillRun(output, rowOffset, a0, a1, a0Col);
                        if (a1 != a0)
                            AppendCe(curCE, ref ceIdx, a1);
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

    // ── Mode codes (T.6 Table 1) ──────────────────────────────────────────────

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

    /// <summary>Reads the next T.6 two-dimensional mode codeword (MSB-first).</summary>
    private static int ReadMode(ref BitReader r)
    {
        // Table 1/T.6 (ITU-T T.6, 2.2.3), read as a prefix tree. Pairing the table's notation
        // and code-word columns correctly matters more than it looks: extracted naively the two
        // columns sit one row apart, which yields VR(1) = 1 and shifts every vertical mode.
        //
        //   1        V(0)         0001     Pass
        //   011      VR(1)  +1    001      Horizontal, then M(a0a1) + M(a1a2)
        //   010      VL(1)  -1    0000001  Extension prefix
        //   000011   VR(2)  +2
        //   000010   VL(2)  -2
        //   0000011  VR(3)  +3
        //   0000010  VL(3)  -3
        //
        // Clause 2.2.3.3 states the horizontal flag code directly, as "001" "taken from the
        // two-dimensional code table (Table 1/T.6)" — so it comes from the same table, just read
        // independently of how the notation and code-word columns happen to be laid out on the
        // page. That is what settles the entry a mis-aligned reading gets wrong: 011 is VR(1), not
        // Horizontal.
        //
        // Before #437 this tree disagreed with the table on six of those ten code words. Two of
        // them, 001 and 0001, also consumed one bit too many, so everything after them was read
        // at the wrong offset: the stream desynchronised rather than merely mis-decoding a mode.

        if (r.ReadBit() == 1)
            return 0; // 1 — V(0)

        if (r.ReadBit() == 1)
            return r.ReadBit() == 1 ? 1 : -1; // 011 — VR(1); 010 — VL(1)

        if (r.ReadBit() == 1)
            return Mode.Horizontal; // 001

        if (r.ReadBit() == 1)
            return Mode.Pass; // 0001

        if (r.ReadBit() == 1)
            return r.ReadBit() == 1 ? 2 : -2; // 000011 — VR(2); 000010 — VL(2)

        if (r.ReadBit() == 1)
            return r.ReadBit() == 1 ? 3 : -3; // 0000011 — VR(3); 0000010 — VL(3)

        if (r.ReadBit() == 1)
        {
            // 0000001 — the extension prefix. ITU-T T.88 6.2.6 forbids the extension codes of
            // T.6, uncompressed mode included, from appearing in MMR-encoded JBIG2 data, and a
            // JBIG2 generic region is the only caller that reaches this decoder. The clause
            // constrains the data, not the decoder's response; failing is a choice, not a
            // mandate. Before #437 this prefix fell through to the end-of-block scan below,
            // which returns Mode.Eofb, and DecodeRow's Mode.Eofb case has always thrown
            // InvalidDataException unconditionally — so it still threw, just under a message
            // that blamed truncation instead of naming the code that was present.
            throw new InvalidDataException(
                "JBIG2 MMR: T.6 extension code encountered; ITU-T T.88 6.2.6 forbids extension " +
                "codes in MMR-encoded JBIG2 data.");
        }

        // 0000000... — EOFB (two EOLs of 000000000001) or byte padding. Seven zero bits have been
        // consumed to reach here and an EOL is twelve bits, so five more decide it. Every path
        // through this loop returns Mode.Eofb, and DecodeRow's Mode.Eofb case throws
        // unconditionally either way, so which branch fires below is not otherwise observable.
        for (var i = 0; i < 5; i++)
        {
            if (r.TryReadBit(out var b) && b == 1)
            {
                // The EOL-terminating 1, or any stray 1 after the run of zeros.
                return Mode.Eofb;
            }
        }
        return Mode.Eofb;
    }

    // ── Run-length decoder (T.4 / Modified Huffman) ───────────────────────────

    /// <summary>
    /// Decodes one T.4 run length for the given <paramref name="color"/> (0=white, 1=black).
    /// A run may be made up of one or more makeup codes followed by a terminating code.
    /// The accumulated run is bounded by <paramref name="maxRun"/> (the image width); a run
    /// that exceeds it is rejected, which also guards against integer overflow of the
    /// accumulator on a malformed stream of makeup codes.
    /// </summary>
    private static int DecodeRun(ref BitReader r, int color, int maxRun)
    {
        var total = 0;
        while (true)
        {
            var (value, isMakeup) = ReadRun(ref r, color);
            total += value;
            if (total > maxRun)
                throw new InvalidDataException(
                    "JBIG2 MMR: decoded run length exceeds the image width.");
            if (!isMakeup) return total;
        }
    }

    // ── Run-length code tables (ITU-T T.4 Tables 2, 3a and 3b) ────────────────

    // Transcribed from the standard and kept as literal code words so a reviewer can diff this
    // block against Table 2, Table 3a and Table 3b directly. That check is the one #440 needed and
    // did not have: the black table these replace disagreed with Table 2 (terminating codes) in 60
    // of its 64 entries and with Table 3a (make-up codes) in all 27 of its 27, and was not either
    // table in any recognisable form, while the white table was correct apart from a missing run
    // of 1.
    //
    // Applying the old table's own lookup rule — shortest matching length wins — 48 of its 91
    // entries were shadowed by a shorter entry and could never have been reached at all: all six
    // 001101xxx runs, all six 000100xxx runs, the four 11-bit make-ups 384 through 576, and among
    // them 0100, 0101 and 0111, which sat behind the three-bit prefixes 010 and 011 that the reader
    // returned on first. A code word cannot coexist with its own prefix in a prefix code, so their
    // presence was the signal that the table had never been transcribed from a real one.
    // CodeTablesAreAPrefixCode in the tests now asserts that property, which closes the
    // unreachable-entry half of the defect. The other half was entries that were reachable and
    // simply wrong, and only the value-level known-answer vectors catch those.

    // ITU-T T.4 Table 2, white terminating codes, runs 0 to 63.
    private static readonly string[] WhiteTerminating =
    [
        "00110101", "000111", "0111", "1000",                       // 0-3
        "1011", "1100", "1110", "1111",                             // 4-7
        "10011", "10100", "00111", "01000",                         // 8-11
        "001000", "000011", "110100", "110101",                     // 12-15
        "101010", "101011", "0100111", "0001100",                   // 16-19
        "0001000", "0010111", "0000011", "0000100",                 // 20-23
        "0101000", "0101011", "0010011", "0100100",                 // 24-27
        "0011000", "00000010", "00000011", "00011010",              // 28-31
        "00011011", "00010010", "00010011", "00010100",             // 32-35
        "00010101", "00010110", "00010111", "00101000",             // 36-39
        "00101001", "00101010", "00101011", "00101100",             // 40-43
        "00101101", "00000100", "00000101", "00001010",             // 44-47
        "00001011", "01010010", "01010011", "01010100",             // 48-51
        "01010101", "00100100", "00100101", "01011000",             // 52-55
        "01011001", "01011010", "01011011", "01001010",             // 56-59
        "01001011", "00110010", "00110011", "00110100",             // 60-63
    ];

    // ITU-T T.4 Table 2, black terminating codes, runs 0 to 63.
    private static readonly string[] BlackTerminating =
    [
        "0000110111", "010", "11", "10",                            // 0-3
        "011", "0011", "0010", "00011",                             // 4-7
        "000101", "000100", "0000100", "0000101",                   // 8-11
        "0000111", "00000100", "00000111", "000011000",             // 12-15
        "0000010111", "0000011000", "0000001000", "00001100111",    // 16-19
        "00001101000", "00001101100", "00000110111", "00000101000", // 20-23
        "00000010111", "00000011000", "000011001010", "000011001011",// 24-27
        "000011001100", "000011001101", "000001101000", "000001101001",// 28-31
        "000001101010", "000001101011", "000011010010", "000011010011",// 32-35
        "000011010100", "000011010101", "000011010110", "000011010111",// 36-39
        "000001101100", "000001101101", "000011011010", "000011011011",// 40-43
        "000001010100", "000001010101", "000001010110", "000001010111",// 44-47
        "000001100100", "000001100101", "000001010010", "000001010011",// 48-51
        "000000100100", "000000110111", "000000111000", "000000100111",// 52-55
        "000000101000", "000001011000", "000001011001", "000000101011",// 56-59
        "000000101100", "000001011010", "000001100110", "000001100111",// 60-63
    ];

    // ITU-T T.4 Table 3a, white make-up codes, runs 64 to 1728 in steps of 64.
    private static readonly string[] WhiteMakeUp =
    [
        "11011", "10010", "010111", "0110111",                      // 64-256
        "00110110", "00110111", "01100100", "01100101",             // 320-512
        "01101000", "01100111", "011001100", "011001101",           // 576-768
        "011010010", "011010011", "011010100", "011010101",         // 832-1024
        "011010110", "011010111", "011011000", "011011001",         // 1088-1280
        "011011010", "011011011", "010011000", "010011001",         // 1344-1536
        "010011010", "011000", "010011011",                         // 1600-1728
    ];

    // ITU-T T.4 Table 3a, black make-up codes, runs 64 to 1728 in steps of 64.
    private static readonly string[] BlackMakeUp =
    [
        "0000001111", "000011001000", "000011001001", "000001011011",// 64-256
        "000000110011", "000000110100", "000000110101", "0000001101100",// 320-512
        "0000001101101", "0000001001010", "0000001001011", "0000001001100",// 576-768
        "0000001001101", "0000001110010", "0000001110011", "0000001110100",// 832-1024
        "0000001110101", "0000001110110", "0000001110111", "0000001010010",// 1088-1280
        "0000001010011", "0000001010100", "0000001010101", "0000001011010",// 1344-1536
        "0000001011011", "0000001100100", "0000001100101",          // 1600-1728
    ];

    // ITU-T T.4 Table 3b, the extended make-up codes 1792 to 2560, shared by both colours.
    private static readonly string[] SharedMakeUp =
    [
        "00000001000", "00000001100", "00000001101", "000000010010",// 1792-1984
        "000000010011", "000000010100", "000000010101", "000000010110",// 2048-2240
        "000000010111", "000000011100", "000000011101", "000000011110",// 2304-2496
        "000000011111",                                             // 2560
    ];

    // The longest code word in any of the three tables is 13 bits: twenty black make-up codes,
    // every one from run 512 through run 1728, are that length. A code word longer than that
    // cannot exist, so a reader that has consumed 13 bits without a match is looking at data that
    // is not T.4 at all.
    internal const int MaxRunCodeBits = 13;

    /// <summary>
    /// The two decoding tables as <see cref="ReadRun"/> composes them, for the prefix-code
    /// assertion in the tests. Each colour sees its own terminating and make-up codes plus the
    /// shared extended ones, and it is within one of those sets that no code word may be a prefix
    /// of another. Across the two colours code words collide freely and legitimately: 010 is black
    /// 1 and also the first three bits of white 11.
    /// </summary>
    internal static (string Colour, string[] CodeWords)[] DecodingTables =>
    [
        ("white", [.. ComposeEntries(WhiteTerminating, WhiteMakeUp).Select(e => e.CodeWord)]),
        ("black", [.. ComposeEntries(BlackTerminating, BlackMakeUp).Select(e => e.CodeWord)]),
    ];

    private static readonly Dictionary<int, (int Run, bool MakeUp)> WhiteCodes =
        BuildCodes(WhiteTerminating, WhiteMakeUp);

    private static readonly Dictionary<int, (int Run, bool MakeUp)> BlackCodes =
        BuildCodes(BlackTerminating, BlackMakeUp);

    /// <summary>
    /// Merges one colour's terminating and make-up codes with the shared extended make-up codes,
    /// pairing each code word with the run length and make-up flag it decodes to. This is the one
    /// place that composition happens: both <see cref="DecodingTables"/> (the prefix-code
    /// assertion's view) and <see cref="BuildCodes"/> (the decoder's lookup table) enumerate this
    /// same sequence, so they cannot drift into decoding a code word the prefix check never saw.
    /// </summary>
    private static IEnumerable<(string CodeWord, int Run, bool MakeUp)> ComposeEntries(
        string[] terminating, string[] makeUp)
    {
        for (var run = 0; run < terminating.Length; run++)
            yield return (terminating[run], run, false);
        for (var i = 0; i < makeUp.Length; i++)
            yield return (makeUp[i], 64 * (i + 1), true);
        for (var i = 0; i < SharedMakeUp.Length; i++)
            yield return (SharedMakeUp[i], 1792 + (64 * i), true);
    }

    /// <summary>
    /// Keys every code word by its length and value together. Length has to be part of the key
    /// because the tables are a prefix code over variable-length words: 11 is black 2 and 0011 is
    /// black 5, and their numeric values collide once the leading zeros are dropped.
    /// </summary>
    private static Dictionary<int, (int Run, bool MakeUp)> BuildCodes(string[] terminating, string[] makeUp)
    {
        var codes = new Dictionary<int, (int Run, bool MakeUp)>(terminating.Length + makeUp.Length + SharedMakeUp.Length);
        foreach (var (codeWord, run, makeup) in ComposeEntries(terminating, makeUp))
            codes.Add(Key(codeWord), (run, makeup));
        return codes;
    }

    /// <summary>Packs a code word's bit length and value into one lookup key.</summary>
    private static int Key(string codeWord)
    {
        var value = 0;
        foreach (var c in codeWord)
            value = (value << 1) | (c == '1' ? 1 : 0);
        return Key(codeWord.Length, value);
    }

    /// <summary>Packs a bit length and value into the same lookup key <see cref="Key(string)"/> uses.</summary>
    private static int Key(int length, int value) => (length << 16) | value;

    /// <summary>
    /// Reads one T.4 run-length code word of the given colour, MSB-first, by consuming bits until
    /// the accumulated value matches a code word of exactly that length.
    /// </summary>
    private static (int run, bool makeup) ReadRun(ref BitReader r, int color)
    {
        var codes = color == 0 ? WhiteCodes : BlackCodes;
        var value = 0;
        for (var bits = 1; bits <= MaxRunCodeBits; bits++)
        {
            value = (value << 1) | r.ReadBit();
            if (codes.TryGetValue(Key(bits, value), out var hit))
                return (hit.Run, hit.MakeUp);
        }

        throw new InvalidDataException(
            "JBIG2 MMR: run-length code word is not in ITU-T T.4 Table 2, 3a or 3b.");
    }


    /// <summary>
    /// Appends a changing-element x-coordinate to <paramref name="ce"/>, rejecting overflow.
    /// A valid row has at most <c>width</c> transitions, so the array (sized <c>width + 2</c>)
    /// cannot legitimately overflow; a malformed 2D stream (e.g. repeated zero-length
    /// horizontal runs) can drive the index past the end, which this guard converts into an
    /// <see cref="InvalidDataException"/> rather than an <see cref="IndexOutOfRangeException"/>.
    /// </summary>
    private static void AppendCe(int[] ce, ref int idx, int value)
    {
        if (idx >= ce.Length)
            throw new InvalidDataException(
                "JBIG2 MMR: too many changing elements in a row (malformed 2D data).");
        ce[idx++] = value;
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
