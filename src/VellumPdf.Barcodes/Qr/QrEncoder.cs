// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Qr;

/// <summary>Orchestrates full-size QR Code encoding: version/mask selection, segmentation, the bit stream, block interleaving, and final matrix construction.</summary>
internal static class QrEncoder
{
    private static readonly (int Min, int Max)[] VersionGroups = [(1, 9), (10, 26), (27, 40)];

    /// <summary>Encodes <paramref name="barcode"/>'s string or byte content into a QR Code symbol.</summary>
    /// <exception cref="ArgumentException"><see cref="QrCode.Gs1"/> is not <see cref="QrGs1Mode.None"/> on a symbol built from the byte-array constructor.</exception>
    /// <exception cref="FormatException">
    /// The content does not fit any version (or the forced <see cref="QrCode.Version"/>) at the
    /// requested error correction level; Latin-1 text encoding was requested for non-Latin-1
    /// content; or, under <see cref="QrCode.Gs1"/>, the content is not well-formed GS1 element-string data.
    /// </exception>
    internal static BarcodeMatrix Encode(QrCode barcode)
    {
        if (barcode.Gs1 != QrGs1Mode.None && barcode.Bytes is not null)
            throw new ArgumentException(
                $"{nameof(QrCode.Gs1)} requires text content ({nameof(QrCode)}(string) constructor) and cannot be combined with the byte-array constructor.",
                nameof(barcode));

        // Gs1ElementString.Parse (and, transitively, Gs1DigitalLink.Build) reject null-or-empty
        // input with ArgumentException, since that guard is written against a public-parameter
        // contract. But QrCode.Gs1's own documented contract (and the barcodes guide) promises
        // FormatException for any not-well-formed GS1 content, so empty content is intercepted
        // here rather than leaking that internal ArgumentException (and its "input" parameter
        // name, meaningless to a QrCode caller) out of GetMatrix().
        if (barcode.Gs1 != QrGs1Mode.None && string.IsNullOrEmpty(barcode.Text))
            throw new FormatException("GS1 content is empty; a GS1 QR symbol requires at least one application identifier.");

        var gs1Fnc1FirstPosition = barcode.Gs1 == QrGs1Mode.ElementString;

        var (segmentFactory, content, byteEncoding, useEci, contentLength) = barcode.Gs1 switch
        {
            QrGs1Mode.ElementString => PrepareGs1ElementStringContent(Gs1ElementString.Parse(barcode.Text!).EncoderPayload),
            QrGs1Mode.DigitalLink => PrepareStringContent(Gs1DigitalLink.Build(barcode.Text!), barcode.TextEncoding),
            _ => barcode.Bytes is { } bytes
                ? PrepareByteContent(bytes)
                : PrepareStringContent(barcode.Text!, barcode.TextEncoding),
        };

        var (version, dataCodewords) = SelectVersionAndBuild(barcode, content, segmentFactory, byteEncoding, useEci, contentLength, gs1Fnc1FirstPosition);

        var ecInfo = QrTables.GetEcBlockInfo(version, barcode.ErrorCorrection);
        var allCodewords = QrBlockInterleaver.Interleave(dataCodewords, ecInfo);

        var (matrix, isFunction) = QrMatrixBuilder.BuildFunctionPatterns(version);
        var size = QrMatrixBuilder.SizeForVersion(version);
        QrMatrixBuilder.PlaceData(matrix, isFunction, size, allCodewords);

        var mask = barcode.Mask ?? ChooseBestMask(matrix, isFunction, size);
        QrMasking.ApplyMask(matrix, isFunction, size, mask);

        QrMatrixBuilder.PlaceFormatInfo(matrix, size, QrFormatVersionInfo.ComputeQrFormatBits(barcode.ErrorCorrection, mask));
        if (version >= 7) QrMatrixBuilder.PlaceVersionInfo(matrix, size, QrFormatVersionInfo.ComputeVersionBits(version));

        return matrix;
    }

    private static int ChooseBestMask(BarcodeMatrix matrix, bool[,] isFunction, int size)
    {
        var bestMask = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            QrMasking.ApplyMask(matrix, isFunction, size, mask);
            var penalty = QrMasking.ComputePenalty(matrix);
            QrMasking.ApplyMask(matrix, isFunction, size, mask); // undo (XOR is its own inverse)
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
            }
        }

        return bestMask;
    }

    private delegate IReadOnlyList<QrSegment> SegmentFactory(Func<QrSegmentMode, int> headerBits);

    private static (SegmentFactory SegmentFactory, string Content, Encoding ByteEncoding, bool UseEci, int ContentLength) PrepareByteContent(byte[] bytes)
    {
        // Byte-mode content bypasses the charset policy (and the numeric/alphanumeric DP)
        // entirely: it is carried verbatim, one codeword per byte, via ISO/IEC 8859-1 so each
        // byte round-trips to a single character.
        var content = Encoding.Latin1.GetString(bytes);
        var segment = new QrSegment(QrSegmentMode.Byte, 0, content.Length, content.Length);
        return (_ => [segment], content, Encoding.Latin1, UseEci: false, ContentLength: bytes.Length);
    }

    private static (SegmentFactory SegmentFactory, string Content, Encoding ByteEncoding, bool UseEci, int ContentLength) PrepareStringContent(string text, QrTextEncoding textEncoding)
    {
        var (byteEncoding, useEci) = ResolveTextEncoding(text, textEncoding);
        return (headerBits => QrSegmenter.Segment(text, headerBits, byteEncoding, allowAlphanumeric: true, allowByte: true, allowKanji: true), text, byteEncoding, useEci, text.Length);
    }

    /// <summary>
    /// Resolves <paramref name="textEncoding"/> to the byte encoding and ECI-header decision
    /// <see cref="PrepareStringContent"/> would use for <paramref name="text"/>. Also used by
    /// <see cref="QrCode.StructuredAppend(IReadOnlyList{string}, QrErrorCorrection, QrTextEncoding)"/>
    /// to compute the Structured Append parity byte with the same byte representation the set's
    /// symbols encode their content in.
    /// </summary>
    /// <exception cref="FormatException"><see cref="QrTextEncoding.Latin1"/> was requested for text outside ISO/IEC 8859-1.</exception>
    internal static (Encoding ByteEncoding, bool UseEci) ResolveTextEncoding(string text, QrTextEncoding textEncoding)
    {
        var latin1Representable = IsLatin1Representable(text);
        return textEncoding switch
        {
            QrTextEncoding.Latin1 when !latin1Representable =>
                throw new FormatException($"\"{text}\" contains characters outside ISO/IEC 8859-1 (Latin-1); use QrTextEncoding.Utf8, Utf8Eci or Auto instead."),
            QrTextEncoding.Latin1 => (Encoding.Latin1, false),
            QrTextEncoding.Utf8 => (Encoding.UTF8, false),
            QrTextEncoding.Utf8Eci => (Encoding.UTF8, true),
            QrTextEncoding.Auto => latin1Representable ? (Encoding.Latin1, false) : (Encoding.UTF8, true),
            _ => throw new ArgumentOutOfRangeException(nameof(textEncoding), textEncoding, null),
        };
    }

    /// <summary>
    /// Prepares a GS1 element-string payload (already normalized by <see cref="Gs1ElementString.Parse"/>,
    /// U+001D standing in for each required field separator) for the FNC1-in-first-position path.
    /// </summary>
    /// <remarks>
    /// Always Latin-1 with no ECI header: <see cref="Gs1ElementString"/> restricts every value
    /// character to the printable-ASCII range, so the payload is Latin-1-representable by
    /// construction, and an ECI-tagged charset would be non-standard for a GS1 symbol regardless
    /// of <see cref="QrCode.TextEncoding"/>.
    ///
    /// <para>
    /// Alphanumeric mode is deliberately not offered. ISO/IEC 18004 §7.4.8.2 lets a FNC1-mode
    /// symbol represent the field separator as <c>%</c> in Alphanumeric mode (doubled to <c>%%</c>
    /// for a literal <c>%</c> in the data), but that substitution is only safe to apply once a run's
    /// mode is already decided, and this encoder picks each run's mode automatically — doubling
    /// every <c>%</c> ahead of segmentation would corrupt any run the segmenter instead assigns to
    /// Byte mode. Byte mode carries the same clause's other sanctioned form without that ambiguity:
    /// every non-digit character, including the field separator itself, is written as a single raw
    /// byte (0x1D for the separator), so Numeric mode still compacts digit runs and no character
    /// needs escaping.
    /// </para>
    /// <para>
    /// Kanji mode is deliberately not offered either. <see cref="Gs1ElementString"/> restricts
    /// every value character to printable ASCII, which is not GS1 AI content even where some
    /// byte also happens to double as a Shift-JIS Kanji codepoint (e.g. 0x815F, a double-byte
    /// Kanji block entry, maps to U+005C REVERSE SOLIDUS). Mixing Kanji-mode runs into a GS1
    /// element string would be non-standard for the profile.
    /// </para>
    /// </remarks>
    private static (SegmentFactory SegmentFactory, string Content, Encoding ByteEncoding, bool UseEci, int ContentLength) PrepareGs1ElementStringContent(string content) =>
        (headerBits => QrSegmenter.Segment(content, headerBits, Encoding.Latin1, allowAlphanumeric: false, allowByte: true, allowKanji: false),
            content, Encoding.Latin1, UseEci: false, ContentLength: content.Length);

    private static bool IsLatin1Representable(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value > 0xFF) return false;
        return true;
    }

    private static int HeaderBits(int version, QrSegmentMode mode) => QrTables.ModeIndicatorBits + QrTables.CharacterCountBits(version, mode);

    /// <summary>The number of data bits <paramref name="segment"/> contributes (excluding its mode/count header), for both QR and Micro QR.</summary>
    internal static int SegmentDataBits(string content, QrSegment segment, Encoding byteEncoding) => segment.Mode switch
    {
        QrSegmentMode.Numeric => (10 * (segment.RuneCount / 3)) + (segment.RuneCount % 3) switch { 0 => 0, 1 => 4, _ => 7 },
        QrSegmentMode.Alphanumeric => (11 * (segment.RuneCount / 2)) + (segment.RuneCount % 2 == 1 ? 6 : 0),
        QrSegmentMode.Byte => 8 * byteEncoding.GetByteCount(content.AsSpan(segment.CharStart, segment.CharLength)),
        QrSegmentMode.Kanji => 13 * segment.RuneCount,
        _ => throw new ArgumentOutOfRangeException(nameof(segment)),
    };

    private static (int Version, byte[] DataCodewords) SelectVersionAndBuild(
        QrCode barcode, string content, SegmentFactory segmentFactory, Encoding byteEncoding, bool useEci, int contentLength, bool gs1Fnc1FirstPosition)
    {
        var gs1Bits = gs1Fnc1FirstPosition ? QrTables.ModeIndicatorBits : 0;
        // §8.1: the Structured Append header is 4 (mode) + 8 (sequence indicator) + 8 (parity) = 20 bits.
        var structuredAppend = barcode.StructuredAppendInfo;
        var saBits = structuredAppend is not null ? 20 : 0;

        (int Group, int Version)[] candidates = barcode.Version is { } forced
            ? [(GroupFor(forced), forced)]
            : [.. VersionGroups.SelectMany((g, gi) => Enumerable.Range(g.Min, g.Max - g.Min + 1).Select(v => (gi, v)))];

        var lastGroup = -1;
        IReadOnlyList<QrSegment>? groupSegments = null;
        var groupContentBits = 0;
        var groupWritesEci = false;

        foreach (var (group, version) in candidates)
        {
            if (group != lastGroup)
            {
                groupSegments = segmentFactory(mode => HeaderBits(VersionGroups[group].Min, mode));

                // The ECI header only changes how a Byte-mode segment's bytes are interpreted, so
                // it is wasted capacity when segmentation produces no Byte-mode run at all (an
                // all-Kanji message, for instance). It is also not merely wasteful: zxing-cpp was
                // observed decoding a Kanji-only symbol incorrectly when an ECI header preceded
                // the Kanji segment with no Byte segment to apply it to, so this omission is
                // required for interoperability, not just an optimization.
                groupWritesEci = useEci && groupSegments.Any(s => s.Mode == QrSegmentMode.Byte);
                groupContentBits = (groupWritesEci ? 12 : 0) + gs1Bits + saBits;
                foreach (var segment in groupSegments)
                    groupContentBits += HeaderBits(VersionGroups[group].Min, segment.Mode) + SegmentDataBits(content, segment, byteEncoding);
                lastGroup = group;
            }

            var ecInfo = QrTables.GetEcBlockInfo(version, barcode.ErrorCorrection);
            var capacityBits = ecInfo.TotalDataCodewords * 8;
            if (groupContentBits > capacityBits) continue;

            var writer = new BitWriter();
            // §8.1: the Structured Append header, when present, comes before everything else:
            // the ECI header, the FNC1-in-first-position marker, and the first data segment alike.
            if (structuredAppend is { } sa) QrBitStreamBuilder.WriteStructuredAppendHeader(writer, sa.Index, sa.Total, sa.Parity);
            if (groupWritesEci) QrBitStreamBuilder.WriteUtf8EciHeader(writer);
            // §7.4.8.2: FNC1 in first position is placed after any ECI header and immediately
            // before the first data-encoding mode indicator.
            if (gs1Fnc1FirstPosition) QrBitStreamBuilder.WriteFnc1FirstPosition(writer);
            QrBitStreamBuilder.WriteSegments(
                writer,
                content,
                groupSegments!,
                mode => (QrTables.ModeIndicator(mode), QrTables.ModeIndicatorBits),
                mode => QrTables.CharacterCountBits(version, mode),
                byteEncoding);

            var dataCodewords = QrBitStreamBuilder.Finish(writer, ecInfo.TotalDataCodewords, QrTables.TerminatorBits, lastCodewordIsHalfWidth: false);
            return (version, dataCodewords);
        }

        if (barcode.Version is { } explicitVersion)
        {
            var ecInfo = QrTables.GetEcBlockInfo(explicitVersion, barcode.ErrorCorrection);
            throw new FormatException(
                $"Content of length {contentLength} needs {groupContentBits} bits, exceeding version {explicitVersion} level {barcode.ErrorCorrection}'s capacity of {ecInfo.TotalDataCodewords * 8} bits ({ecInfo.TotalDataCodewords} data codewords).");
        }

        var v40 = QrTables.GetEcBlockInfo(40, barcode.ErrorCorrection);
        throw new FormatException(
            $"Content of length {contentLength} exceeds the QR Code capacity even at version 40, level {barcode.ErrorCorrection} ({v40.TotalDataCodewords} data codewords, {v40.TotalDataCodewords * 8} bits).");
    }

    private static int GroupFor(int version)
    {
        for (var i = 0; i < VersionGroups.Length; i++)
            if (version >= VersionGroups[i].Min && version <= VersionGroups[i].Max) return i;
        throw new ArgumentOutOfRangeException(nameof(version), version, "QR Code version must be between 1 and 40.");
    }
}
