// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Code39;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes;

/// <summary>
/// A Code 39 barcode (ISO/IEC 16388), the self-checking 43-character symbology long used in
/// logistics, defense (LOGMARS) and healthcare (HIBC) item marking. An optional Extended (Full
/// ASCII) mode reaches the full 128-character ASCII range through two-character shift pairs.
/// </summary>
public sealed class Code39Barcode : Barcode1D
{
    private Encoded1D? _encoded;

    /// <summary>
    /// Creates a Code 39 barcode from its content. With <see cref="FullAscii"/> unset (the
    /// default), <paramref name="content"/> must be drawn from the 43-character standard set
    /// (0-9, A-Z, space, and <c>-.$/+%</c>) — validated when the barcode is measured or drawn,
    /// since character-set validity depends on <see cref="FullAscii"/>. With <see cref="FullAscii"/>
    /// set, any ASCII (0-127) character is accepted, each expanded to its Extended Code 39
    /// shift-pair representation.
    /// </summary>
    /// <param name="content">The text to encode, as described above.</param>
    public Code39Barcode(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    /// <summary>The content as supplied to the constructor — the human-readable text, not the encoded symbol sequence.</summary>
    public string Content { get; }

    /// <summary>Appends a modulo-43 check character before the stop character. Defaults to <c>false</c>.</summary>
    public bool CheckDigit { get; init; }

    /// <summary>
    /// Extended (Full ASCII) mode: <see cref="Content"/> may use any ASCII character (0-127),
    /// each mapped to its one- or two-character Code 39 representation (AIM USS-39 precedence
    /// codes <c>$</c>, <c>/</c>, <c>%</c> and <c>+</c>). Defaults to <c>false</c> (standard
    /// 43-character set only).
    /// </summary>
    public bool FullAscii { get; init; }

    /// <summary>
    /// The ratio of a wide element's width to a narrow element's width. Defaults to 2.5;
    /// ISO/IEC 16388 requires a value between 2.0 and 3.0.
    /// </summary>
    public double WideNarrowRatio { get; init; } = 2.5;

    private protected override Encoded1D GetEncoded() => _encoded ??= Code39Encoder.Encode(this);
}
