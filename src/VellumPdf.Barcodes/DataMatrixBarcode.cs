// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.DataMatrix;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes;

/// <summary>
/// A Data Matrix symbol (ISO/IEC 16022, ECC 200): a square or rectangular matrix symbology with
/// automatic symbol-size selection across the 24 square and 6 rectangular ECC 200 sizes. Content
/// is compacted automatically across ASCII, C40, Text and Base 256 encodation, following the
/// specification's mode-switching heuristics. X12 and EDIFACT encodation are not supported (every
/// ASCII-representable byte remains reachable through ASCII, C40 or Text, so this only costs a
/// little density on content those modes would favour, never correctness).
///
/// <para>
/// Forcing one exact size among the 24/6 (rather than just biasing the family via
/// <see cref="Shape"/>) is deferred to a future release, to keep this type's stable surface tight
/// while the encoder is new; <see cref="Shape"/> and the automatic sizing already cover the common
/// cases.
/// </para>
/// </summary>
public sealed class DataMatrixBarcode : Barcode
{
    private Encoded2D? _encoded;

    /// <summary>Creates a Data Matrix symbol from text, compacted automatically across ASCII, C40, Text and Base 256.</summary>
    /// <param name="content">The text to encode. Must be representable in ISO/IEC 8859-1 (Latin-1).</param>
    public DataMatrixBarcode(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Text = content;
    }

    /// <summary>Creates a Data Matrix symbol carrying raw bytes verbatim in Base 256 mode.</summary>
    /// <param name="content">The bytes to encode.</param>
    public DataMatrixBarcode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Bytes = content;
    }

    /// <summary>
    /// When <c>true</c>, this is a GS1 Data Matrix symbol: FNC1 (codeword 232) is emitted in the
    /// first data-codeword position, and — mirroring <see cref="Code128Barcode.Gs1"/> — any
    /// U+001D (group separator) elsewhere in the content also becomes FNC1 rather than its literal
    /// value. Defaults to <c>false</c>.
    /// </summary>
    public bool Gs1 { get; init; }

    /// <summary>Biases automatic symbol-size selection toward a square or rectangular layout. Defaults to <see cref="DataMatrixShape.Automatic"/>.</summary>
    public DataMatrixShape Shape { get; init; } = DataMatrixShape.Automatic;

    internal string? Text { get; }

    internal byte[]? Bytes { get; }

    /// <summary>Encodes and returns the symbol's module grid, caching the result on first use.</summary>
    /// <exception cref="ArgumentException">Both <see cref="Barcode.ModuleSize"/> and <see cref="Barcode.TargetWidth"/> are set.</exception>
    /// <exception cref="FormatException">
    /// The content is not representable in ISO/IEC 8859-1 (string constructor only), or needs more
    /// data codewords than the largest symbol in the requested <see cref="Shape"/> provides.
    /// </exception>
    public BarcodeMatrix GetMatrix() => GetEncoded().Matrix;

    private protected override BarcodeSize MeasureCore() => BarcodeGeometry.Measure2D(this, GetEncoded());

    internal override Encoded2D? GetEncoded2D() => GetEncoded();

    private Encoded2D GetEncoded()
    {
        if (_encoded is not null) return _encoded;

        var matrix = DataMatrixEncoder.Encode(this);
        return _encoded = new Encoded2D { Matrix = matrix, QuietZoneModules = 1, RowHeightModules = 1 };
    }
}
