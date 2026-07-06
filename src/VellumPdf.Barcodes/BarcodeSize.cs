// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>The measured footprint of a barcode, in points.</summary>
/// <param name="Width">The total width, including quiet zones and any HRI/bearer contribution.</param>
/// <param name="Height">The total height, including quiet zones and any HRI/bearer contribution.</param>
public readonly record struct BarcodeSize(double Width, double Height);
