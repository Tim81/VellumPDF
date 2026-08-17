// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Images;

/// <summary>
/// The <c>/CCITTFaxDecode</c> decode parameters for a CCITT image (ISO 32000-1 Table 11), as passed
/// to <see cref="CcittImageLoader"/>.
/// </summary>
/// <remarks>
/// A record rather than four optional parameters on <c>Load</c>. Optional parameters are the hardest
/// part of an API to evolve once it is locked — adding one is a binary-compatibility break for
/// existing call sites, and they could not be combined with an
/// <see cref="ImageLoadOptions"/> argument at all, because the overload that accepted those could
/// not carry them.
/// </remarks>
public sealed record CcittOptions
{
    /// <summary>
    /// The <c>K</c> value: negative selects Group 4 (T.6 MMR), zero Group 3 1-D, positive Group 3
    /// mixed 1-D/2-D. Defaults to <c>-1</c> (Group 4), which is what fax-derived TIFF data usually
    /// carries.
    /// </summary>
    public int K { get; init; } = -1;

    /// <summary>
    /// Whether a 1 bit means black. Defaults to <see langword="false"/>, matching the PDF default
    /// where 0 is black.
    /// </summary>
    public bool BlackIs1 { get; init; }

    /// <summary>
    /// Whether each encoded line is padded to a byte boundary. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool EncodedByteAlign { get; init; }

    /// <summary>
    /// Whether encoded lines carry end-of-line bit patterns. Defaults to <see langword="false"/>.
    /// </summary>
    public bool EndOfLine { get; init; }

    /// <summary>The defaults: Group 4, 0-is-black, no byte alignment, no end-of-line patterns.</summary>
    public static CcittOptions Default { get; } = new();
}
