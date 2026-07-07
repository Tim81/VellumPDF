// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>
/// Biases automatic Data Matrix symbol-size selection (ISO/IEC 16022 ECC 200 defines 24 square
/// and 6 rectangular sizes) toward a square or rectangular layout. Does not force one specific
/// size among the 24/6 — only which family the automatic sizing picks from. Forcing an exact
/// size (e.g. "16x48") is deferred to a future release; see <see cref="DataMatrixBarcode.Shape"/>.
/// </summary>
public enum DataMatrixShape
{
    /// <summary>Picks the smallest fitting square size — matching most generators' default and the well-known worked examples. The default.</summary>
    Automatic = 0,

    /// <summary>Picks the smallest fitting square size (10x10 through 144x144).</summary>
    Square = 1,

    /// <summary>Picks the smallest fitting rectangular size (8x18 through 16x48).</summary>
    Rectangular = 2,
}
