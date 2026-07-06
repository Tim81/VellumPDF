// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.EanUpc;

/// <summary>
/// The EAN/UPC digit-encoding tables (GS1 General Specifications §5.2; Wikipedia
/// International Article Number / EAN-5 / EAN-2). Each digit pattern is a 7-character string
/// of module bits ('1' = dark, '0' = light), always two bars and two spaces.
/// </summary>
internal static class EanTables
{
    /// <summary>Odd-parity ("L") patterns for digits 0-9, used for the left group of EAN-13/UPC-A/EAN-8 and left-hand EAN-5/EAN-2 digits.</summary>
    internal static readonly string[] L =
    [
        "0001101", "0011001", "0010011", "0111101", "0100011",
        "0110001", "0101111", "0111011", "0110111", "0001011",
    ];

    /// <summary>Even-parity ("G") patterns for digits 0-9 (the bitwise reverse of the R patterns), used where the parity table calls for G.</summary>
    internal static readonly string[] G =
    [
        "0100111", "0110011", "0011011", "0100001", "0011101",
        "0111001", "0000101", "0010001", "0001001", "0010111",
    ];

    /// <summary>Even-parity ("R") patterns for digits 0-9 (the bitwise complement of the L patterns), used for the right group of EAN-13/UPC-A/EAN-8.</summary>
    internal static readonly string[] R =
    [
        "1110010", "1100110", "1101100", "1000010", "1011100",
        "1001110", "1010000", "1000100", "1001000", "1110100",
    ];

    /// <summary>
    /// For each possible EAN-13 first digit (index), the L/G parity pattern applied to the
    /// following six digits of the left group. All-'L' (index 0) is also the pattern EAN-8 and
    /// UPC-A use for their own left group, since both are the "first digit is zero" case.
    /// </summary>
    internal static readonly string[] Ean13FirstDigitParity =
    [
        "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG",
        "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL",
    ];

    /// <summary>
    /// For each EAN-5 checksum value (weighted 3,9,3,9,3 mod 10; see
    /// <see cref="EanEncoder.ComputeEan5Checksum"/>), the L/G parity pattern applied to the five
    /// add-on digits.
    /// </summary>
    internal static readonly string[] Ean5Parity =
    [
        "GGLLL", "GLGLL", "GLLGL", "GLLLG", "LGGLL",
        "LLGGL", "LLLGG", "LGLGL", "LGLLG", "LLGLG",
    ];

    /// <summary>For each EAN-2 value-mod-4 result, the L/G parity pattern applied to the two add-on digits.</summary>
    internal static readonly string[] Ean2Parity = ["LL", "LG", "GL", "GG"];
}
