// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>Which sides of an <see cref="Itf14Barcode"/> carry a bearer bar.</summary>
public enum ItfBearerBarStyle
{
    /// <summary>A full rectangular frame around the symbol and its quiet zones (the GS1 default).</summary>
    Frame,

    /// <summary>A bar above and below the symbol only, with no vertical sides.</summary>
    Horizontal,

    /// <summary>No bearer bar.</summary>
    None,
}
