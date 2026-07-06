// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;
using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes;

/// <summary>
/// A QR Code symbol (ISO/IEC 18004, model 2, versions 1-40). The version, error correction level
/// and data mask are chosen automatically unless overridden; text content is segmented across
/// numeric, alphanumeric and byte mode for the smallest fitting symbol. See the barcodes guide's
/// QR charset policy for how <see cref="TextEncoding"/> affects non-Latin-1 text, and its GS1 mode
/// section for <see cref="Gs1"/>.
/// </summary>
public sealed class QrCode : Barcode
{
    private Encoded2D? _encoded;

    /// <summary>Creates a QR Code symbol from text, segmented across numeric, alphanumeric and byte mode as content allows.</summary>
    /// <param name="content">The text to encode.</param>
    public QrCode(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Text = content;
    }

    /// <summary>Creates a QR Code symbol carrying raw bytes verbatim in byte mode (ISO/IEC 8859-1, one codeword per byte), ignoring <see cref="TextEncoding"/>.</summary>
    /// <param name="content">The bytes to encode.</param>
    public QrCode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Bytes = content;
    }

    /// <summary>The error-correction level. Defaults to <see cref="QrErrorCorrection.M"/>.</summary>
    public QrErrorCorrection ErrorCorrection { get; init; } = QrErrorCorrection.M;

    /// <summary>Forces a specific version (1-40) instead of the smallest one that fits the content.</summary>
    public int? Version { get; init; }

    /// <summary>Forces a specific data mask pattern (0-7) instead of the one with the lowest penalty score.</summary>
    public int? Mask { get; init; }

    /// <summary>How byte-mode text content is encoded and whether an ECI header names it. Defaults to <see cref="QrTextEncoding.Auto"/>. Ignored by the byte-array constructor.</summary>
    public QrTextEncoding TextEncoding { get; init; } = QrTextEncoding.Auto;

    /// <summary>
    /// Encodes <see cref="Text"/> as GS1 data instead of verbatim text. Defaults to <see cref="QrGs1Mode.None"/>.
    /// Not supported by the byte-array constructor: GS1 element strings are character data, so a
    /// <see cref="QrCode(byte[])"/> symbol with this set throws at encode time.
    /// </summary>
    public QrGs1Mode Gs1 { get; init; } = QrGs1Mode.None;

    internal string? Text { get; }

    internal byte[]? Bytes { get; }

    /// <summary>Encodes and returns the symbol's module grid, caching the result on first use.</summary>
    /// <exception cref="ArgumentException">
    /// <see cref="Version"/> or <see cref="Mask"/> is outside its valid range, both <see cref="Barcode.ModuleSize"/>
    /// and <see cref="Barcode.TargetWidth"/> are set, or <see cref="Gs1"/> is not <see cref="QrGs1Mode.None"/>
    /// on a symbol built from the byte-array constructor.
    /// </exception>
    /// <exception cref="FormatException">
    /// The content does not fit (the forced <see cref="Version"/>, or any version up to 40) at
    /// <see cref="ErrorCorrection"/>; <see cref="QrTextEncoding.Latin1"/> was requested for
    /// non-Latin-1 text; or, when <see cref="Gs1"/> is set, the content is not well-formed GS1
    /// element-string data.
    /// </exception>
    public BarcodeMatrix GetMatrix() => GetEncoded().Matrix;

    private protected override BarcodeSize MeasureCore() => BarcodeGeometry.Measure2D(this, GetEncoded());

    internal override Encoded2D? GetEncoded2D() => GetEncoded();

    private Encoded2D GetEncoded()
    {
        if (_encoded is not null) return _encoded;

        if (Version is { } version && version is < 1 or > 40)
            throw new ArgumentException($"Version must be between 1 and 40 (was {version}).", nameof(Version));
        if (Mask is { } mask && mask is < 0 or > 7)
            throw new ArgumentException($"Mask must be between 0 and 7 (was {mask}).", nameof(Mask));

        var matrix = QrEncoder.Encode(this);
        return _encoded = new Encoded2D { Matrix = matrix, QuietZoneModules = 4, RowHeightModules = 1 };
    }
}
