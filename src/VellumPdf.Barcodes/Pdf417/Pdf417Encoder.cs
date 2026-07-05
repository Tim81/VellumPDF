// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Pdf417;

/// <summary>
/// Orchestrates full PDF417 encoding: high-level compaction, error-correction-level resolution,
/// dimension solving, Reed-Solomon error correction (<see cref="ReedSolomonGf929"/>), row
/// indicators, and final matrix assembly.
/// </summary>
internal static class Pdf417Encoder
{
    /// <summary>The maximum data-region codewords (symbol length descriptor, content and padding) at error-correction level 0 — the spec's absolute ceiling, since every other level leaves less room.</summary>
    private const int AbsoluteMaxDataCodewords = 925;

    /// <summary>Per level (0-8), the most data-region codewords (see <see cref="AbsoluteMaxDataCodewords"/>) that still leave room for that level's error-correction codewords within the 928-codeword limit: <c>928 - 2^(level + 1) - 1</c>.</summary>
    private static readonly int[] MaxDataCodewordsByLevel = [925, 923, 919, 911, 895, 863, 799, 671, 415];

    internal static BarcodeMatrix Encode(Pdf417Barcode barcode)
    {
        var content = barcode.Bytes is { } bytes
            ? Pdf417HighLevelEncoder.EncodeBytes(bytes)
            : Pdf417HighLevelEncoder.EncodeText(barcode.Text!);

        var dataCodewords = content.Count + 1; // + symbol length descriptor
        if (dataCodewords > AbsoluteMaxDataCodewords)
            throw new FormatException($"Content needs {content.Count} data codewords, exceeding PDF417's maximum of {AbsoluteMaxDataCodewords - 1} regardless of error-correction level.");

        var level = barcode.ErrorCorrectionLevel == -1 ? ResolveRecommendedLevel(dataCodewords) : barcode.ErrorCorrectionLevel;
        var ecCodewords = ReedSolomonGf929.DegreeForLevel(level);

        var dims = Pdf417Dimensions.Resolve(dataCodewords, ecCodewords, barcode.Columns, barcode.Rows, barcode.PreferredAspectRatio, barcode.RowHeight);

        var dataRegionLength = dims.TotalCodewords - ecCodewords;
        var padCodewords = dataRegionLength - dataCodewords;

        var dataRegion = new int[dataRegionLength];
        dataRegion[0] = dataCodewords + padCodewords; // symbol length descriptor: total data-region codewords including itself and padding
        content.CopyTo(dataRegion, 1);
        for (var i = 1 + content.Count; i < dataRegionLength; i++) dataRegion[i] = 900; // pad codeword

        var ec = ReedSolomonGf929.ComputeCheckCodewords(dataRegion, ecCodewords);

        return BuildMatrix(dataRegion, ec, dims.Columns, dims.Rows, level);
    }

    /// <summary>
    /// Resolves the error-correction level for auto (<c>-1</c>) requests. Below 864 data-region
    /// codewords this follows ISO/IEC 15438's own recommended-minimum table (also published at
    /// https://grandzebu.net/informatique/codbar/pdf417-en.htm): 1-40 -> level 2, 41-160 -> level
    /// 3, 161-320 -> level 4, 321-863 -> level 5. The published table stops there because level
    /// 5's 863-codeword ceiling is already the largest a level above 0 can offer — levels 6-8
    /// reserve progressively more codewords for error correction, so their data ceilings (799,
    /// 671, 415) are all lower still. For the rare symbol needing more than 863 data-region
    /// codewords, this falls back to the highest level (below 5) that still has room, since no
    /// level above 5 ever could.
    /// </summary>
    internal static int ResolveRecommendedLevel(int dataCodewords)
    {
        if (dataCodewords <= 40) return 2;
        if (dataCodewords <= 160) return 3;
        if (dataCodewords <= 320) return 4;
        if (dataCodewords <= 863) return 5;

        for (var level = 4; level >= 0; level--)
            if (dataCodewords <= MaxDataCodewordsByLevel[level]) return level;

        throw new FormatException($"{dataCodewords - 1} content codewords exceed PDF417's maximum capacity at any error-correction level.");
    }

    private static BarcodeMatrix BuildMatrix(int[] dataRegion, int[] ec, int columns, int rows, int level)
    {
        var width = Pdf417Dimensions.WidthModules(columns);
        var matrix = new BarcodeMatrix(width, rows);

        var y = (rows - 1) / 3;
        var z = (level * 3) + ((rows - 1) % 3);
        var v = columns - 1;

        for (var row = 0; row < rows; row++)
        {
            var cluster = (row % 3) * 3;
            var xi = row / 3;

            var (left, right) = cluster switch
            {
                0 => ((30 * xi) + y, (30 * xi) + v),
                3 => ((30 * xi) + z, (30 * xi) + y),
                _ => ((30 * xi) + v, (30 * xi) + z), // cluster 6
            };

            var x = 0;
            x = PlacePattern(matrix, row, x, Pdf417Tables.StartPattern, Pdf417Tables.PatternModules);
            x = PlacePattern(matrix, row, x, Pdf417Tables.GetPattern(cluster, left), Pdf417Tables.PatternModules);

            for (var c = 0; c < columns; c++)
            {
                var index = (row * columns) + c;
                var codeword = index < dataRegion.Length ? dataRegion[index] : ec[index - dataRegion.Length];
                x = PlacePattern(matrix, row, x, Pdf417Tables.GetPattern(cluster, codeword), Pdf417Tables.PatternModules);
            }

            x = PlacePattern(matrix, row, x, Pdf417Tables.GetPattern(cluster, right), Pdf417Tables.PatternModules);
            PlacePattern(matrix, row, x, Pdf417Tables.StopPattern, Pdf417Tables.StopPatternModules);
        }

        return matrix;
    }

    private static int PlacePattern(BarcodeMatrix matrix, int row, int startX, uint pattern, int moduleCount)
    {
        for (var m = 0; m < moduleCount; m++)
        {
            var bit = (pattern >> (moduleCount - 1 - m)) & 1;
            matrix.SetDark(startX + m, row, bit != 0);
        }

        return startX + moduleCount;
    }
}
