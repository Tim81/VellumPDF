// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;
using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes;

/// <summary>
/// A Micro QR Code symbol (ISO/IEC 18004, versions M1-M4): a compact QR variant with a single
/// finder pattern, for short messages where a full QR Code's three finders would waste space.
/// There is no Extended Channel Interpretation (ECI) support, so content must be representable in
/// ISO/IEC 8859-1 (Latin-1). The smallest version that fits the content is chosen automatically
/// unless <see cref="Version"/> is set; version M1 supports numeric data only and provides error
/// detection rather than correction, and error correction level H is never available.
/// </summary>
public sealed class MicroQrCode : Barcode
{
    private Encoded2D? _encoded;

    /// <summary>Creates a Micro QR symbol from text, segmented across numeric, alphanumeric and byte mode as the resolved version allows.</summary>
    /// <param name="content">The text to encode. Must be representable in ISO/IEC 8859-1 (Latin-1).</param>
    public MicroQrCode(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    /// <summary>The text to encode.</summary>
    public string Content { get; }

    /// <summary>
    /// The requested error-correction level. Defaults to <see cref="QrErrorCorrection.L"/>. Version
    /// M1 always provides error detection only regardless of this setting; M2 and M3 support L and
    /// M; M4 supports L, M and Q. H is never available in Micro QR.
    /// </summary>
    public QrErrorCorrection ErrorCorrection { get; init; } = QrErrorCorrection.L;

    /// <summary>Forces a specific version (1-4, meaning M1-M4) instead of the smallest one that fits the content and error-correction level.</summary>
    public int? Version { get; init; }

    /// <summary>Encodes and returns the symbol's module grid, caching the result on first use.</summary>
    /// <exception cref="ArgumentException"><see cref="Version"/> is outside 1-4, it does not support <see cref="ErrorCorrection"/>, or both <see cref="Barcode.ModuleSize"/> and <see cref="Barcode.TargetWidth"/> are set.</exception>
    /// <exception cref="FormatException"><see cref="Content"/> is not representable in ISO/IEC 8859-1, needs a mode the resolved version does not support, or does not fit the forced <see cref="Version"/> (or any of M1-M4) at <see cref="ErrorCorrection"/>.</exception>
    public BarcodeMatrix GetMatrix() => GetEncoded().Matrix;

    private protected override BarcodeSize MeasureCore() => BarcodeGeometry.Measure2D(this, GetEncoded());

    private Encoded2D GetEncoded()
    {
        if (_encoded is not null) return _encoded;

        if (Version is { } version && version is < 1 or > 4)
            throw new ArgumentException($"Version must be between 1 and 4 (was {version}).", nameof(Version));

        var matrix = MicroQrEncoder.Encode(this);
        return _encoded = new Encoded2D { Matrix = matrix, QuietZoneModules = 2, RowHeightModules = 1 };
    }
}
