// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Pdf417;

/// <summary>
/// Solves a PDF417 symbol's column and row counts (ISO/IEC 15438 allows 1-30 data columns and
/// 3-90 rows, with at most 928 codewords per symbol). When neither is forced, the solver searches
/// every feasible column count and picks whichever comes closest to the barcode's preferred
/// width-to-height aspect ratio.
/// </summary>
internal static class Pdf417Dimensions
{
    internal const int MinColumns = 1;
    internal const int MaxColumns = 30;
    internal const int MinRows = 3;
    internal const int MaxRows = 90;
    internal const int MaxTotalCodewords = 928;

    /// <summary>The solved column and row counts, and the resulting total codewords per symbol (data, pad and error correction together).</summary>
    /// <param name="Columns">The number of data columns per row (1-30).</param>
    /// <param name="Rows">The number of rows (3-90).</param>
    /// <param name="TotalCodewords">The total codewords in the symbol: <c>Columns * Rows</c>.</param>
    internal readonly record struct Result(int Columns, int Rows, int TotalCodewords);

    /// <summary>The overall symbol width, in modules, for the given number of data columns (start + left row indicator + data + right row indicator + stop).</summary>
    internal static int WidthModules(int columns) => ((columns + 3) * Pdf417Tables.PatternModules) + Pdf417Tables.StopPatternModules;

    /// <summary>
    /// Resolves the column and row counts for a symbol carrying <paramref name="dataCodewords"/>
    /// data-region codewords (the symbol length descriptor, the compacted content and any padding)
    /// plus <paramref name="ecCodewords"/> error-correction codewords.
    /// </summary>
    /// <param name="dataCodewords">The number of data-region codewords before padding (symbol length descriptor plus compacted content).</param>
    /// <param name="ecCodewords">The number of error-correction codewords the chosen level requires.</param>
    /// <param name="columns">A forced column count (1-30), or <c>null</c> to solve it.</param>
    /// <param name="rows">A forced row count (3-90), or <c>null</c> to solve it.</param>
    /// <param name="preferredAspectRatio">The width-to-height ratio to aim for when both <paramref name="columns"/> and <paramref name="rows"/> are unset.</param>
    /// <param name="rowHeightModules">The height of one row, in modules, used to evaluate the aspect ratio.</param>
    /// <exception cref="ArgumentException"><paramref name="columns"/> or <paramref name="rows"/> is set but outside its valid range.</exception>
    /// <exception cref="FormatException">No column/row combination within range holds <paramref name="dataCodewords"/> plus <paramref name="ecCodewords"/> codewords within the 928-codeword limit.</exception>
    internal static Result Resolve(int dataCodewords, int ecCodewords, int? columns, int? rows, double preferredAspectRatio, double rowHeightModules)
    {
        if (columns is { } explicitColumns && explicitColumns is < MinColumns or > MaxColumns)
            throw new ArgumentException($"Columns must be between {MinColumns} and {MaxColumns} (was {explicitColumns}).", nameof(columns));
        if (rows is { } explicitRows && explicitRows is < MinRows or > MaxRows)
            throw new ArgumentException($"Rows must be between {MinRows} and {MaxRows} (was {explicitRows}).", nameof(rows));

        var required = dataCodewords + ecCodewords;

        if (columns is { } fixedColumns && rows is { } fixedRows)
        {
            var total = fixedColumns * fixedRows;
            if (total > MaxTotalCodewords || total < required)
                throw new FormatException(
                    $"{dataCodewords} data codewords plus {ecCodewords} error-correction codewords do not fit in {fixedColumns} columns x {fixedRows} rows ({total} total codewords available, {required} required, {MaxTotalCodewords} maximum).");
            return new Result(fixedColumns, fixedRows, total);
        }

        if (columns is { } onlyColumns)
        {
            var neededRows = Math.Max(MinRows, CeilDiv(required, onlyColumns));
            var total = onlyColumns * neededRows;
            if (neededRows > MaxRows || total > MaxTotalCodewords)
                throw new FormatException(
                    $"{dataCodewords} data codewords plus {ecCodewords} error-correction codewords need more than {MaxRows} rows at {onlyColumns} columns.");
            return new Result(onlyColumns, neededRows, total);
        }

        if (rows is { } onlyRows)
        {
            var neededColumns = Math.Max(MinColumns, CeilDiv(required, onlyRows));
            var total = neededColumns * onlyRows;
            if (neededColumns > MaxColumns || total > MaxTotalCodewords)
                throw new FormatException(
                    $"{dataCodewords} data codewords plus {ecCodewords} error-correction codewords need more than {MaxColumns} columns at {onlyRows} rows.");
            return new Result(neededColumns, onlyRows, total);
        }

        var bestColumns = -1;
        var bestRows = -1;
        var bestScore = double.MaxValue;
        for (var candidateColumns = MinColumns; candidateColumns <= MaxColumns; candidateColumns++)
        {
            var neededRows = Math.Max(MinRows, CeilDiv(required, candidateColumns));
            if (neededRows > MaxRows) continue;

            var total = candidateColumns * neededRows;
            if (total > MaxTotalCodewords) continue;

            var aspect = WidthModules(candidateColumns) / (neededRows * rowHeightModules);
            var score = Math.Abs(aspect - preferredAspectRatio);
            if (score < bestScore)
            {
                bestScore = score;
                bestColumns = candidateColumns;
                bestRows = neededRows;
            }
        }

        if (bestColumns < 0)
            throw new FormatException(
                $"{dataCodewords} data codewords plus {ecCodewords} error-correction codewords exceed PDF417's maximum of {MaxTotalCodewords} total codewords.");

        return new Result(bestColumns, bestRows, bestColumns * bestRows);
    }

    private static int CeilDiv(int numerator, int denominator) => (numerator + denominator - 1) / denominator;
}
