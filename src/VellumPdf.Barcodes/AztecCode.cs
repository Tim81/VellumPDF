// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Aztec;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes;

/// <summary>
/// An Aztec Code symbol (ISO/IEC 24778): a square matrix symbology with a central bullseye finder
/// pattern and no quiet zone requirement, in 4 compact sizes (1-4 layers) and 32 full-range sizes
/// (1-32 layers). Content is compacted automatically across the five Upper/Lower/Mixed/Punct/Digit
/// character modes, with binary shift for bytes none of them reach directly. Symbol size and
/// error-correction split are chosen automatically from the content and <see cref="ErrorCorrectionPercent"/>.
///
/// <para>
/// GS1 element strings are not supported by this release (unlike <see cref="QrCode.Gs1"/> and
/// <see cref="DataMatrixBarcode.Gs1"/>); forcing an exact layer count, rather than just biasing the
/// family via <see cref="Format"/>, is likewise deferred to a future release.
/// </para>
///
/// <para>
/// Symbols round-trip through external readers across every compact and full-range size: the
/// fixed patterns, mode message and data-field spiral are all verified against a real decoder
/// (zxing-cpp) — see <c>VellumPdf.Barcodes.Aztec.AztecPlacement</c>'s remarks.
/// </para>
/// </summary>
public sealed class AztecCode : Barcode
{
    private Encoded2D? _encoded;

    /// <summary>Creates an Aztec Code symbol from text, compacted automatically across the five character modes.</summary>
    /// <param name="content">The text to encode. Must be representable in ISO/IEC 8859-1 (Latin-1).</param>
    /// <exception cref="ArgumentException"><paramref name="content"/> is empty.</exception>
    public AztecCode(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        Text = content;
    }

    /// <summary>Creates an Aztec Code symbol carrying raw bytes verbatim in binary shift mode.</summary>
    /// <param name="content">The bytes to encode.</param>
    /// <exception cref="ArgumentException"><paramref name="content"/> is empty.</exception>
    public AztecCode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
            throw new ArgumentException("Aztec Code content must be non-empty.", nameof(content));
        Bytes = content;
    }

    /// <summary>
    /// The percentage of the symbol's data-region capacity spent on error correction, from 5 to 95.
    /// Defaults to 23, ISO/IEC 24778's recommended level (applied on top of a fixed 3-codeword
    /// minimum reserve, per the same recommendation). A higher value trades capacity for resilience
    /// to print defects and scan damage.
    /// </summary>
    public int ErrorCorrectionPercent { get; init; } = 23;

    /// <summary>Biases automatic symbol-size selection toward a compact or full-range layout. Defaults to <see cref="AztecFormat.Automatic"/>.</summary>
    public AztecFormat Format { get; init; } = AztecFormat.Automatic;

    internal string? Text { get; }

    internal byte[]? Bytes { get; }

    /// <summary>Encodes and returns the symbol's module grid, caching the result on first use.</summary>
    /// <exception cref="ArgumentException">
    /// Both <see cref="Barcode.ModuleSize"/> and <see cref="Barcode.TargetWidth"/> are set, or
    /// <see cref="ErrorCorrectionPercent"/> is outside 5-95.
    /// </exception>
    /// <exception cref="FormatException">
    /// The content is not representable in ISO/IEC 8859-1 (string constructor only), or needs more
    /// data-codeword capacity than the largest symbol in the requested <see cref="Format"/> provides.
    /// </exception>
    public BarcodeMatrix GetMatrix() => GetEncoded().Matrix;

    private protected override BarcodeSize MeasureCore() => BarcodeGeometry.Measure2D(this, GetEncoded());

    internal override Encoded2D? GetEncoded2D() => GetEncoded();

    private Encoded2D GetEncoded()
    {
        if (_encoded is not null) return _encoded;

        if (ErrorCorrectionPercent is < 5 or > 95)
            throw new ArgumentException($"ErrorCorrectionPercent must be between 5 and 95 (was {ErrorCorrectionPercent}).", nameof(ErrorCorrectionPercent));

        var matrix = AztecEncoder.Encode(this);

        // Aztec Code needs no quiet zone (ISO/IEC 24778 clause 4.1.c.2): the bullseye finder is
        // self-quieting, unlike QR's or Data Matrix's corner-anchored finder patterns.
        return _encoded = new Encoded2D { Matrix = matrix, QuietZoneModules = 0, RowHeightModules = 1 };
    }
}
