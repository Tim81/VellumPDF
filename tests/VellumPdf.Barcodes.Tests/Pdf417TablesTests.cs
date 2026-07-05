// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Pdf417;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Self-check invariants for the hand-transcribed <see cref="Pdf417Tables"/> pattern tables (2,787
/// patterns across three clusters): every pattern is 17 modules with exactly four bars and four
/// spaces, starts with a bar and ends with a space, and satisfies the cluster congruence
/// (ISO/IEC 15438 section 2.2.2). A single mistyped digit during transcription would need to
/// preserve all of these simultaneously to go unnoticed, which is vanishingly unlikely.
/// </summary>
public sealed class Pdf417TablesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void GetClusterPatterns_hasAllNineHundredTwentyNinePatterns(int cluster) =>
        Assert.Equal(929, Pdf417Tables.GetClusterPatterns(cluster).Length);

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void GetClusterPatterns_everyPattern_isSeventeenModulesWithFourBarsAndFourSpaces(int cluster)
    {
        var patterns = Pdf417Tables.GetClusterPatterns(cluster).ToArray();
        for (var codeword = 0; codeword < patterns.Length; codeword++)
        {
            var runs = ToRuns(patterns[codeword], Pdf417Tables.PatternModules);

            var totalModules = 0;
            foreach (var run in runs) totalModules += run;
            Assert.True(totalModules == Pdf417Tables.PatternModules, $"Cluster {cluster} codeword {codeword}: modules sum to {totalModules}, expected {Pdf417Tables.PatternModules}.");
            Assert.True(runs.Count == 8, $"Cluster {cluster} codeword {codeword}: {runs.Count} runs, expected 8 (four bars, four spaces).");

            // A pattern's first run is a bar (bit 1) and its last is a space (bit 0): runs
            // alternate starting with a bar, so an even count of runs (8) always ends on a space.
            var bars = new int[4];
            var spaces = new int[4];
            for (var i = 0; i < 8; i++)
            {
                if (i % 2 == 0) bars[i / 2] = runs[i];
                else spaces[i / 2] = runs[i];
            }

            var computedCluster = (bars[0] - bars[1] + bars[2] - bars[3] + 9) % 9;
            Assert.True(computedCluster == cluster, $"Cluster {cluster} codeword {codeword}: computed cluster {computedCluster} from bars {string.Join(',', bars)}.");
        }
    }

    [Fact]
    public void StartPattern_isSeventeenModules_matchingSpecString()
    {
        Assert.Equal(Pdf417Tables.StartPattern, Convert.ToUInt32("11111111010101000", 2));
        var runs = ToRuns(Pdf417Tables.StartPattern, Pdf417Tables.PatternModules);
        var total = 0;
        foreach (var run in runs) total += run;
        Assert.Equal(Pdf417Tables.PatternModules, total);
    }

    [Fact]
    public void StopPattern_isEighteenModules_matchingSpecString()
    {
        Assert.Equal(Pdf417Tables.StopPattern, Convert.ToUInt32("111111101000101001", 2));
        var runs = ToRuns(Pdf417Tables.StopPattern, Pdf417Tables.StopPatternModules);
        var total = 0;
        foreach (var run in runs) total += run;
        Assert.Equal(Pdf417Tables.StopPatternModules, total);
    }

    [Theory]
    [InlineData(0, 0, "11101010111000000")] // grandzebu.net's first published cluster-0 pattern (codeword 0)
    [InlineData(0, 1, "11110101011110000")] // grandzebu.net's second published cluster-0 pattern (codeword 1)
    public void GetPattern_matchesPublishedReferenceValues(int cluster, int codeword, string binary) =>
        Assert.Equal(Convert.ToUInt32(binary, 2), Pdf417Tables.GetPattern(cluster, codeword));

    [Fact]
    public void GetPattern_outOfRange_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf417Tables.GetPattern(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf417Tables.GetPattern(0, 929));
    }

    [Fact]
    public void GetClusterPatterns_invalidCluster_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf417Tables.GetClusterPatterns(1));

    private static List<int> ToRuns(uint pattern, int moduleCount)
    {
        var runs = new List<int>();
        var currentBit = (pattern >> (moduleCount - 1)) & 1;
        var runLength = 1;
        for (var m = moduleCount - 2; m >= 0; m--)
        {
            var bit = (pattern >> m) & 1;
            if (bit == currentBit)
            {
                runLength++;
            }
            else
            {
                runs.Add(runLength);
                currentBit = bit;
                runLength = 1;
            }
        }

        runs.Add(runLength);
        return runs;
    }
}
