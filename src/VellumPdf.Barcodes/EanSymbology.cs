// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>The EAN/UPC family symbology an <see cref="EanBarcode"/> encodes as.</summary>
public enum EanSymbology
{
    /// <summary>EAN-13: 12 data digits plus a check digit, 95 modules.</summary>
    Ean13,

    /// <summary>EAN-8: 7 data digits plus a check digit, 67 modules.</summary>
    Ean8,

    /// <summary>UPC-A: 11 data digits plus a check digit; a 0-prefixed EAN-13, 95 modules.</summary>
    UpcA,

    /// <summary>
    /// UPC-E: the zero-suppressed 6-digit form of a UPC-A, valid only for number system 0 or 1.
    /// 51 modules — the narrowest symbol in the family.
    /// </summary>
    UpcE,
}
