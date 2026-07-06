// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Qr;

/// <summary>
/// Orchestrates Micro QR encoding (ISO/IEC 18004 versions M1-M4): a single finder pattern instead
/// of three, timing patterns along row/column 0 instead of 6, no alignment patterns, no ECI, and
/// per-version restrictions on which modes and error-correction levels are available. There is no
/// version information (Micro QR has none) and format information has a single copy rather than
/// QR's redundant pair.
/// </summary>
internal static class MicroQrEncoder
{
    /// <summary>Returns the module grid side length for Micro QR version <paramref name="microVersion"/> (1-4): 11, 13, 15 or 17.</summary>
    internal static int SizeForVersion(int microVersion) => 9 + (2 * microVersion);

    /// <summary>Encodes <paramref name="barcode"/>'s content into a Micro QR symbol.</summary>
    /// <exception cref="ArgumentException">The forced <see cref="MicroQrCode.Version"/> does not support <see cref="MicroQrCode.ErrorCorrection"/>.</exception>
    /// <exception cref="FormatException">The content is not Latin-1, or does not fit the forced version (or any of M1-M4) at the requested level.</exception>
    internal static BarcodeMatrix Encode(MicroQrCode barcode)
    {
        var content = barcode.Content;
        foreach (var rune in content.EnumerateRunes())
            if (rune.Value > 0xFF)
                throw new FormatException($"Micro QR does not support ECI, so \"{content}\" must be representable in ISO/IEC 8859-1 (Latin-1); '{rune}' is not.");

        int[] versionsToTry = barcode.Version is { } forced ? [forced] : [1, 2, 3, 4];
        var lastFailureMessage = "";

        foreach (var microVersion in versionsToTry)
        {
            if (!TryResolveLevel(microVersion, barcode.ErrorCorrection, autoSelecting: barcode.Version is null, out var level))
            {
                if (barcode.Version is not null)
                    throw new ArgumentException($"Micro QR version M{microVersion} does not support error correction level {barcode.ErrorCorrection}.", nameof(MicroQrCode.ErrorCorrection));
                continue;
            }

            var allowAlphanumeric = microVersion >= 2;
            var allowByte = microVersion >= 3;

            IReadOnlyList<QrSegment> segments;
            try
            {
                segments = QrSegmenter.Segment(content, mode => HeaderBits(microVersion, mode), Encoding.Latin1, allowAlphanumeric, allowByte);
            }
            catch (FormatException ex)
            {
                if (barcode.Version is not null) throw;
                lastFailureMessage = ex.Message;
                continue;
            }

            var capacity = QrTables.GetMicroCapacity(microVersion, level);
            var contentBits = 0;
            foreach (var segment in segments)
                contentBits += HeaderBits(microVersion, segment.Mode) + QrEncoder.SegmentDataBits(content, segment, Encoding.Latin1);

            if (contentBits > capacity.DataBits)
            {
                lastFailureMessage = $"needs {contentBits} bits, exceeding version M{microVersion} level {level}'s capacity of {capacity.DataBits} bits";
                if (barcode.Version is not null)
                    throw new FormatException($"Content of length {content.Length} {lastFailureMessage} ({capacity.DataCodewords} data codewords).");
                continue;
            }

            return Build(content, microVersion, level, segments, capacity);
        }

        throw new FormatException($"Content of length {content.Length} does not fit any Micro QR version at error correction level {barcode.ErrorCorrection}"
            + (lastFailureMessage.Length > 0 ? $" ({lastFailureMessage})." : "."));
    }

    private static BarcodeMatrix Build(string content, int microVersion, QrErrorCorrection level, IReadOnlyList<QrSegment> segments, MicroQrCapacity capacity)
    {
        var writer = new BitWriter();
        QrBitStreamBuilder.WriteSegments(
            writer,
            content,
            segments,
            mode => (QrTables.MicroModeIndicator(microVersion, mode), QrTables.MicroModeIndicatorBits(microVersion)),
            mode => QrTables.MicroCharacterCountBits(microVersion, mode),
            Encoding.Latin1);

        var dataCodewords = QrBitStreamBuilder.Finish(writer, capacity.DataCodewords, QrTables.MicroTerminatorBits(microVersion), capacity.LastCodewordIsHalfWidth);
        var allCodewords = QrBlockInterleaver.AppendSingleBlockEc(dataCodewords, capacity.EcCodewords);

        var size = SizeForVersion(microVersion);
        var matrix = new BarcodeMatrix(size, size);
        var isFunction = new bool[size, size];

        QrMatrixBuilder.DrawFinderAndSeparator(matrix, isFunction, size, 0, 0);
        DrawTimingPatterns(matrix, isFunction, size);
        ReserveFormatInfo(isFunction);

        var halfWidthIndex = capacity.LastCodewordIsHalfWidth ? capacity.DataCodewords - 1 : (int?)null;
        QrMatrixBuilder.PlaceData(matrix, isFunction, size, allCodewords, skipColumn: null, halfWidthCodewordIndex: halfWidthIndex);

        var maskReference = ChooseBestMask(matrix, isFunction, size);
        QrMasking.ApplyMask(matrix, isFunction, size, QrMasking.MicroMaskConditionIndices[maskReference]);

        var symbolNumber = QrTables.MicroSymbolNumber(microVersion, level);
        PlaceFormatInfo(matrix, QrFormatVersionInfo.ComputeMicroFormatBits(symbolNumber, maskReference));

        return matrix;
    }

    /// <summary>Whether <paramref name="microVersion"/> can be encoded at <paramref name="requestedLevel"/> (Table 13: M1 has no selectable level; M2/M3 offer L/M; M4 offers L/M/Q, never H).</summary>
    private static bool TryResolveLevel(int microVersion, QrErrorCorrection requestedLevel, bool autoSelecting, out QrErrorCorrection level)
    {
        level = requestedLevel;
        return microVersion switch
        {
            1 => !autoSelecting || requestedLevel == QrErrorCorrection.L, // M1 is "error detection only"; only offered automatically at the default level
            2 or 3 => requestedLevel is QrErrorCorrection.L or QrErrorCorrection.M,
            4 => requestedLevel is QrErrorCorrection.L or QrErrorCorrection.M or QrErrorCorrection.Q,
            _ => false,
        };
    }

    private static int HeaderBits(int microVersion, QrSegmentMode mode) =>
        QrTables.MicroModeIndicatorBits(microVersion) + QrTables.MicroCharacterCountBits(microVersion, mode);

    private static void DrawTimingPatterns(BarcodeMatrix matrix, bool[,] isFunction, int size)
    {
        for (var i = 8; i < size; i++)
        {
            isFunction[0, i] = true;
            matrix.SetDark(i, 0, i % 2 == 0);
            isFunction[i, 0] = true;
            matrix.SetDark(0, i, i % 2 == 0);
        }
    }

    // The 15-bit format information has a single copy (no redundancy), adjacent to the one
    // finder pattern: bits 0-6 run down column 8 (rows 1-7, row 0 being the timing pattern),
    // bit 7 sits at the corner (8, 8), and bits 8-14 run left along row 8 (columns 7 down to 1,
    // column 0 being the timing pattern) — the same corner arrangement as QR's first copy,
    // shifted by one row/column to clear Micro QR's row/column-0 timing pattern.
    private static void ReserveFormatInfo(bool[,] isFunction)
    {
        for (var row = 1; row <= 8; row++) isFunction[row, 8] = true;
        for (var col = 1; col <= 8; col++) isFunction[8, col] = true;
    }

    private static void PlaceFormatInfo(BarcodeMatrix matrix, int bits)
    {
        for (var i = 0; i <= 6; i++) matrix.SetDark(8, i + 1, ((bits >> i) & 1) != 0);
        matrix.SetDark(8, 8, ((bits >> 7) & 1) != 0);
        for (var i = 8; i <= 14; i++) matrix.SetDark(15 - i, 8, ((bits >> i) & 1) != 0);
    }

    /// <summary>
    /// Scores each of the four Micro QR data masks by the number of dark modules along the right
    /// and lower edges (excluding each edge's one timing-pattern module, §7.8.3.2) and returns the
    /// 2-bit data mask pattern reference (Table 10) with the highest score.
    /// </summary>
    private static int ChooseBestMask(BarcodeMatrix matrix, bool[,] isFunction, int size)
    {
        var bestReference = 0;
        var bestScore = -1;

        for (var reference = 0; reference < 4; reference++)
        {
            var conditionIndex = QrMasking.MicroMaskConditionIndices[reference];
            QrMasking.ApplyMask(matrix, isFunction, size, conditionIndex);

            var sum1 = 0; // right edge, excluding the row-0 timing module
            for (var row = 1; row < size; row++)
                if (matrix.IsDark(size - 1, row)) sum1++;

            var sum2 = 0; // lower edge, excluding the column-0 timing module
            for (var col = 1; col < size; col++)
                if (matrix.IsDark(col, size - 1)) sum2++;

            var score = sum1 <= sum2 ? (sum1 * 16) + sum2 : (sum2 * 16) + sum1;

            QrMasking.ApplyMask(matrix, isFunction, size, conditionIndex); // undo

            if (score > bestScore)
            {
                bestScore = score;
                bestReference = reference;
            }
        }

        return bestReference;
    }
}
