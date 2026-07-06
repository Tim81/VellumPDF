// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Qr;

/// <summary>The three data-encoding modes this encoder supports for QR and Micro QR (Kanji, ISO/IEC 18004 §7.4.6, is omitted).</summary>
internal enum QrSegmentMode
{
    /// <summary>Digits 0-9 only, packed three to ten bits (ISO/IEC 18004 §7.4.3).</summary>
    Numeric,

    /// <summary>The 45-character set of <see cref="QrTables.AlphanumericCharset"/>, packed two to eleven bits (§7.4.4).</summary>
    Alphanumeric,

    /// <summary>Arbitrary bytes, one codeword per byte (§7.4.5).</summary>
    Byte,
}

/// <summary>
/// A version/error-correction level's error-correction block layout (ISO/IEC 18004 Table 9): the
/// total codewords the symbol carries, how many error-correction codewords protect each block,
/// and the one or two groups of same-sized data blocks the codewords are split across.
/// </summary>
internal readonly record struct QrEcBlockInfo(
    int TotalCodewords,
    int EcCodewordsPerBlock,
    int Group1Blocks,
    int Group1DataCodewords,
    int Group2Blocks,
    int Group2DataCodewords)
{
    /// <summary>The total number of data codewords (before error correction) across both groups.</summary>
    internal int TotalDataCodewords => (Group1Blocks * Group1DataCodewords) + (Group2Blocks * Group2DataCodewords);

    /// <summary>The total number of error-correction blocks across both groups.</summary>
    internal int TotalBlocks => Group1Blocks + Group2Blocks;
}

/// <summary>
/// A Micro QR version/error-correction level's single-block codeword split (ISO/IEC 18004 Table 7).
/// </summary>
/// <param name="DataCodewords">
/// The number of data codewords; for versions M1 and M3, the last of these is only 4 bits wide
/// (<paramref name="LastCodewordIsHalfWidth"/>).
/// </param>
/// <param name="EcCodewords">The number of (always full 8-bit) error-correction codewords.</param>
/// <param name="LastCodewordIsHalfWidth">Whether the final data codeword is 4 bits wide rather than 8 (versions M1 and M3).</param>
internal readonly record struct MicroQrCapacity(int DataCodewords, int EcCodewords, bool LastCodewordIsHalfWidth)
{
    /// <summary>The total number of data bits: 8 per codeword, except the last one when <see cref="LastCodewordIsHalfWidth"/>.</summary>
    internal int DataBits => (DataCodewords * 8) - (LastCodewordIsHalfWidth ? 4 : 0);
}

/// <summary>
/// Static tables from ISO/IEC 18004:2015: the alphanumeric charset (Table 5), mode indicators and
/// character/terminator bit widths (Table 2, Table 3), the QR error-correction block layout for
/// every version and level (Table 9), and alignment pattern centres (Annex E). Micro QR's own mode
/// indicator, character count and terminator widths (also Table 2/Table 3) live alongside the QR
/// ones since both symbologies share the same two tables.
/// </summary>
internal static class QrTables
{
    /// <summary>The 45-character alphanumeric-mode charset, in value order 0-44 (ISO/IEC 18004 Table 5).</summary>
    internal const string AlphanumericCharset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    /// <summary>The 4-bit QR Code mode indicator for <paramref name="mode"/> (Table 2).</summary>
    internal static int ModeIndicator(QrSegmentMode mode) => mode switch
    {
        QrSegmentMode.Numeric => 0b0001,
        QrSegmentMode.Alphanumeric => 0b0010,
        QrSegmentMode.Byte => 0b0100,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>The 4-bit QR Code ECI mode indicator (Table 2).</summary>
    internal const int EciModeIndicator = 0b0111;

    /// <summary>The bit width of every QR Code mode indicator, including ECI and the terminator.</summary>
    internal const int ModeIndicatorBits = 4;

    /// <summary>The number of bits in a QR Code terminator (Table 2); shortened when capacity does not allow the full width.</summary>
    internal const int TerminatorBits = 4;

    /// <summary>The two ISO/IEC 18004 Table 2 pad codewords, alternated to fill unused data capacity.</summary>
    internal static readonly byte[] PadCodewords = [0b1110_1100, 0b0001_0001];

    /// <summary>The number of bits in the character count indicator for <paramref name="mode"/> at QR Code <paramref name="version"/> (Table 3).</summary>
    internal static int CharacterCountBits(int version, QrSegmentMode mode)
    {
        var group = version <= 9 ? 0 : version <= 26 ? 1 : 2;
        return (mode, group) switch
        {
            (QrSegmentMode.Numeric, 0) => 10,
            (QrSegmentMode.Numeric, 1) => 12,
            (QrSegmentMode.Numeric, _) => 14,
            (QrSegmentMode.Alphanumeric, 0) => 9,
            (QrSegmentMode.Alphanumeric, 1) => 11,
            (QrSegmentMode.Alphanumeric, _) => 13,
            (QrSegmentMode.Byte, 0) => 8,
            (QrSegmentMode.Byte, _) => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    /// <summary>The Micro QR mode indicator bit width for symbol version <paramref name="microVersion"/> (1-4, Table 2); 0 for M1 (numeric only, no indicator).</summary>
    internal static int MicroModeIndicatorBits(int microVersion) => microVersion - 1;

    /// <summary>
    /// The Micro QR mode indicator value for <paramref name="mode"/> at symbol version
    /// <paramref name="microVersion"/> (Table 2), sized to <see cref="MicroModeIndicatorBits"/>.
    /// </summary>
    internal static int MicroModeIndicator(int microVersion, QrSegmentMode mode) => (microVersion, mode) switch
    {
        (1, QrSegmentMode.Numeric) => 0, // no indicator bits; value is unused
        (2, QrSegmentMode.Numeric) => 0b0,
        (2, QrSegmentMode.Alphanumeric) => 0b1,
        (3, QrSegmentMode.Numeric) => 0b00,
        (3, QrSegmentMode.Alphanumeric) => 0b01,
        (3, QrSegmentMode.Byte) => 0b10,
        (4, QrSegmentMode.Numeric) => 0b000,
        (4, QrSegmentMode.Alphanumeric) => 0b001,
        (4, QrSegmentMode.Byte) => 0b010,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Mode {mode} is not available in Micro QR version M{microVersion}."),
    };

    /// <summary>The Micro QR character count indicator bit width for <paramref name="mode"/> at symbol version <paramref name="microVersion"/> (Table 3).</summary>
    internal static int MicroCharacterCountBits(int microVersion, QrSegmentMode mode) => (microVersion, mode) switch
    {
        (1, QrSegmentMode.Numeric) => 3,
        (2, QrSegmentMode.Numeric) => 4,
        (2, QrSegmentMode.Alphanumeric) => 3,
        (3, QrSegmentMode.Numeric) => 5,
        (3, QrSegmentMode.Alphanumeric) => 4,
        (3, QrSegmentMode.Byte) => 4,
        (4, QrSegmentMode.Numeric) => 6,
        (4, QrSegmentMode.Alphanumeric) => 5,
        (4, QrSegmentMode.Byte) => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Mode {mode} is not available in Micro QR version M{microVersion}."),
    };

    /// <summary>The Micro QR terminator bit width for symbol version <paramref name="microVersion"/> (Table 2): 3, 5, 7 or 9 zero bits for M1-M4.</summary>
    internal static int MicroTerminatorBits(int microVersion) => (2 * microVersion) + 1;

    /// <summary>Returns the character value (0-44) of <paramref name="c"/> in <see cref="AlphanumericCharset"/>, or -1 if it is not an alphanumeric-mode character.</summary>
    internal static int AlphanumericValue(char c) => AlphanumericCharset.IndexOf(c, StringComparison.Ordinal);

    /// <summary>Returns the QR error-correction block layout for <paramref name="version"/> (1-40) and <paramref name="level"/> (ISO/IEC 18004 Table 9).</summary>
    internal static QrEcBlockInfo GetEcBlockInfo(int version, QrErrorCorrection level)
    {
        if (version is < 1 or > 40)
            throw new ArgumentOutOfRangeException(nameof(version), version, "QR Code version must be between 1 and 40.");
        return EcBlockTable[version - 1][(int)level];
    }

    /// <summary>Returns the alignment pattern centre coordinates for <paramref name="version"/> (empty for version 1, ISO/IEC 18004 Annex E).</summary>
    internal static IReadOnlyList<int> GetAlignmentCentres(int version)
    {
        if (version is < 1 or > 40)
            throw new ArgumentOutOfRangeException(nameof(version), version, "QR Code version must be between 1 and 40.");
        return AlignmentCentres[version - 1];
    }

    /// <summary>Returns the Micro QR symbol number (Table 13) identifying <paramref name="microVersion"/>/<paramref name="level"/>, used as the format information's first three data bits.</summary>
    internal static int MicroSymbolNumber(int microVersion, QrErrorCorrection level) => (microVersion, level) switch
    {
        (1, _) => 0,
        (2, QrErrorCorrection.L) => 1,
        (2, QrErrorCorrection.M) => 2,
        (3, QrErrorCorrection.L) => 3,
        (3, QrErrorCorrection.M) => 4,
        (4, QrErrorCorrection.L) => 5,
        (4, QrErrorCorrection.M) => 6,
        (4, QrErrorCorrection.Q) => 7,
        _ => throw new ArgumentException($"Micro QR version M{microVersion} does not support error correction level {level}.", nameof(level)),
    };

    /// <summary>Returns the Micro QR data/error-correction codeword split for <paramref name="microVersion"/>/<paramref name="level"/> (ISO/IEC 18004 Table 7; Micro QR always uses a single block).</summary>
    internal static MicroQrCapacity GetMicroCapacity(int microVersion, QrErrorCorrection level) => (microVersion, level) switch
    {
        (1, _) => new MicroQrCapacity(3, 2, LastCodewordIsHalfWidth: true),
        (2, QrErrorCorrection.L) => new MicroQrCapacity(5, 5, LastCodewordIsHalfWidth: false),
        (2, QrErrorCorrection.M) => new MicroQrCapacity(4, 6, LastCodewordIsHalfWidth: false),
        (3, QrErrorCorrection.L) => new MicroQrCapacity(11, 6, LastCodewordIsHalfWidth: true),
        (3, QrErrorCorrection.M) => new MicroQrCapacity(9, 8, LastCodewordIsHalfWidth: true),
        (4, QrErrorCorrection.L) => new MicroQrCapacity(16, 8, LastCodewordIsHalfWidth: false),
        (4, QrErrorCorrection.M) => new MicroQrCapacity(14, 10, LastCodewordIsHalfWidth: false),
        (4, QrErrorCorrection.Q) => new MicroQrCapacity(10, 14, LastCodewordIsHalfWidth: false),
        _ => throw new ArgumentException($"Micro QR version M{microVersion} does not support error correction level {level}.", nameof(level)),
    };

    // Table 9, one row per version 1-40, four entries per row (L, M, Q, H matching QrErrorCorrection's
    // declaration order) of (ecCodewordsPerBlock, group1Blocks, group1DataCodewords, group2Blocks, group2DataCodewords).
    private static readonly QrEcBlockInfo[][] EcBlockTable =
    [
        Row(26, (7,1,19,0,0), (10,1,16,0,0), (13,1,13,0,0), (17,1,9,0,0)),
        Row(44, (10,1,34,0,0), (16,1,28,0,0), (22,1,22,0,0), (28,1,16,0,0)),
        Row(70, (15,1,55,0,0), (26,1,44,0,0), (18,2,17,0,0), (22,2,13,0,0)),
        Row(100, (20,1,80,0,0), (18,2,32,0,0), (26,2,24,0,0), (16,4,9,0,0)),
        Row(134, (26,1,108,0,0), (24,2,43,0,0), (18,2,15,2,16), (22,2,11,2,12)),
        Row(172, (18,2,68,0,0), (16,4,27,0,0), (24,4,19,0,0), (28,4,15,0,0)),
        Row(196, (20,2,78,0,0), (18,4,31,0,0), (18,2,14,4,15), (26,4,13,1,14)),
        Row(242, (24,2,97,0,0), (22,2,38,2,39), (22,4,18,2,19), (26,4,14,2,15)),
        Row(292, (30,2,116,0,0), (22,3,36,2,37), (20,4,16,4,17), (24,4,12,4,13)),
        Row(346, (18,2,68,2,69), (26,4,43,1,44), (24,6,19,2,20), (28,6,15,2,16)),
        Row(404, (20,4,81,0,0), (30,1,50,4,51), (28,4,22,4,23), (24,3,12,8,13)),
        Row(466, (24,2,92,2,93), (22,6,36,2,37), (26,4,20,6,21), (28,7,14,4,15)),
        Row(532, (26,4,107,0,0), (22,8,37,1,38), (24,8,20,4,21), (22,12,11,4,12)),
        Row(581, (30,3,115,1,116), (24,4,40,5,41), (20,11,16,5,17), (24,11,12,5,13)),
        Row(655, (22,5,87,1,88), (24,5,41,5,42), (30,5,24,7,25), (24,11,12,7,13)),
        Row(733, (24,5,98,1,99), (28,7,45,3,46), (24,15,19,2,20), (30,3,15,13,16)),
        Row(815, (28,1,107,5,108), (28,10,46,1,47), (28,1,22,15,23), (28,2,14,17,15)),
        Row(901, (30,5,120,1,121), (26,9,43,4,44), (28,17,22,1,23), (28,2,14,19,15)),
        Row(991, (28,3,113,4,114), (26,3,44,11,45), (26,17,21,4,22), (26,9,13,16,14)),
        Row(1085, (28,3,107,5,108), (26,3,41,13,42), (30,15,24,5,25), (28,15,15,10,16)),
        Row(1156, (28,4,116,4,117), (26,17,42,0,0), (28,17,22,6,23), (30,19,16,6,17)),
        Row(1258, (28,2,111,7,112), (28,17,46,0,0), (30,7,24,16,25), (24,34,13,0,0)),
        Row(1364, (30,4,121,5,122), (28,4,47,14,48), (30,11,24,14,25), (30,16,15,14,16)),
        Row(1474, (30,6,117,4,118), (28,6,45,14,46), (30,11,24,16,25), (30,30,16,2,17)),
        Row(1588, (26,8,106,4,107), (28,8,47,13,48), (30,7,24,22,25), (30,22,15,13,16)),
        Row(1706, (28,10,114,2,115), (28,19,46,4,47), (28,28,22,6,23), (30,33,16,4,17)),
        Row(1828, (30,8,122,4,123), (28,22,45,3,46), (30,8,23,26,24), (30,12,15,28,16)),
        Row(1921, (30,3,117,10,118), (28,3,45,23,46), (30,4,24,31,25), (30,11,15,31,16)),
        Row(2051, (30,7,116,7,117), (28,21,45,7,46), (30,1,23,37,24), (30,19,15,26,16)),
        Row(2185, (30,5,115,10,116), (28,19,47,10,48), (30,15,24,25,25), (30,23,15,25,16)),
        Row(2323, (30,13,115,3,116), (28,2,46,29,47), (30,42,24,1,25), (30,23,15,28,16)),
        Row(2465, (30,17,115,0,0), (28,10,46,23,47), (30,10,24,35,25), (30,19,15,35,16)),
        Row(2611, (30,17,115,1,116), (28,14,46,21,47), (30,29,24,19,25), (30,11,15,46,16)),
        Row(2761, (30,13,115,6,116), (28,14,46,23,47), (30,44,24,7,25), (30,59,16,1,17)),
        Row(2876, (30,12,121,7,122), (28,12,47,26,48), (30,39,24,14,25), (30,22,15,41,16)),
        Row(3034, (30,6,121,14,122), (28,6,47,34,48), (30,46,24,10,25), (30,2,15,64,16)),
        Row(3196, (30,17,122,4,123), (28,29,46,14,47), (30,49,24,10,25), (30,24,15,46,16)),
        Row(3362, (30,4,122,18,123), (28,13,46,32,47), (30,48,24,14,25), (30,42,15,32,16)),
        Row(3532, (30,20,117,4,118), (28,40,47,7,48), (30,43,24,22,25), (30,10,15,67,16)),
        Row(3706, (30,19,118,6,119), (28,18,47,31,48), (30,34,24,34,25), (30,20,15,61,16)),
    ];

    private static QrEcBlockInfo[] Row(
        int totalCodewords,
        (int Ec, int B1, int D1, int B2, int D2) l,
        (int Ec, int B1, int D1, int B2, int D2) m,
        (int Ec, int B1, int D1, int B2, int D2) q,
        (int Ec, int B1, int D1, int B2, int D2) h) =>
    [
        new QrEcBlockInfo(totalCodewords, l.Ec, l.B1, l.D1, l.B2, l.D2),
        new QrEcBlockInfo(totalCodewords, m.Ec, m.B1, m.D1, m.B2, m.D2),
        new QrEcBlockInfo(totalCodewords, q.Ec, q.B1, q.D1, q.B2, q.D2),
        new QrEcBlockInfo(totalCodewords, h.Ec, h.B1, h.D1, h.B2, h.D2),
    ];

    // Annex E: alignment pattern centre coordinates, one row per version. Version 1 has none.
    private static readonly int[][] AlignmentCentres =
    [
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50],
        [6, 30, 54],
        [6, 32, 58],
        [6, 34, 62],
        [6, 26, 46, 66],
        [6, 26, 48, 70],
        [6, 26, 50, 74],
        [6, 30, 54, 78],
        [6, 30, 56, 82],
        [6, 30, 58, 86],
        [6, 34, 62, 90],
        [6, 28, 50, 72, 94],
        [6, 26, 50, 74, 98],
        [6, 30, 54, 78, 102],
        [6, 28, 54, 80, 106],
        [6, 32, 58, 84, 110],
        [6, 30, 58, 86, 114],
        [6, 34, 62, 90, 118],
        [6, 26, 50, 74, 98, 122],
        [6, 30, 54, 78, 102, 126],
        [6, 26, 52, 78, 104, 130],
        [6, 30, 56, 82, 108, 134],
        [6, 34, 60, 86, 112, 138],
        [6, 30, 58, 86, 114, 142],
        [6, 34, 62, 90, 118, 146],
        [6, 30, 54, 78, 102, 126, 150],
        [6, 24, 50, 76, 102, 128, 154],
        [6, 28, 54, 80, 106, 132, 158],
        [6, 32, 58, 84, 110, 136, 162],
        [6, 26, 54, 82, 110, 138, 166],
        [6, 30, 58, 86, 114, 142, 170],
    ];
}
