// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Code128;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes;

/// <summary>
/// A Code 128 barcode (ISO/IEC 15417), automatically choosing between subsets A, B and C, with
/// an optional GS1-128 mode for application-identifier data.
/// </summary>
public sealed class Code128Barcode : Barcode1D
{
    private Encoded1D? _encoded;

    /// <summary>Creates a Code 128 barcode from its Latin-1 content.</summary>
    /// <param name="content">
    /// The text to encode. Must be Latin-1 (code points 0-255); characters above 127 are carried
    /// with FNC4 (ISO/IEC 15417). See <see cref="Gs1"/> for a GS1-128 restriction on this range.
    /// </param>
    /// <exception cref="ArgumentException">A character in <paramref name="content"/> falls outside 0-255.</exception>
    public Code128Barcode(string content) => Content = Code128Encoder.Validate(content);

    /// <summary>The encoded content.</summary>
    public string Content { get; }

    /// <summary>
    /// When <c>true</c>, this is a GS1-128 symbol: FNC1 is emitted immediately after the start
    /// code (the GS1-128 application-identifier marker), and any U+001D (group separator) in
    /// <see cref="Content"/> becomes FNC1 rather than a literal control character. Defaults to
    /// <c>false</c>. The GS1 General Specifications do not permit FNC4 in a GS1-128 symbol, so
    /// <see cref="Content"/> may not contain a character above 127 while this is <c>true</c>.
    /// </summary>
    public bool Gs1 { get; init; }

    private protected override Encoded1D GetEncoded() => _encoded ??= Code128Encoder.Encode(this);
}
