// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;
using VellumPdf.Barcodes.Pdf417;

namespace VellumPdf.Barcodes;

/// <summary>
/// A PDF417 symbol (ISO/IEC 15438): a stacked linear barcode with 3-90 rows of 1-30 data columns,
/// chosen automatically to match <see cref="PreferredAspectRatio"/> unless <see cref="Columns"/>
/// or <see cref="Rows"/> is set. Content is compacted automatically across text, byte and numeric
/// modes following the specification's mode-switching heuristics. Set <see cref="Compact"/> for
/// the narrower Compact (Truncated) format. Use
/// <see cref="MacroSet(IReadOnlyList{string}, int, MacroPdf417Options)"/> to split a larger
/// payload across several linked Macro PDF417 symbols (ISO/IEC 15438 Annex H).
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

    /// <summary>
    /// Renders the Compact (Truncated) format instead of the standard one (ISO/IEC 15438): the
    /// right row-indicator column is left out and the 18-module stop pattern is replaced by a
    /// single dark module, narrowing the symbol. The start pattern, left row indicator, data
    /// codewords and Reed-Solomon error correction are unaffected, but the symbol loses the
    /// error-correction redundancy the dropped right-side elements normally provide, so it is
    /// less tolerant of damage near its right edge. Defaults to <c>false</c>.
    /// </summary>
    public bool Compact { get; init; }

    internal string? Text { get; }

    internal byte[]? Bytes { get; }

    /// <summary>The Macro PDF417 segment this symbol was stamped with by one of the <c>MacroSet</c> factories, if any.</summary>
    internal MacroSegmentInfo? MacroSegmentInfo { get; init; }

    /// <summary>
    /// Splits <paramref name="parts"/> across up to 99999 linked PDF417 symbols (ISO/IEC 15438
    /// Annex H) sharing <paramref name="fileId"/>, with no optional fields beyond the segment
    /// count. See the three-parameter overload for the full description and exceptions.
    /// </summary>
    public static IReadOnlyList<Pdf417Barcode> MacroSet(IReadOnlyList<string> parts, int fileId) =>
        MacroSet(parts, fileId, new MacroPdf417Options());

    /// <summary>
    /// Splits <paramref name="parts"/> across up to 99999 linked PDF417 symbols (ISO/IEC 15438
    /// Annex H), each carrying a Macro control block appended after its data codewords (so the
    /// symbol's Reed-Solomon error correction covers the control block too). Every returned
    /// symbol is an ordinary <see cref="Pdf417Barcode"/> that draws through the normal
    /// <see cref="BarcodeCanvasExtensions.DrawBarcode"/> path; the caller positions and draws each
    /// one (see the barcodes guide's Macro PDF417 layout guidance).
    /// </summary>
    /// <param name="parts">The message, pre-split into 1 to 99999 parts in reading order.</param>
    /// <param name="fileId">The identifier every symbol in the set shares (0-899, ISO/IEC 15438 Annex H).</param>
    /// <param name="options">
    /// Optional fields carried on the set's last symbol. <see cref="MacroPdf417Options.SegmentCount"/>,
    /// when left unset, defaults to <paramref name="parts"/>'s count.
    /// </param>
    /// <returns>One <see cref="Pdf417Barcode"/> per part, in the same order, each carrying its Macro control block.</returns>
    /// <exception cref="ArgumentException"><paramref name="parts"/> has fewer than 1 or more than 99999 entries, or <paramref name="fileId"/> is outside 0-899.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="MacroPdf417Options.Timestamp"/> is before the Unix epoch, <see cref="MacroPdf417Options.FileSize"/>
    /// is negative, <see cref="MacroPdf417Options.Checksum"/> is outside 0-65535, or
    /// <see cref="MacroPdf417Options.SegmentCount"/> is negative.
    /// </exception>
    public static IReadOnlyList<Pdf417Barcode> MacroSet(IReadOnlyList<string> parts, int fileId, MacroPdf417Options options)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(options);
        if (parts.Count is < 1 or > MacroControlBlock.MaxSegmentIndex + 1)
            throw new ArgumentException($"A Macro PDF417 set holds 1 to {MacroControlBlock.MaxSegmentIndex + 1} segments (was {parts.Count}).", nameof(parts));
        if (fileId is < 0 or > MacroControlBlock.MaxFileId)
            throw new ArgumentException($"fileId must be between 0 and {MacroControlBlock.MaxFileId} (was {fileId}).", nameof(fileId));
        if (options.Timestamp is { } timestamp && timestamp.ToUnixTimeSeconds() < 0)
            throw new ArgumentOutOfRangeException(nameof(options), timestamp, "Timestamp must not be before the Unix epoch (1970-01-01T00:00:00Z).");
        if (options.FileSize is { } fileSize && fileSize < 0)
            throw new ArgumentOutOfRangeException(nameof(options), fileSize, "FileSize must not be negative.");
        if (options.Checksum is { } checksum && checksum is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), checksum, "Checksum must be between 0 and 65535 (a CCITT-16 CRC).");
        if (options.SegmentCount is { } segmentCount && segmentCount < 0)
            throw new ArgumentOutOfRangeException(nameof(options), segmentCount, "SegmentCount must not be negative.");

        var lastSegmentOptions = options.SegmentCount is null ? options with { SegmentCount = parts.Count } : options;

        var symbols = new Pdf417Barcode[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            var isLast = i == parts.Count - 1;
            symbols[i] = new Pdf417Barcode(parts[i])
            {
                MacroSegmentInfo = new MacroSegmentInfo(i, fileId, isLast, isLast ? lastSegmentOptions : null),
            };
        }

        return symbols;
    }

    /// <summary>
    /// Splits <paramref name="content"/> into <paramref name="symbolCount"/> roughly-equal parts
    /// (split on Unicode scalar boundaries, never through a surrogate pair) and delegates to
    /// <see cref="MacroSet(IReadOnlyList{string}, int, MacroPdf417Options)"/> with no optional
    /// fields beyond the segment count. See the four-parameter overload for the full description.
    /// </summary>
    public static IReadOnlyList<Pdf417Barcode> MacroSet(string content, int symbolCount, int fileId) =>
        MacroSet(content, symbolCount, fileId, new MacroPdf417Options());

    /// <summary>
    /// Splits <paramref name="content"/> into <paramref name="symbolCount"/> roughly-equal parts
    /// (split on Unicode scalar boundaries, never through a surrogate pair) and delegates to
    /// <see cref="MacroSet(IReadOnlyList{string}, int, MacroPdf417Options)"/>. Prefer that
    /// overload directly when the split points need to fall on specific boundaries rather than
    /// roughly-equal rune counts.
    /// </summary>
    /// <param name="content">The message to split.</param>
    /// <param name="symbolCount">The number of symbols to split <paramref name="content"/> across (1-99999).</param>
    /// <param name="fileId">The identifier every symbol in the set shares (0-899, ISO/IEC 15438 Annex H).</param>
    /// <param name="options">
    /// Optional fields carried on the set's last symbol. <see cref="MacroPdf417Options.SegmentCount"/>,
    /// when left unset, defaults to <paramref name="symbolCount"/>.
    /// </param>
    /// <returns>One <see cref="Pdf417Barcode"/> per part, in reading order.</returns>
    /// <exception cref="ArgumentException"><paramref name="symbolCount"/> is less than 1 or more than 99999, or <paramref name="fileId"/> is outside 0-899.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="MacroPdf417Options.Timestamp"/> is before the Unix epoch, <see cref="MacroPdf417Options.FileSize"/>
    /// is negative, <see cref="MacroPdf417Options.Checksum"/> is outside 0-65535, or
    /// <see cref="MacroPdf417Options.SegmentCount"/> is negative.
    /// </exception>
    /// <exception cref="FormatException"><paramref name="content"/> contains an unpaired UTF-16 surrogate.</exception>
    public static IReadOnlyList<Pdf417Barcode> MacroSet(string content, int symbolCount, int fileId, MacroPdf417Options options)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (symbolCount is < 1 or > MacroControlBlock.MaxSegmentIndex + 1)
            throw new ArgumentException($"A Macro PDF417 set holds 1 to {MacroControlBlock.MaxSegmentIndex + 1} segments (was {symbolCount}).", nameof(symbolCount));

        return MacroSet(RuneSplitter.SplitByRune(content, symbolCount), fileId, options);
    }

    /// <summary>Encodes and returns the symbol's module grid, caching the result on first use. Each row of the grid is one PDF417 row; the painter stretches it to <see cref="RowHeight"/> modules tall.</summary>
    /// <exception cref="ArgumentException"><see cref="ErrorCorrectionLevel"/>, <see cref="Columns"/> or <see cref="Rows"/> is outside its valid range, <see cref="RowHeight"/> is less than 3, <see cref="PreferredAspectRatio"/> is not a positive finite number, or both <see cref="Barcode.ModuleSize"/> and <see cref="Barcode.TargetWidth"/> are set.</exception>
    /// <exception cref="FormatException">The content is not representable in ISO/IEC 8859-1, or does not fit within 928 codewords (or the forced <see cref="Columns"/>/<see cref="Rows"/>) at <see cref="ErrorCorrectionLevel"/>.</exception>
    public BarcodeMatrix GetMatrix() => GetEncoded().Matrix;

    private protected override BarcodeSize MeasureCore() => BarcodeGeometry.Measure2D(this, GetEncoded());

    internal override Encoded2D? GetEncoded2D() => GetEncoded();

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
