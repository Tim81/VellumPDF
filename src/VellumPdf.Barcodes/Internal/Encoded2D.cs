// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// The result of encoding a matrix (2D) barcode: the module grid plus the metadata the shared
/// geometry calculator and painter need. Produced by <c>QrEncoder</c>, <c>MicroQrEncoder</c> and
/// <c>Pdf417Encoder</c>; <see cref="BarcodeGeometry"/> and <see cref="BarcodePainter"/> share this
/// one model across all three, alongside <see cref="Encoded1D"/> for the linear symbologies.
/// </summary>
internal sealed class Encoded2D
{
    /// <summary>The dark/light module grid.</summary>
    public required BarcodeMatrix Matrix { get; init; }

    /// <summary>The quiet zone around all four sides, in modules.</summary>
    public required double QuietZoneModules { get; init; }

    /// <summary>
    /// The height of each row, in modules. 1 for square-module symbologies (QR, Micro QR);
    /// PDF417 rows are typically taller than they are wide.
    /// </summary>
    public required double RowHeightModules { get; init; }
}
