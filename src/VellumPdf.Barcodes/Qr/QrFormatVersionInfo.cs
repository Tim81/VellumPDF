// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Qr;

/// <summary>
/// Computes the 15-bit format information and 18-bit version information fields (ISO/IEC 18004
/// Annexes C and D): a Bose-Chaudhuri-Hocquenghem remainder appended to a handful of data bits,
/// then (for format information only) XORed with a fixed mask so no combination of level/mask or
/// symbol number/mask produces an all-zero field.
/// </summary>
internal static class QrFormatVersionInfo
{
    // Annex C: G(x) = x^10 + x^8 + x^5 + x^4 + x^2 + x + 1.
    private const int FormatGeneratorPolynomial = 0b101_0011_0111;
    private const int FormatGeneratorDegree = 10;

    // Annex C: masks applied after the BCH remainder is appended, so the field is never all zero.
    private const int FormatMaskQr = 0b101_0100_0001_0010;
    private const int FormatMaskMicro = 0b100_0100_0100_0101;

    // Annex D: G(x) = x^12 + x^11 + x^10 + x^9 + x^8 + x^5 + x^2 + 1.
    private const int VersionGeneratorPolynomial = 0b1_1111_0010_0101;
    private const int VersionGeneratorDegree = 12;

    /// <summary>The QR Code error-correction level indicator bits (ISO/IEC 18004 Table 12).</summary>
    internal static int QrErrorCorrectionIndicator(QrErrorCorrection level) => level switch
    {
        QrErrorCorrection.L => 0b01,
        QrErrorCorrection.M => 0b00,
        QrErrorCorrection.Q => 0b11,
        QrErrorCorrection.H => 0b10,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    /// <summary>Computes the 15-bit masked format information for a full-size QR Code symbol (§7.9.1).</summary>
    internal static int ComputeQrFormatBits(QrErrorCorrection level, int maskId)
    {
        var data = (QrErrorCorrectionIndicator(level) << 3) | maskId;
        return AppendBch(data, 5, FormatGeneratorPolynomial, FormatGeneratorDegree) ^ FormatMaskQr;
    }

    /// <summary>Computes the 15-bit masked format information for a Micro QR symbol (§7.9.2), from its Table 13 symbol number and 2-bit data mask pattern reference.</summary>
    internal static int ComputeMicroFormatBits(int symbolNumber, int maskReference)
    {
        var data = (symbolNumber << 2) | maskReference;
        return AppendBch(data, 5, FormatGeneratorPolynomial, FormatGeneratorDegree) ^ FormatMaskMicro;
    }

    /// <summary>Computes the 18-bit (unmasked) version information for QR Code <paramref name="version"/> 7-40 (§7.10).</summary>
    internal static int ComputeVersionBits(int version) => AppendBch(version, 6, VersionGeneratorPolynomial, VersionGeneratorDegree);

    /// <summary>
    /// Appends the BCH remainder of dividing <paramref name="data"/> (shifted left by
    /// <paramref name="generatorDegree"/>) by <paramref name="generatorPolynomial"/> under GF(2)
    /// (bitwise XOR, no carries) to <paramref name="data"/> itself, returning the combined field.
    /// </summary>
    private static int AppendBch(int data, int dataBits, int generatorPolynomial, int generatorDegree)
    {
        var remainder = data << generatorDegree;
        for (var bit = dataBits + generatorDegree - 1; bit >= generatorDegree; bit--)
        {
            if (((remainder >> bit) & 1) != 0)
                remainder ^= generatorPolynomial << (bit - generatorDegree);
        }

        return (data << generatorDegree) | remainder;
    }
}
