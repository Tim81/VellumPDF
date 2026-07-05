// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;
using VellumPdf.Barcodes.Pdf417;

namespace VellumPdf.Barcodes;

/// <summary>
/// A PDF417 symbol (ISO/IEC 15438): a stacked linear barcode with 3-90 rows of 1-30 data columns,
/// chosen automatically to match <see cref="PreferredAspectRatio"/> unless <see cref="Columns"/>
/// or <see cref="Rows"/> is set. Content is compacted automatically across text, byte and numeric
/// modes following the specification's mode-switching heuristics. Macro PDF417 (splitting content
/// across several symbols) is not supported.
/// </summary>
public sealed class Pdf417Barcode : Barcode
{
    private Encoded2D? _encoded;

    /// <summary>Creates a PDF417 symbol from text, compacted automatically across text, byte and numeric modes.</summary>
    /// <param name="content">The text to encode. Must be representable in ISO/IEC 8859-1 (Latin-1).</param>
    public Pdf417Barcode(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Text = content;
    }

    /// <summary>Creates a PDF417 symbol carrying raw bytes verbatim in byte compaction mode.</summary>
    /// <param name="content">The bytes to encode.</param>
    public Pdf417Barcode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Bytes = content;
    }

    /// <summary>
    /// The error-correction level (0-8; each level doubles the number of error-correction
    /// codewords, from 2 at level 0 to 512 at level 8). The default, -1, picks the level
    /// ISO/IEC 15438 recommends for the content's size.
    /// </summary>
    public int ErrorCorrectionLevel { get; init; } = -1;

    /// <summary>Forces the number of data columns (1-30) instead of solving it from <see cref="PreferredAspectRatio"/>.</summary>
    public int? Columns { get; init; }

    /// <summary>Forces the number of rows (3-90) instead of solving it from <see cref="PreferredAspectRatio"/>.</summary>
    public int? Rows { get; init; }

    /// <summary>The width-to-height ratio the automatic column/row solver aims for when neither <see cref="Columns"/> nor <see cref="Rows"/> is set. Defaults to 3.0.</summary>
    public double PreferredAspectRatio { get; init; } = 3.0;

    /// <summary>The height of each row, in modules. Defaults to 3.0, the specification's recommended minimum.</summary>
    public double RowHeight { get; init; } = 3.0;

    internal string? Text { get; }

    internal byte[]? Bytes { get; }

    /// <summary>Encodes and returns the symbol's module grid, caching the result on first use. Each row of the grid is one PDF417 row; the painter stretches it to <see cref="RowHeight"/> modules tall.</summary>
    /// <exception cref="ArgumentException"><see cref="ErrorCorrectionLevel"/>, <see cref="Columns"/> or <see cref="Rows"/> is outside its valid range, <see cref="RowHeight"/> is less than 3, <see cref="PreferredAspectRatio"/> is not a positive finite number, or both <see cref="Barcode.ModuleSize"/> and <see cref="Barcode.TargetWidth"/> are set.</exception>
    /// <exception cref="FormatException">The content is not representable in ISO/IEC 8859-1, or does not fit within 928 codewords (or the forced <see cref="Columns"/>/<see cref="Rows"/>) at <see cref="ErrorCorrectionLevel"/>.</exception>
    public BarcodeMatrix GetMatrix() => GetEncoded().Matrix;

    private protected override BarcodeSize MeasureCore() => BarcodeGeometry.Measure2D(this, GetEncoded());

    private Encoded2D GetEncoded()
    {
        if (_encoded is not null) return _encoded;

        if (ErrorCorrectionLevel != -1 && ErrorCorrectionLevel is < 0 or > 8)
            throw new ArgumentException($"ErrorCorrectionLevel must be -1 or between 0 and 8 (was {ErrorCorrectionLevel}).", nameof(ErrorCorrectionLevel));
        if (Columns is { } columns && columns is < 1 or > 30)
            throw new ArgumentException($"Columns must be between 1 and 30 (was {columns}).", nameof(Columns));
        if (Rows is { } rows && rows is < 3 or > 90)
            throw new ArgumentException($"Rows must be between 3 and 90 (was {rows}).", nameof(Rows));
        if (!double.IsFinite(RowHeight) || RowHeight < 3)
            throw new ArgumentException($"RowHeight must be a finite number of at least 3 (was {RowHeight}).", nameof(RowHeight));
        if (!double.IsFinite(PreferredAspectRatio) || PreferredAspectRatio <= 0)
            throw new ArgumentException($"PreferredAspectRatio must be a positive finite number (was {PreferredAspectRatio}).", nameof(PreferredAspectRatio));

        var matrix = Pdf417Encoder.Encode(this);
        return _encoded = new Encoded2D { Matrix = matrix, QuietZoneModules = 2, RowHeightModules = RowHeight };
    }
}
