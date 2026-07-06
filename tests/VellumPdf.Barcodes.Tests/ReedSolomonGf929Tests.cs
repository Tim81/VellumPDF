// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="ReedSolomonGf929"/>, cross-checked against Wikipedia's worked RS(7,3)
/// PDF417 example (Reed-Solomon error correction, "Example" under Peterson-Gorenstein-Zierler).
/// </summary>
public sealed class ReedSolomonGf929Tests
{
    [Fact]
    public void GetGeneratorPolynomial_degree4_matchesWikipediaRs73Example()
    {
        // g(x) = (x - 3)(x - 3^2)(x - 3^3)(x - 3^4) = x^4 + 809x^3 + 723x^2 + 568x + 522
        var g = ReedSolomonGf929.GetGeneratorPolynomial(4);
        Assert.Equal([1, 809, 723, 568, 522], g);
    }

    [Fact]
    public void ComputeCheckCodewords_rs73Example_matchesWikipedia()
    {
        // p(x) = 3x^2 + 2x + 1 -> check symbols (382, 191, 487, 474); the Wikipedia example also
        // gives the intermediate remainder s_r(x) = 547x^3 + 738x^2 + 442x + 455, i.e. the
        // negation (mod 929) of the returned check codewords.
        int[] message = [3, 2, 1];
        var check = ReedSolomonGf929.ComputeCheckCodewords(message, 4);
        Assert.Equal([382, 191, 487, 474], check);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(8, 512)]
    public void DegreeForLevel_isTwoToThePowerOfLevelPlusOne(int level, int expectedDegree) =>
        Assert.Equal(expectedDegree, ReedSolomonGf929.DegreeForLevel(level));

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void DegreeForLevel_outsideZeroToEight_throws(int level) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReedSolomonGf929.DegreeForLevel(level));

    [Fact]
    public void Mod_reducesNegativeAndOverflowingValues_intoZeroToModulusRange()
    {
        Assert.Equal(928, ReedSolomonGf929.Mod(-1));
        Assert.Equal(0, ReedSolomonGf929.Mod(929));
        Assert.Equal(1, ReedSolomonGf929.Mod(930));
    }

    [Fact]
    public void GetGeneratorPolynomial_isMonic_leadingCoefficientIsOne()
    {
        for (var degree = 1; degree <= 16; degree++)
            Assert.Equal(1, ReedSolomonGf929.GetGeneratorPolynomial(degree)[0]);
    }
}
