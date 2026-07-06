// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Qr;

/// <summary>
/// The eight QR Code data mask patterns (ISO/IEC 18004 Table 10) and the penalty scoring used to
/// pick the best one for a full-size symbol (§7.8.3.1, Table 11). Micro QR uses four of these same
/// eight conditions (§7.8.3.2's edge-count scoring lives in <c>MicroQrEncoder</c>, since it needs
/// the smaller symbol's own edge geometry).
/// </summary>
internal static class QrMasking
{
    /// <summary>The four data mask condition indices (into <see cref="Condition"/>) available to Micro QR, in data-mask-pattern-reference order 00/01/10/11 (Table 10).</summary>
    internal static readonly int[] MicroMaskConditionIndices = [1, 4, 6, 7];

    /// <summary>Evaluates data mask condition <paramref name="maskId"/> (0-7) at module <paramref name="row"/>/<paramref name="column"/> (Table 10; (0,0) is the top-left module).</summary>
    internal static bool Condition(int maskId, int row, int column) => maskId switch
    {
        0 => (row + column) % 2 == 0,
        1 => row % 2 == 0,
        2 => column % 3 == 0,
        3 => (row + column) % 3 == 0,
        4 => ((row / 2) + (column / 3)) % 2 == 0,
        5 => (row * column % 2) + (row * column % 3) == 0,
        6 => ((row * column % 2) + (row * column % 3)) % 2 == 0,
        7 => ((row + column) % 2 + (row * column % 3)) % 2 == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(maskId), maskId, "Mask pattern reference must be between 0 and 7."),
    };

    /// <summary>Toggles every non-function module for which <paramref name="maskId"/>'s condition holds. Applying the same mask twice restores the original modules.</summary>
    internal static void ApplyMask(BarcodeMatrix matrix, bool[,] isFunction, int size, int maskId)
    {
        for (var row = 0; row < size; row++)
            for (var column = 0; column < size; column++)
                if (!isFunction[row, column] && Condition(maskId, row, column))
                    matrix.SetDark(column, row, !matrix.IsDark(column, row));
    }

    /// <summary>
    /// Scores a full-size QR Code symbol per Table 11: N1 for runs of 5+ same-colour modules in a
    /// row or column, N2 for 2x2 same-colour blocks, N3 for a 1:1:3:1:1 dark:light:dark:light:dark
    /// pattern flanked by four light modules, and N4 for the symbol's dark-module proportion
    /// deviating from 50%. Lower is better. Reads <paramref name="matrix"/>'s own dimensions, so it
    /// applies equally to (square) QR symbols and, for isolated rule testing, to single rows/columns.
    /// </summary>
    internal static int ComputePenalty(BarcodeMatrix matrix) =>
        RunPenalty(matrix) + BlockPenalty(matrix) + FinderLikePenalty(matrix) + DarkRatioPenalty(matrix);

    /// <summary>N4: penalises the symbol's dark-module proportion for deviating from 50% (10 points per 5% step).</summary>
    internal static int DarkRatioPenalty(BarcodeMatrix matrix)
    {
        var dark = 0;
        for (var row = 0; row < matrix.Height; row++)
            for (var column = 0; column < matrix.Width; column++)
                if (matrix.IsDark(column, row)) dark++;

        var percentDark = 100.0 * dark / (matrix.Width * matrix.Height);
        var k = (int)(Math.Abs(percentDark - 50.0) / 5.0);
        return 10 * k;
    }

    /// <summary>N1: penalises runs of 5 or more same-colour modules in a row or column (3 points, plus 1 for each module beyond 5).</summary>
    internal static int RunPenalty(BarcodeMatrix matrix)
    {
        var penalty = 0;
        for (var row = 0; row < matrix.Height; row++) penalty += RunPenaltyForLine(column => matrix.IsDark(column, row), matrix.Width);
        for (var column = 0; column < matrix.Width; column++) penalty += RunPenaltyForLine(row => matrix.IsDark(column, row), matrix.Height);
        return penalty;
    }

    private static int RunPenaltyForLine(Func<int, bool> isDarkAt, int length)
    {
        var penalty = 0;
        var runLength = 1;
        var previous = isDarkAt(0);
        for (var i = 1; i < length; i++)
        {
            var current = isDarkAt(i);
            if (current == previous)
            {
                runLength++;
                continue;
            }

            if (runLength >= 5) penalty += 3 + (runLength - 5);
            runLength = 1;
            previous = current;
        }

        if (runLength >= 5) penalty += 3 + (runLength - 5);
        return penalty;
    }

    /// <summary>N2: penalises each 2x2 same-colour block (3 points each, including overlapping blocks within a larger uniform area).</summary>
    internal static int BlockPenalty(BarcodeMatrix matrix)
    {
        var penalty = 0;
        for (var row = 0; row < matrix.Height - 1; row++)
        {
            for (var column = 0; column < matrix.Width - 1; column++)
            {
                var colour = matrix.IsDark(column, row);
                if (matrix.IsDark(column + 1, row) == colour &&
                    matrix.IsDark(column, row + 1) == colour &&
                    matrix.IsDark(column + 1, row + 1) == colour)
                    penalty += 3;
            }
        }

        return penalty;
    }

    // The 1:1:3:1:1 pattern (dark,light,dark,dark,dark,light,dark) flanked by four light modules,
    // read as an 11-bit window (dark = 1) with the flank leading (0x05D) or trailing (0x5D0).
    private const int FinderLikePatternLeading = 0b000_0101_1101;
    private const int FinderLikePatternTrailing = 0b101_1101_0000;
    private const int FinderLikeWindowMask = 0b111_1111_1111;

    /// <summary>N3: penalises a 1:1:3:1:1 dark:light:dark:light:dark pattern flanked by four light modules on either side (40 points per occurrence).</summary>
    internal static int FinderLikePenalty(BarcodeMatrix matrix)
    {
        var penalty = 0;
        for (var row = 0; row < matrix.Height; row++) penalty += FinderLikeLinePenalty(column => matrix.IsDark(column, row), matrix.Width);
        for (var column = 0; column < matrix.Width; column++) penalty += FinderLikeLinePenalty(row => matrix.IsDark(column, row), matrix.Height);
        return penalty;
    }

    private static int FinderLikeLinePenalty(Func<int, bool> isDarkAt, int length)
    {
        var penalty = 0;
        var window = 0;
        for (var i = 0; i < length; i++)
        {
            window = ((window << 1) | (isDarkAt(i) ? 1 : 0)) & FinderLikeWindowMask;
            if (i < 10) continue;
            if (window == FinderLikePatternLeading || window == FinderLikePatternTrailing) penalty += 40;
        }

        return penalty;
    }
}
