// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Creates a PDF Image XObject from raw CCITT Group 3 or Group 4 compressed bytes.
///
/// <para><b>Default behaviour (passthrough):</b> bytes are embedded verbatim as a
/// <c>/CCITTFaxDecode</c> stream — no CCITT decoding is performed. The PDF viewer decodes
/// the stream at render time.</para>
///
/// <para>When <see cref="ImageDecodeMode.DecodeToRaster"/> is requested via
/// <see cref="ImageLoadOptions.DecodeMode"/>, the stream is decoded to a 1-bpp raster and
/// re-encoded with <c>/FlateDecode</c> (lossless). Only 1D (K=0 and K&lt;0/G4) rows are
/// decoded; 2D (K&gt;0, Modified READ) rows throw <see cref="NotSupportedException"/>.</para>
///
/// <para><b>K parameter semantics (ISO 32000-2 Table 10):</b></para>
/// <list type="bullet">
///   <item><c>k &lt; 0</c> — Group 4 (T.6), pure 2D MMR. Most CCITT fax TIFFs use this.</item>
///   <item><c>k = 0</c> — Group 3, 1D encoding (T.4 without 2D rows).</item>
///   <item><c>k &gt; 0</c> — Group 3, mixed 1D/2D (T.4 with at most k−1 2D rows between 1D rows).</item>
/// </list>
///
/// <para><b>Columns and Rows</b> are required; they tell the decoder the image geometry.</para>
///
/// <para><b>BlackIs1</b> controls polarity: when <see langword="false"/> (the PDF default)
/// bit value 0 = black and 1 = white, which is the standard fax convention.
/// Pass <see langword="true"/> only when the source data uses the opposite convention.</para>
///
/// <para><b>EndOfLine</b>: when <see langword="true"/>, emits <c>/EndOfLine true</c> in
/// /DecodeParms, indicating that the T.4 stream contains explicit EOL codes before each row
/// (Group 3 streams). Defaults to <see langword="false"/> (Group 4 / no EOLs).</para>
/// </summary>
public static class CcittImageLoader
{
    /// <summary>
    /// Wraps raw CCITT compressed bytes as a <c>/CCITTFaxDecode</c> Image XObject
    /// (passthrough — bytes are embedded verbatim).
    /// </summary>
    /// <param name="ccittData">The raw CCITT-compressed bytes. Must be non-empty.</param>
    /// <param name="columns">Image width in pixels. Must be positive.</param>
    /// <param name="rows">Image height in pixels. Must be positive.</param>
    /// <param name="k">
    /// The K value for /DecodeParms: negative = Group 4 (T.6 MMR), 0 = Group 3 1D, positive = Group 3 mixed.
    /// Defaults to -1 (Group 4).
    /// </param>
    /// <param name="blackIs1">
    /// When <see langword="true"/>, emits <c>/BlackIs1 true</c> in /DecodeParms (bit 1 = black).
    /// Omitted when <see langword="false"/> (the PDF default, bit 0 = black).
    /// </param>
    /// <param name="encodedByteAlign">
    /// When <see langword="true"/>, emits <c>/EncodedByteAlign true</c> in /DecodeParms (each row
    /// is padded to a byte boundary). Omitted when <see langword="false"/> (the PDF default).
    /// </param>
    /// <param name="endOfLine">
    /// When <see langword="true"/>, emits <c>/EndOfLine true</c> in /DecodeParms, indicating that
    /// explicit EOL codes are present in the T.4 stream before each row (Group 3). Omitted when
    /// <see langword="false"/> (the PDF default, used for Group 4 and bare Group 3 without EOLs).
    /// </param>
    /// <returns>A <see cref="PdfImageXObject"/> with /Filter /CCITTFaxDecode and the correct /DecodeParms.</returns>
    public static PdfImageXObject Load(
        byte[] ccittData,
        int columns,
        int rows,
        int k = -1,
        bool blackIs1 = false,
        bool encodedByteAlign = false,
        bool endOfLine = false)
    {
        return LoadCore(ccittData, columns, rows, ImageLoadOptions.Default, k, blackIs1, encodedByteAlign, endOfLine);
    }

    /// <summary>
    /// Wraps raw CCITT compressed bytes as an Image XObject with explicit load options.
    /// When <paramref name="options"/>.<see cref="ImageLoadOptions.DecodeMode"/> is
    /// <see cref="ImageDecodeMode.DecodeToRaster"/>, the stream is decoded to a 1-bpp raster
    /// and re-encoded with <c>/FlateDecode</c> (lossless). Only 1D rows (K=0) are supported
    /// for raster decode; 2D mixed-mode rows (K&gt;0) and Group 4 (K&lt;0) throw
    /// <see cref="NotSupportedException"/>. Uses passthrough defaults: K=-1 (Group 4),
    /// BlackIs1=false, EncodedByteAlign=false, EndOfLine=false.
    /// </summary>
    /// <param name="ccittData">The raw CCITT-compressed bytes. Must be non-empty.</param>
    /// <param name="columns">Image width in pixels. Must be positive.</param>
    /// <param name="rows">Image height in pixels. Must be positive.</param>
    /// <param name="options">Load options (decode mode). Must not be null.</param>
    /// <returns>
    /// A <see cref="PdfImageXObject"/> with the appropriate filter and /DecodeParms.
    /// </returns>
    public static PdfImageXObject Load(byte[] ccittData, int columns, int rows, ImageLoadOptions options)
    {
        return LoadCore(ccittData, columns, rows, options, k: -1, blackIs1: false, encodedByteAlign: false, endOfLine: false);
    }

    private static PdfImageXObject LoadCore(
        byte[] ccittData,
        int columns,
        int rows,
        ImageLoadOptions options,
        int k,
        bool blackIs1,
        bool encodedByteAlign,
        bool endOfLine)
    {
        if (ccittData is null || ccittData.Length == 0)
            throw new ArgumentException("CCITT data must be non-empty.", nameof(ccittData));
        if (columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Columns must be positive.");
        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Rows must be positive.");
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        ImageLimits.ValidateDimensions("CCITT", columns, rows);

        if (options.DecodeMode == ImageDecodeMode.DecodeToRaster)
        {
            var raster = DecodeCcittToRaster(ccittData, columns, rows, k, blackIs1, encodedByteAlign);
            return new PdfImageXObject(columns, rows, raster, PdfName.FlateDecode, ImageColorSpace.DeviceGray, bitsPerComponent: 1);
        }

        return Build(ccittData, columns, rows, k, blackIs1, encodedByteAlign, endOfLine);
    }

    /// <summary>
    /// Core builder shared by <see cref="Load(byte[], int, int, int, bool, bool, bool)"/>,
    /// <see cref="Load(byte[], int, int, ImageLoadOptions)"/>, and
    /// <see cref="TiffImageLoader"/>. Callers are responsible for validation before calling
    /// this method.
    /// </summary>
    internal static PdfImageXObject Build(
        byte[] data,
        int columns,
        int rows,
        int k,
        bool blackIs1,
        bool encodedByteAlign,
        bool endOfLine = false)
    {
        var dp = new PdfDictionary()
            .Set(new PdfName("K"), new PdfInteger(k))
            .Set(new PdfName("Columns"), new PdfInteger(columns))
            .Set(new PdfName("Rows"), new PdfInteger(rows));

        if (blackIs1)
            dp.Set(new PdfName("BlackIs1"), PdfBoolean.True);

        if (encodedByteAlign)
            dp.Set(new PdfName("EncodedByteAlign"), PdfBoolean.True);

        if (endOfLine)
            dp.Set(new PdfName("EndOfLine"), PdfBoolean.True);

        return new PdfImageXObject(
            columns, rows, data,
            PdfName.CCITTFaxDecode,
            ImageColorSpace.DeviceGray,
            bitsPerComponent: 1,
            decodeParms: dp);
    }

    // ── CCITT T.4 1D (Modified Huffman) decoder ───────────────────────────────

    // White run-length Huffman codes (terminating, 0–63 runs).
    // Each entry: (code, codeLength, runLength).
    private static readonly (ushort Code, int Len, int Run)[] WhiteTerminating =
    [
        (0b00110101, 8, 0),
        (0b000111, 6, 1),
        (0b0111, 4, 2),
        (0b1000, 4, 3),
        (0b1011, 4, 4),
        (0b1100, 4, 5),
        (0b1110, 4, 6),
        (0b1111, 4, 7),
        (0b10011, 5, 8),
        (0b10100, 5, 9),
        (0b00111, 5, 10),
        (0b01000, 5, 11),
        (0b001000, 6, 12),
        (0b000011, 6, 13),
        (0b110100, 6, 14),
        (0b110101, 6, 15),
        (0b101010, 6, 16),
        (0b101011, 6, 17),
        (0b0100111, 7, 18),
        (0b0001100, 7, 19),
        (0b0001000, 7, 20),
        (0b0010111, 7, 21),
        (0b0000011, 7, 22),
        (0b0000100, 7, 23),
        (0b0101000, 7, 24),
        (0b0101011, 7, 25),
        (0b0010011, 7, 26),
        (0b0100100, 7, 27),
        (0b0011000, 7, 28),
        (0b00000010, 8, 29),
        (0b00000011, 8, 30),
        (0b00011010, 8, 31),
        (0b00011011, 8, 32),
        (0b00010010, 8, 33),
        (0b00010011, 8, 34),
        (0b00010100, 8, 35),
        (0b00010101, 8, 36),
        (0b00010110, 8, 37),
        (0b00010111, 8, 38),
        (0b00101000, 8, 39),
        (0b00101001, 8, 40),
        (0b00101010, 8, 41),
        (0b00101011, 8, 42),
        (0b00101100, 8, 43),
        (0b00101101, 8, 44),
        (0b00000100, 8, 45),
        (0b00000101, 8, 46),
        (0b00001010, 8, 47),
        (0b00001011, 8, 48),
        (0b01010010, 8, 49),
        (0b01010011, 8, 50),
        (0b01010100, 8, 51),
        (0b01010101, 8, 52),
        (0b00100100, 8, 53),
        (0b00100101, 8, 54),
        (0b01011000, 8, 55),
        (0b01011001, 8, 56),
        (0b01011010, 8, 57),
        (0b01011011, 8, 58),
        (0b01001010, 8, 59),
        (0b01001011, 8, 60),
        (0b00110010, 8, 61),
        (0b00110011, 8, 62),
        (0b00110100, 8, 63),
    ];

    // White make-up codes (64, 128, ..., 1728).
    private static readonly (ushort Code, int Len, int Run)[] WhiteMakeUp =
    [
        (0b11011, 5, 64),
        (0b10010, 5, 128),
        (0b010111, 6, 192),
        (0b0110111, 7, 256),
        (0b00110110, 8, 320),
        (0b00110111, 8, 384),
        (0b01100100, 8, 448),
        (0b01100101, 8, 512),
        (0b01101000, 8, 576),
        (0b01100111, 8, 640),
        (0b011001100, 9, 704),
        (0b011001101, 9, 768),
        (0b011010010, 9, 832),
        (0b011010011, 9, 896),
        (0b011010100, 9, 960),
        (0b011010101, 9, 1024),
        (0b011010110, 9, 1088),
        (0b011010111, 9, 1152),
        (0b011011000, 9, 1216),
        (0b011011001, 9, 1280),
        (0b011011010, 9, 1344),
        (0b011011011, 9, 1408),
        (0b010011000, 9, 1472),
        (0b010011001, 9, 1536),
        (0b010011010, 9, 1600),
        (0b011000, 6, 1664),
        (0b010011011, 9, 1728),
    ];

    // Black run-length Huffman codes (terminating, 0–63 runs).
    private static readonly (ushort Code, int Len, int Run)[] BlackTerminating =
    [
        (0b0000110111, 10, 0),
        (0b010, 3, 1),
        (0b11, 2, 2),
        (0b10, 2, 3),
        (0b011, 3, 4),
        (0b0011, 4, 5),
        (0b0010, 4, 6),
        (0b00011, 5, 7),
        (0b000101, 6, 8),
        (0b000100, 6, 9),
        (0b0000100, 7, 10),
        (0b0000101, 7, 11),
        (0b0000111, 7, 12),
        (0b00000100, 8, 13),
        (0b00000111, 8, 14),
        (0b000011000, 9, 15),
        (0b0000010111, 10, 16),
        (0b0000011000, 10, 17),
        (0b0000001000, 10, 18),
        (0b00001100111, 11, 19),
        (0b00001101000, 11, 20),
        (0b00001101100, 11, 21),
        (0b00000110111, 11, 22),
        (0b00000101000, 11, 23),
        (0b00000010111, 11, 24),
        (0b00000011000, 11, 25),
        (0b000011001010, 12, 26),
        (0b000011001011, 12, 27),
        (0b000011001100, 12, 28),
        (0b000011001101, 12, 29),
        (0b000001101000, 12, 30),
        (0b000001101001, 12, 31),
        (0b000001101010, 12, 32),
        (0b000001101011, 12, 33),
        (0b000011010010, 12, 34),
        (0b000011010011, 12, 35),
        (0b000011010100, 12, 36),
        (0b000011010101, 12, 37),
        (0b000011010110, 12, 38),
        (0b000011010111, 12, 39),
        (0b000001101100, 12, 40),
        (0b000001101101, 12, 41),
        (0b000011011010, 12, 42),
        (0b000011011011, 12, 43),
        (0b000001010100, 12, 44),
        (0b000001010101, 12, 45),
        (0b000001010110, 12, 46),
        (0b000001010111, 12, 47),
        (0b000001100100, 12, 48),
        (0b000001100101, 12, 49),
        (0b000001010010, 12, 50),
        (0b000001010011, 12, 51),
        (0b000000100100, 12, 52),
        (0b000000110111, 12, 53),
        (0b000000111000, 12, 54),
        (0b000000100111, 12, 55),
        (0b000000101000, 12, 56),
        (0b000001011000, 12, 57),
        (0b000001011001, 12, 58),
        (0b000000101011, 12, 59),
        (0b000000101100, 12, 60),
        (0b000001011010, 12, 61),
        (0b000001100110, 12, 62),
        (0b000001100111, 12, 63),
    ];

    // Black make-up codes (64, 128, ..., 1728).
    private static readonly (ushort Code, int Len, int Run)[] BlackMakeUp =
    [
        (0b0000001111, 10, 64),
        (0b000011001000, 12, 128),
        (0b000011001001, 12, 192),
        (0b000001011011, 12, 256),
        (0b000000110011, 12, 320),
        (0b000000110100, 12, 384),
        (0b000000110101, 12, 448),
        (0b0000001101100, 13, 512),
        (0b0000001101101, 13, 576),
        (0b0000001001010, 13, 640),
        (0b0000001001011, 13, 704),
        (0b0000001001100, 13, 768),
        (0b0000001001101, 13, 832),
        (0b0000001110010, 13, 896),
        (0b0000001110011, 13, 960),
        (0b0000001110100, 13, 1024),
        (0b0000001110101, 13, 1088),
        (0b0000001110110, 13, 1152),
        (0b0000001110111, 13, 1216),
        (0b0000001010010, 13, 1280),
        (0b0000001010011, 13, 1344),
        (0b0000001010100, 13, 1408),
        (0b0000001010101, 13, 1472),
        (0b0000001011010, 13, 1536),
        (0b0000001011011, 13, 1600),
        (0b0000001100100, 13, 1664),
        (0b0000001100101, 13, 1728),
    ];

    // Extended make-up codes shared by both white and black (1792..2560).
    private static readonly (ushort Code, int Len, int Run)[] ExtendedMakeUp =
    [
        (0b00000001000, 11, 1792),
        (0b00000001100, 11, 1856),
        (0b00000001101, 11, 1920),
        (0b000000010010, 12, 1984),
        (0b000000010011, 12, 2048),
        (0b000000010100, 12, 2112),
        (0b000000010101, 12, 2176),
        (0b000000010110, 12, 2240),
        (0b000000010111, 12, 2304),
        (0b000000011100, 12, 2368),
        (0b000000011101, 12, 2432),
        (0b000000011110, 12, 2496),
        (0b000000011111, 12, 2560),
    ];

    // EOL code: 000000000001 (12 bits)
    private const ushort EolCode = 0b000000000001;
    private const int EolLen = 12;

    /// <summary>
    /// Decodes a CCITT T.4 1D (Modified Huffman) or T.6 (Group 4 MMR) stream to a 1-bpp
    /// packed raster (MSB-first, rows padded to byte boundaries).
    /// <para>T.6 (k &lt; 0) and T.4 1D (k = 0) are decoded. T.4 mixed 1D/2D (k &gt; 0)
    /// throws <see cref="NotSupportedException"/> for 2D rows.</para>
    /// </summary>
    internal static byte[] DecodeCcittToRaster(
        byte[] data,
        int columns,
        int rows,
        int k,
        bool blackIs1,
        bool encodedByteAlign)
    {
        if (k < 0)
            throw new NotSupportedException(
                "DecodeToRaster for CCITT Group 4 (T.6 MMR, K<0) is not supported. " +
                "Use passthrough (the default) for Group 4 streams.");

        // k >= 0 → T.4 1D or mixed. We support 1D only (k=0 or k>0 where all rows are 1D).
        // Row byte count (padded to byte boundary).
        var rowBytes = (columns + 7) / 8;
        var raster = new byte[(long)rows * rowBytes];
        var reader = new BitReader(data);

        for (var row = 0; row < rows; row++)
        {
            // T.4 streams with EndOfLine=true: skip EOL before each row.
            // Also skip fill bits (byte-align if EncodedByteAlign=true).
            if (encodedByteAlign)
                reader.ByteAlign();

            // Try to consume EOL (12 zero bits + 1 one bit = 000000000001).
            // EOL is optional in 1D T.4 streams; skip if present.
            reader.TryConsumeEol(EolCode, EolLen);

            // Check for 2D row indicator bit when k > 0.
            if (k > 0)
            {
                // T.4 mixed mode: a 1-bit tag follows EOL. 1=1D row, 0=2D row.
                var tag = reader.ReadBit();
                if (tag == 0)
                    throw new NotSupportedException(
                        "DecodeToRaster does not support 2D (Modified READ) rows in mixed-mode " +
                        "CCITT T.4 streams (K>0). Use passthrough (the default) for 2D streams.");
                // tag == 1 → 1D row, continue normally.
            }

            // Decode one 1D row.
            var rowBase = row * rowBytes;
            DecodeRow1D(reader, raster, rowBase, columns, blackIs1);
        }

        return raster;
    }

    /// <summary>Decodes one T.4 1D Modified Huffman encoded row into <paramref name="raster"/>.</summary>
    private static void DecodeRow1D(
        BitReader reader,
        byte[] raster,
        int rowBase,
        int columns,
        bool blackIs1)
    {
        var col = 0;
        var white = true; // T.4 rows always start with a white run (even if run length is 0)

        while (col < columns)
        {
            int runLength = ReadRunLength(reader, white);

            // Bound: total run cannot exceed remaining pixels.
            if (runLength < 0 || col + runLength > columns)
                throw new InvalidDataException(
                    $"CCITT T.4: run length {runLength} at column {col} exceeds row width {columns}.");

            // Write bits into raster.
            // PDF CCITTFaxDecode: bit 0 = black (when BlackIs1=false).
            // We produce a raster where 1 = black (the natural 1-bpp meaning).
            // If blackIs1=false: white run → bit=0, black run → bit=1.
            // If blackIs1=true: white run → bit=1, black run → bit=0.
            bool emitOne = white ? blackIs1 : !blackIs1;

            if (emitOne)
            {
                for (var i = 0; i < runLength; i++)
                {
                    var bitIdx = col + i;
                    raster[rowBase + bitIdx / 8] |= (byte)(0x80 >> (bitIdx % 8));
                }
            }
            // else: bits are already 0 from array initialisation.

            col += runLength;
            white = !white;
        }
    }

    /// <summary>
    /// Reads one run-length code (make-up + terminating) for either white or black runs.
    /// Returns the total run length. Throws <see cref="InvalidDataException"/> on truncation
    /// or unrecognised code.
    /// </summary>
    private static int ReadRunLength(BitReader reader, bool white)
    {
        var total = 0;

        // Read make-up codes (each adds a multiple of 64) until we get a terminating code.
        while (true)
        {
            int run = TryMatchCodes(reader, white);
            if (run < 0)
                throw new InvalidDataException(
                    $"CCITT T.4: unrecognised Huffman code reading {(white ? "white" : "black")} run.");

            if (run < 64)
            {
                // Terminating code.
                total += run;
                return total;
            }

            // Make-up code: accumulate and continue.
            total += run;

            // Guard: prevent infinite loop on malformed make-up sequences.
            if (total > 2560 + 64)
                throw new InvalidDataException(
                    "CCITT T.4: make-up code sequence exceeds maximum run length.");
        }
    }

    /// <summary>
    /// Attempts to match white or black Huffman codes at the current bit position.
    /// Returns the run length on match, or -1 if no code matched.
    /// Advances the reader only on a successful match.
    /// </summary>
    private static int TryMatchCodes(BitReader reader, bool white)
    {
        var terminating = white ? WhiteTerminating : BlackTerminating;
        var makeUp = white ? WhiteMakeUp : BlackMakeUp;

        // Try terminating codes first (they share prefixes with make-up in some cases;
        // we try all and pick longest match — actually T.4 codes are prefix-free within
        // each table, so we just scan for the first match).
        int runLen = TryScan(reader, terminating);
        if (runLen >= 0)
            return runLen;

        runLen = TryScan(reader, makeUp);
        if (runLen >= 0)
            return runLen;

        // Extended make-up codes (shared).
        runLen = TryScan(reader, ExtendedMakeUp);
        if (runLen >= 0)
            return runLen;

        return -1;
    }

    /// <summary>Scans a code table, returning the run length of the first match.</summary>
    private static int TryScan(BitReader reader, (ushort Code, int Len, int Run)[] table)
    {
        foreach (var (code, len, run) in table)
        {
            if (reader.TryMatch(code, len))
                return run;
        }
        return -1;
    }

    // ── BitReader: MSB-first bit reader over a byte array ────────────────────

    /// <summary>
    /// MSB-first bit reader. Reads bits from <c>data[offset]</c> starting at the most
    /// significant bit. Used by the CCITT T.4 decoder.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bytePos;
        private int _bitPos; // 0–7, 0 = MSB

        public BitReader(byte[] data)
        {
            _data = data;
            _bytePos = 0;
            _bitPos = 0;
        }

        /// <summary>Returns true when all bits are consumed.</summary>
        public readonly bool IsEmpty => _bytePos >= _data.Length;

        /// <summary>
        /// Reads a single bit. Returns 0 or 1. Throws <see cref="InvalidDataException"/> on truncation.
        /// </summary>
        public int ReadBit()
        {
            if (_bytePos >= _data.Length)
                throw new InvalidDataException("CCITT T.4: unexpected end of stream reading bit.");
            var bit = (_data[_bytePos] >> (7 - _bitPos)) & 1;
            Advance(1);
            return bit;
        }

        /// <summary>
        /// Aligns the reader to the next byte boundary (discards remaining bits in the current byte).
        /// </summary>
        public void ByteAlign()
        {
            if (_bitPos != 0)
            {
                _bytePos++;
                _bitPos = 0;
            }
        }

        /// <summary>
        /// Attempts to match a bit pattern of <paramref name="len"/> bits at the current position.
        /// If matched, advances past the pattern and returns <see langword="true"/>.
        /// If not matched, does not advance and returns <see langword="false"/>.
        /// </summary>
        public bool TryMatch(ushort code, int len)
        {
            // Peek len bits without advancing.
            var available = (_data.Length - _bytePos) * 8 - _bitPos;
            if (available < len)
                return false;

            var accumulated = 0;
            var bp = _bytePos;
            var bit = _bitPos;
            for (var i = 0; i < len; i++)
            {
                accumulated = (accumulated << 1) | ((_data[bp] >> (7 - bit)) & 1);
                bit++;
                if (bit == 8) { bit = 0; bp++; }
            }

            if (accumulated != code)
                return false;

            Advance(len);
            return true;
        }

        /// <summary>
        /// Tries to consume an EOL code. If the next bits do not match, the position
        /// is not advanced. EOL in T.4 is 12 bits: 000000000001.
        /// </summary>
        public void TryConsumeEol(ushort eolCode, int eolLen)
        {
            TryMatch(eolCode, eolLen);
        }

        private void Advance(int bits)
        {
            _bitPos += bits;
            _bytePos += _bitPos / 8;
            _bitPos %= 8;
        }
    }
}
