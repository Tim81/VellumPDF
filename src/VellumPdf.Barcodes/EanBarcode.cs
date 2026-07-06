// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.EanUpc;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes;

/// <summary>
/// An EAN-13, EAN-8, UPC-A or UPC-E barcode, optionally followed by an EAN-2 or EAN-5 add-on
/// (GS1 General Specifications §5.2).
/// </summary>
public sealed class EanBarcode : Barcode1D
{
    private Encoded1D? _encoded;

    /// <summary>
    /// Creates an EAN/UPC barcode. EAN-13 accepts 12 data digits (the check digit is computed)
    /// or 13 (validated); EAN-8 accepts 7 or 8; UPC-A accepts 11 or 12. <see cref="EanSymbology.UpcE"/>
    /// accepts four forms: 6 digits (the compressed data alone; number system 0 is assumed), 7
    /// digits (a leading number-system digit — 0 or 1 — plus the 6 compressed digits), 8 digits
    /// (as 7, plus a check digit that is validated), or an 11/12-digit UPC-A number that
    /// compresses to a UPC-E symbol — UPC-E has no check digit of its own; it is always derived
    /// from the expanded UPC-A.
    /// </summary>
    /// <param name="symbology">Which EAN/UPC symbology to encode as.</param>
    /// <param name="digits">The digits to encode, as described above.</param>
    /// <exception cref="ArgumentException"><paramref name="digits"/> has an unsupported length or contains a non-digit character.</exception>
    /// <exception cref="FormatException">
    /// A supplied check digit does not match the computed one, or (for <see cref="EanSymbology.UpcE"/>)
    /// the number system is not 0 or 1, or the value cannot be represented as UPC-E.
    /// </exception>
    public EanBarcode(EanSymbology symbology, string digits)
    {
        Symbology = symbology;
        Digits = symbology == EanSymbology.UpcE
            ? EanEncoder.NormalizeAndValidateUpcE(digits)
            : EanEncoder.NormalizeAndValidate(symbology, digits);
    }

    /// <summary>The symbology this barcode encodes as.</summary>
    public EanSymbology Symbology { get; }

    /// <summary>
    /// The canonical digit string, including the check digit: 13 digits for EAN-13, 8 for EAN-8,
    /// 12 for UPC-A, or 8 for UPC-E (number system, 6 compressed digits, check digit).
    /// </summary>
    public string Digits { get; }

    /// <summary>
    /// An optional 2- or 5-digit EAN add-on symbol, printed above the main symbol with its own
    /// 9-module gap. <c>null</c> (the default) omits the add-on.
    /// </summary>
    public string? AddOn { get; init; }

    private protected override Encoded1D GetEncoded() => _encoded ??= EanEncoder.Encode(this);
}
