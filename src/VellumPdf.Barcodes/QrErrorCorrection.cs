// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>
/// The error-correction level of a <see cref="QrCode"/>, trading symbol capacity for resilience to
/// damage or misreads (ISO/IEC 18004 Table 12). Higher levels tolerate more codeword errors but
/// need more error-correction codewords for the same data, so they produce a larger symbol at a
/// given version.
/// </summary>
public enum QrErrorCorrection
{
    /// <summary>Recovers approximately 7% of codewords.</summary>
    L,

    /// <summary>Recovers approximately 15% of codewords.</summary>
    M,

    /// <summary>Recovers approximately 25% of codewords.</summary>
    Q,

    /// <summary>Recovers approximately 30% of codewords.</summary>
    H,
}
