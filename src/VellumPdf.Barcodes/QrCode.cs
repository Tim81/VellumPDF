// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
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

    /// <summary>The Structured Append position/parity this symbol was stamped with by one of the <c>StructuredAppend</c> factories, if any.</summary>
    internal StructuredAppendInfo? StructuredAppendInfo { get; init; }

    /// <summary>
    /// Splits <paramref name="parts"/> across up to 16 linked QR Code symbols (ISO/IEC 18004 §8) at
    /// error-correction level <see cref="QrErrorCorrection.M"/> and <see cref="QrTextEncoding.Auto"/>.
    /// See the four-parameter overload for the full description and exceptions.
    /// </summary>
    public static IReadOnlyList<QrCode> StructuredAppend(IReadOnlyList<string> parts) =>
        StructuredAppend(parts, QrErrorCorrection.M, QrTextEncoding.Auto);

    /// <summary>
    /// Splits <paramref name="parts"/> across up to 16 linked QR Code symbols (ISO/IEC 18004 §8),
    /// each stamped with the set's shared parity byte and its own 0-based position. Every returned
    /// symbol is an ordinary <see cref="QrCode"/> that draws through the normal
    /// <see cref="BarcodeCanvasExtensions.DrawBarcode"/> path; the caller positions and draws each
    /// one (see the barcodes guide's Structured Append layout guidance).
    /// </summary>
    /// <param name="parts">The message, pre-split into 1 to 16 parts in reading order.</param>
    /// <param name="errorCorrection">The error-correction level applied to every symbol in the set.</param>
    /// <param name="textEncoding">
    /// How each part's byte-mode content is encoded, and how the shared parity byte is computed
    /// over the concatenated message.
    /// </param>
    /// <returns>One <see cref="QrCode"/> per part, in the same order, each carrying its Structured Append header.</returns>
    /// <exception cref="ArgumentException"><paramref name="parts"/> has fewer than 1 or more than 16 entries.</exception>
    /// <exception cref="FormatException"><see cref="QrTextEncoding.Latin1"/> was requested but the concatenated message is not representable in ISO/IEC 8859-1.</exception>
    public static IReadOnlyList<QrCode> StructuredAppend(
        IReadOnlyList<string> parts, QrErrorCorrection errorCorrection, QrTextEncoding textEncoding)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count is < 1 or > 16)
            throw new ArgumentException($"A Structured Append set holds 1 to 16 symbols (was {parts.Count}).", nameof(parts));

        var parity = ComputeStructuredAppendParity(string.Concat(parts), textEncoding);

        var symbols = new QrCode[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            symbols[i] = new QrCode(parts[i])
            {
                ErrorCorrection = errorCorrection,
                TextEncoding = textEncoding,
                StructuredAppendInfo = new StructuredAppendInfo(i, parts.Count, parity),
            };
        }

        return symbols;
    }

    /// <summary>
    /// Divides <paramref name="content"/> into <paramref name="symbolCount"/> roughly-equal parts
    /// and delegates to <see cref="StructuredAppend(IReadOnlyList{string})"/> at error-correction
    /// level <see cref="QrErrorCorrection.M"/> and <see cref="QrTextEncoding.Auto"/>. See the
    /// four-parameter overload for the full description and exceptions.
    /// </summary>
    public static IReadOnlyList<QrCode> StructuredAppend(string content, int symbolCount) =>
        StructuredAppend(content, symbolCount, QrErrorCorrection.M, QrTextEncoding.Auto);

    /// <summary>
    /// Divides <paramref name="content"/> into <paramref name="symbolCount"/> roughly-equal parts
    /// (split on Unicode scalar boundaries, never through a surrogate pair) and delegates to
    /// <see cref="StructuredAppend(IReadOnlyList{string}, QrErrorCorrection, QrTextEncoding)"/>.
    /// Prefer that overload directly when the split points matter (e.g. keeping GS1 element
    /// boundaries intact); this one is a convenience for arbitrary text.
    /// </summary>
    /// <param name="content">The message to split.</param>
    /// <param name="symbolCount">The number of symbols to split <paramref name="content"/> across (1-16).</param>
    /// <param name="errorCorrection">The error-correction level applied to every symbol in the set.</param>
    /// <param name="textEncoding">How each part's byte-mode content is encoded and how the shared parity byte is computed.</param>
    /// <returns>One <see cref="QrCode"/> per part, in reading order.</returns>
    /// <exception cref="ArgumentException"><paramref name="symbolCount"/> is less than 1 or more than 16.</exception>
    public static IReadOnlyList<QrCode> StructuredAppend(
        string content, int symbolCount, QrErrorCorrection errorCorrection, QrTextEncoding textEncoding)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (symbolCount is < 1 or > 16)
            throw new ArgumentException($"A Structured Append set holds 1 to 16 symbols (was {symbolCount}).", nameof(symbolCount));

        return StructuredAppend(SplitByRune(content, symbolCount), errorCorrection, textEncoding);
    }

    /// <summary>
    /// The parity byte shared by every symbol in a Structured Append set (ISO/IEC 18004 §8.1):
    /// the XOR of every byte of the original, un-split message, using the same byte encoding
    /// <see cref="QrEncoder.ResolveTextEncoding"/> resolves for byte-mode content. A decoder
    /// recomputing parity from the symbols' own byte-mode data then agrees with what is stamped here.
    /// </summary>
    private static byte ComputeStructuredAppendParity(string message, QrTextEncoding textEncoding)
    {
        var (byteEncoding, _) = QrEncoder.ResolveTextEncoding(message, textEncoding);
        byte parity = 0;
        foreach (var b in byteEncoding.GetBytes(message)) parity ^= b;
        return parity;
    }

    /// <summary>Splits <paramref name="content"/> into <paramref name="partCount"/> parts of nearly equal rune count, the first <c>content.Length % partCount</c> parts one rune longer.</summary>
    private static IReadOnlyList<string> SplitByRune(string content, int partCount)
    {
        var runeStarts = new List<int>();
        for (var i = 0; i < content.Length;)
        {
            runeStarts.Add(i);
            i += Rune.GetRuneAt(content, i).Utf16SequenceLength;
        }

        runeStarts.Add(content.Length);
        var runeCount = runeStarts.Count - 1;
        var baseSize = runeCount / partCount;
        var remainder = runeCount % partCount;

        var parts = new string[partCount];
        var runeIndex = 0;
        for (var i = 0; i < partCount; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            var charStart = runeStarts[runeIndex];
            runeIndex += size;
            parts[i] = content[charStart..runeStarts[runeIndex]];
        }

        return parts;
    }

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
