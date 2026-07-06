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
    /// <exception cref="FormatException">The content does not fit any version (or the forced <see cref="QrCode.Version"/>) at the requested error correction level, or Latin-1 text encoding was requested for non-Latin-1 content.</exception>
    internal static BarcodeMatrix Encode(QrCode barcode)
    {
        var (segmentFactory, content, byteEncoding, useEci, contentLength) = barcode.Bytes is { } bytes
            ? PrepareByteContent(bytes)
            : PrepareStringContent(barcode.Text!, barcode.TextEncoding);

        var (version, dataCodewords) = SelectVersionAndBuild(barcode, content, segmentFactory, byteEncoding, useEci, contentLength);

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
        var latin1Representable = IsLatin1Representable(text);
        var (byteEncoding, useEci) = textEncoding switch
        {
            QrTextEncoding.Latin1 when !latin1Representable =>
                throw new FormatException($"\"{text}\" contains characters outside ISO/IEC 8859-1 (Latin-1); use QrTextEncoding.Utf8, Utf8Eci or Auto instead."),
            QrTextEncoding.Latin1 => (Encoding.Latin1, false),
            QrTextEncoding.Utf8 => (Encoding.UTF8, false),
            QrTextEncoding.Utf8Eci => (Encoding.UTF8, true),
            QrTextEncoding.Auto => latin1Representable ? (Encoding.Latin1, false) : (Encoding.UTF8, true),
            _ => throw new ArgumentOutOfRangeException(nameof(textEncoding), textEncoding, null),
        };

        return (headerBits => QrSegmenter.Segment(text, headerBits, byteEncoding, allowAlphanumeric: true, allowByte: true), text, byteEncoding, useEci, text.Length);
    }

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
        _ => throw new ArgumentOutOfRangeException(nameof(segment)),
    };

    private static (int Version, byte[] DataCodewords) SelectVersionAndBuild(
        QrCode barcode, string content, SegmentFactory segmentFactory, Encoding byteEncoding, bool useEci, int contentLength)
    {
        var eciBits = useEci ? 12 : 0;

        (int Group, int Version)[] candidates = barcode.Version is { } forced
            ? [(GroupFor(forced), forced)]
            : [.. VersionGroups.SelectMany((g, gi) => Enumerable.Range(g.Min, g.Max - g.Min + 1).Select(v => (gi, v)))];

        var lastGroup = -1;
        IReadOnlyList<QrSegment>? groupSegments = null;
        var groupContentBits = 0;

        foreach (var (group, version) in candidates)
        {
            if (group != lastGroup)
            {
                groupSegments = segmentFactory(mode => HeaderBits(VersionGroups[group].Min, mode));
                groupContentBits = eciBits;
                foreach (var segment in groupSegments)
                    groupContentBits += HeaderBits(VersionGroups[group].Min, segment.Mode) + SegmentDataBits(content, segment, byteEncoding);
                lastGroup = group;
            }

            var ecInfo = QrTables.GetEcBlockInfo(version, barcode.ErrorCorrection);
            var capacityBits = ecInfo.TotalDataCodewords * 8;
            if (groupContentBits > capacityBits) continue;

            var writer = new BitWriter();
            if (useEci) QrBitStreamBuilder.WriteUtf8EciHeader(writer);
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
