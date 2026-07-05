// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.EanUpc;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes;

/// <summary>
/// An EAN-13, EAN-8 or UPC-A barcode, optionally followed by an EAN-2 or EAN-5 add-on
/// (GS1 General Specifications §5.2).
/// </summary>
public sealed class EanBarcode : Barcode1D
{
    private Encoded1D? _encoded;

    /// <summary>
    /// Creates an EAN/UPC barcode from either the data digits alone (the check digit is
    /// computed) or the data digits plus a check digit (which is validated). EAN-13 accepts 12
    /// or 13 digits, EAN-8 accepts 7 or 8, and UPC-A accepts 11 or 12.
    /// </summary>
    /// <param name="symbology">Which EAN/UPC symbology to encode as.</param>
    /// <param name="digits">The digits to encode, as described above.</param>
    /// <exception cref="ArgumentException"><paramref name="digits"/> has the wrong length or contains a non-digit character.</exception>
    /// <exception cref="FormatException">A supplied check digit does not match the computed one.</exception>
    public EanBarcode(EanSymbology symbology, string digits)
    {
        Symbology = symbology;
        Digits = EanEncoder.NormalizeAndValidate(symbology, digits);
    }

    /// <summary>The symbology this barcode encodes as.</summary>
    public EanSymbology Symbology { get; }

    /// <summary>The canonical digit string, including the check digit (13 digits for EAN-13, 8 for EAN-8, 12 for UPC-A).</summary>
    public string Digits { get; }

    /// <summary>
    /// An optional 2- or 5-digit EAN add-on symbol, printed above the main symbol with its own
    /// 9-module gap. <c>null</c> (the default) omits the add-on.
    /// </summary>
    public string? AddOn { get; init; }

    private protected override Encoded1D GetEncoded() => _encoded ??= EanEncoder.Encode(this);
}
