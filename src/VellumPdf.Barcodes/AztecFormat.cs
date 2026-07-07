// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>
/// Biases automatic Aztec Code symbol selection (ISO/IEC 24778 defines 4 compact sizes, 1-4
/// layers, and 32 full-range sizes, 1-32 layers) toward the compact or full-range family. Does not
/// force one specific layer count within a family; see <see cref="AztecCode.Format"/>.
/// </summary>
public enum AztecFormat
{
    /// <summary>Picks the smallest fitting compact size; falls back to full-range only when the content does not fit any compact size. The default.</summary>
    Automatic = 0,

    /// <summary>Picks the smallest fitting compact size (1-4 layers, 15x15 to 27x27).</summary>
    Compact = 1,

    /// <summary>Picks the smallest fitting full-range size (1-32 layers, 19x19 to 151x151).</summary>
    FullRange = 2,
}
