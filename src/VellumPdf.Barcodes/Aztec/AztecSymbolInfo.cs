// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Aztec;

/// <summary>
/// One Aztec Code symbol size's attributes (ISO/IEC 24778 Table 1, "The size and capacities of
/// Aztec Code symbols"): its overall dimensions, total codeword capacity, codeword bit width, and
/// the Galois field its Reed-Solomon error correction runs over.
/// </summary>
/// <param name="IsCompact">Whether this is one of the four compact sizes (1-4 layers, 11x11 core) rather than a full-range size (1-32 layers, 15x15 core).</param>
/// <param name="Layers">The number of data layers (1-4 compact, 1-32 full-range).</param>
/// <param name="Size">The overall symbol width and height, in modules.</param>
/// <param name="CodewordCount">The symbol's total codeword capacity <c>Cw</c> (data codewords plus error-correction codewords).</param>
/// <param name="WordBits">The number of bits per codeword: 6 (layers 1-2), 8 (3-8), 10 (9-22) or 12 (23-32), by layer count regardless of compact/full-range.</param>
/// <param name="Field">The Galois field <see cref="WordBits"/>-bit codewords are Reed-Solomon-protected over.</param>
internal readonly record struct AztecSymbolSize(bool IsCompact, int Layers, int Size, int CodewordCount, int WordBits, GaloisField Field)
{
    /// <summary>The core's half-width in modules from the symbol's centre: 5 for compact (11x11 core), 7 for full-range (15x15 core).</summary>
    internal int CoreOuterRadius => IsCompact ? 5 : 7;

    /// <summary>The symbol's half-width in modules from its centre: always <c>(Size - 1) / 2</c>, since every Aztec Code symbol size is odd.</summary>
    internal int OuterRadius => (Size - 1) / 2;
}

/// <summary>
/// The Aztec Code symbol-size table (ISO/IEC 24778 Table 1): 4 compact sizes (15x15 to 27x27,
/// 1-4 layers) and 32 full-range sizes (19x19 to 151x151, 1-32 layers). Every dimension below is
/// transcribed from that table and cross-checked internally: <see cref="AztecSymbolSize.CodewordCount"/>
/// times <see cref="AztecSymbolSize.WordBits"/> reproduces the table's separately-listed total bit
/// capacity for every row, and each size's data-field spiral (<see cref="AztecPlacement"/>) holds at
/// least that many module positions — a few sizes (every 1-layer symbol, and some full-range layer
/// counts) carry a handful of spare cells the codewords do not fill, left blank at the spiral's
/// outer head.
/// </summary>
internal static class AztecSymbolInfo
{
    /// <summary>The 4 compact sizes, ascending by layer count.</summary>
    internal static readonly AztecSymbolSize[] Compact =
    [
        new(true, 1, 15, 17, 6, GaloisField.Gf64),
        new(true, 2, 19, 40, 6, GaloisField.Gf64),
        new(true, 3, 23, 51, 8, GaloisField.Gf256),
        new(true, 4, 27, 76, 8, GaloisField.Gf256),
    ];

    /// <summary>The 32 full-range sizes, ascending by layer count.</summary>
    internal static readonly AztecSymbolSize[] FullRange =
    [
        new(false, 1, 19, 21, 6, GaloisField.Gf64),
        new(false, 2, 23, 48, 6, GaloisField.Gf64),
        new(false, 3, 27, 60, 8, GaloisField.Gf256),
        new(false, 4, 31, 88, 8, GaloisField.Gf256),
        new(false, 5, 37, 120, 8, GaloisField.Gf256),
        new(false, 6, 41, 156, 8, GaloisField.Gf256),
        new(false, 7, 45, 196, 8, GaloisField.Gf256),
        new(false, 8, 49, 240, 8, GaloisField.Gf256),
        new(false, 9, 53, 230, 10, GaloisField.Gf1024),
        new(false, 10, 57, 272, 10, GaloisField.Gf1024),
        new(false, 11, 61, 316, 10, GaloisField.Gf1024),
        new(false, 12, 67, 364, 10, GaloisField.Gf1024),
        new(false, 13, 71, 416, 10, GaloisField.Gf1024),
        new(false, 14, 75, 470, 10, GaloisField.Gf1024),
        new(false, 15, 79, 528, 10, GaloisField.Gf1024),
        new(false, 16, 83, 588, 10, GaloisField.Gf1024),
        new(false, 17, 87, 652, 10, GaloisField.Gf1024),
        new(false, 18, 91, 720, 10, GaloisField.Gf1024),
        new(false, 19, 95, 790, 10, GaloisField.Gf1024),
        new(false, 20, 101, 864, 10, GaloisField.Gf1024),
        new(false, 21, 105, 940, 10, GaloisField.Gf1024),
        new(false, 22, 109, 1020, 10, GaloisField.Gf1024),
        new(false, 23, 113, 920, 12, GaloisField.Gf4096),
        new(false, 24, 117, 992, 12, GaloisField.Gf4096),
        new(false, 25, 121, 1066, 12, GaloisField.Gf4096),
        new(false, 26, 125, 1144, 12, GaloisField.Gf4096),
        new(false, 27, 131, 1224, 12, GaloisField.Gf4096),
        new(false, 28, 135, 1306, 12, GaloisField.Gf4096),
        new(false, 29, 139, 1392, 12, GaloisField.Gf4096),
        new(false, 30, 143, 1480, 12, GaloisField.Gf4096),
        new(false, 31, 147, 1570, 12, GaloisField.Gf4096),
        new(false, 32, 151, 1664, 12, GaloisField.Gf4096),
    ];
}
