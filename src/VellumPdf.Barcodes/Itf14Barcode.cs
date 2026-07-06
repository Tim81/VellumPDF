// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;
using VellumPdf.Barcodes.Itf;

namespace VellumPdf.Barcodes;

/// <summary>
/// An ITF-14 (GTIN-14, Interleaved 2-of-5) barcode, typically used on cartons and pallets
/// (GS1 General Specifications §5.3).
/// </summary>
public sealed class Itf14Barcode : Barcode1D
{
    private Encoded1D? _encoded;

    /// <summary>
    /// Creates an ITF-14 barcode from either the 13 data digits (the check digit is computed)
    /// or all 14 digits (the check digit is validated).
    /// </summary>
    /// <param name="digits">13 or 14 ASCII digits, as described above.</param>
    /// <exception cref="ArgumentException"><paramref name="digits"/> has the wrong length or contains a non-digit character.</exception>
    /// <exception cref="FormatException">A supplied check digit does not match the computed one.</exception>
    public Itf14Barcode(string digits) => Digits = Itf14Encoder.NormalizeAndValidate(digits);

    /// <summary>The canonical 14-digit string, including the check digit.</summary>
    public string Digits { get; }

    /// <summary>The bearer-bar style framing the symbol. Defaults to <see cref="ItfBearerBarStyle.Frame"/>.</summary>
    public ItfBearerBarStyle BearerBars { get; init; } = ItfBearerBarStyle.Frame;

    /// <summary>
    /// The ratio of a wide element's width to a narrow element's width. Defaults to 2.5; the
    /// GS1 General Specifications require a value between 2.25 and 3.0.
    /// </summary>
    public double WideNarrowRatio { get; init; } = 2.5;

    private protected override Encoded1D GetEncoded() => _encoded ??= Itf14Encoder.Encode(this);
}
