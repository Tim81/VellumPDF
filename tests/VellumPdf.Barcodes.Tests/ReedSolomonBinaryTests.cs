// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="ReedSolomonBinary"/>. The GF(256) known-answer vector is the worked
/// example from Wikipedia's "Data Matrix" article (message "Wikipedia", a 16×16 ECC 200 symbol:
/// 12 data codewords, 12 error-correction codewords). A second, shorter GF(256) vector and a
/// GF(16) Aztec-mode-message-style vector are cross-checked with the defining Reed-Solomon
/// property — the full codeword polynomial must vanish at every generator root — so they hold
/// independently of any transcribed value.
/// </summary>
public sealed class ReedSolomonBinaryTests
{
    // Data Matrix ECC 200 (GF(256), primitive polynomial 0x12D) uses g(x) = Product (x - alpha^i)
    // for i = 1..n, i.e. the first consecutive root is alpha^1.
    private static readonly ReedSolomonBinary DataMatrix = new(GaloisField.Gf256, firstRoot: 1);

    // Aztec Code mode-message error correction is Reed-Solomon over GF(16) with the same
    // first-root-at-alpha^1 convention (ISO/IEC 24778).
    private static readonly ReedSolomonBinary AztecGf16 = new(GaloisField.Gf16, firstRoot: 1);

    [Fact]
    public void ComputeRemainder_dataMatrixWikipediaExample_matchesPublishedCodewords()
    {
        int[] data = [88, 106, 108, 106, 113, 102, 101, 106, 98, 129, 251, 147];
        int[] expected = [104, 216, 88, 39, 233, 202, 71, 217, 26, 92, 25, 232];

        var remainder = DataMatrix.ComputeRemainder(data, 12);

        Assert.Equal(expected, remainder);
    }

    [Fact]
    public void ComputeRemainder_dataMatrixThreeCodewordExample_matchesPublishedCodewords()
    {
        // The commonly cited smallest-symbol example: 3 data codewords, 5 error codewords.
        int[] data = [142, 164, 186];
        int[] expected = [114, 25, 5, 88, 102];

        var remainder = DataMatrix.ComputeRemainder(data, 5);

        Assert.Equal(expected, remainder);
    }

    [Fact]
    public void ComputeRemainder_gf16AztecStyle_producesAValidCodeword()
    {
        int[] data = [5, 6];
        var remainder = AztecGf16.ComputeRemainder(data, 5);

        Assert.Equal(5, remainder.Length);
        AssertVanishesAtGeneratorRoots(GaloisField.Gf16, firstRoot: 1, data, remainder);

        // Pinned value, cross-checked out of band against the root-vanishing property above.
        Assert.Equal([3, 2, 11, 11, 7], remainder);
    }

    [Fact]
    public void ComputeRemainder_gf16FullModeMessageWidth_producesAValidCodeword()
    {
        int[] data = [9, 4, 0, 12];
        var remainder = AztecGf16.ComputeRemainder(data, 6);

        Assert.Equal(6, remainder.Length);
        AssertVanishesAtGeneratorRoots(GaloisField.Gf16, firstRoot: 1, data, remainder);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(20)]
    public void ComputeRemainder_lengthEqualsErrorCorrectionCount(int ecCount)
    {
        int[] data = [10, 20, 30, 40, 50];
        Assert.Equal(ecCount, DataMatrix.ComputeRemainder(data, ecCount).Length);
    }

    [Fact]
    public void ComputeRemainder_zeroData_isAllZero()
    {
        int[] data = [0, 0, 0];
        var remainder = DataMatrix.ComputeRemainder(data, 5);
        Assert.All(remainder, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ComputeRemainder_emptyData_isAllZero()
    {
        var remainder = DataMatrix.ComputeRemainder([], 5);
        Assert.Equal(new int[5], remainder);
    }

    [Fact]
    public void GetGeneratorPolynomial_isMonic_andCachesAcrossCalls()
    {
        for (var degree = 1; degree <= 20; degree++)
            Assert.Equal(1, DataMatrix.GetGeneratorPolynomial(degree)[0]);

        var first = DataMatrix.GetGeneratorPolynomial(10);
        var second = DataMatrix.GetGeneratorPolynomial(10);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeRemainder_rejectsNonPositiveErrorCorrectionCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DataMatrix.ComputeRemainder([1, 2, 3], 0));
    }

    [Fact]
    public void Constructor_rejectsNullField()
    {
        Assert.Throws<ArgumentNullException>(() => new ReedSolomonBinary(null!, 1));
    }

    [Fact]
    public void ComputeRemainder_gf64_matchesPinnedVector()
    {
        int[] data = [1, 2, 3];
        int[] expected = [1, 62, 32, 6];

        var remainder = new ReedSolomonBinary(GaloisField.Gf64, firstRoot: 1).ComputeRemainder(data, 4);

        Assert.Equal(expected, remainder);
        AssertVanishesAtGeneratorRoots(GaloisField.Gf64, firstRoot: 1, data, remainder);
    }

    [Fact]
    public void ComputeRemainder_gf1024_matchesPinnedVector()
    {
        int[] data = [100, 200, 300];
        int[] expected = [751, 1020, 1010, 296, 318];

        var remainder = new ReedSolomonBinary(GaloisField.Gf1024, firstRoot: 1).ComputeRemainder(data, 5);

        Assert.Equal(expected, remainder);
        AssertVanishesAtGeneratorRoots(GaloisField.Gf1024, firstRoot: 1, data, remainder);
    }

    [Fact]
    public void ComputeRemainder_gf4096_matchesPinnedVector()
    {
        int[] data = [1000, 2000, 3000];
        int[] expected = [582, 752, 2954, 1514, 207, 3178];

        var remainder = new ReedSolomonBinary(GaloisField.Gf4096, firstRoot: 1).ComputeRemainder(data, 6);

        Assert.Equal(expected, remainder);
        AssertVanishesAtGeneratorRoots(GaloisField.Gf4096, firstRoot: 1, data, remainder);
    }

    [Fact]
    public void ComputeRemainder_gf256_singleCheckSymbol_matchesPinnedVector()
    {
        int[] data = [10, 20, 30];
        int[] expected = [60];

        var remainder = DataMatrix.ComputeRemainder(data, 1);

        Assert.Equal(expected, remainder);
        AssertVanishesAtGeneratorRoots(GaloisField.Gf256, firstRoot: 1, data, remainder);
    }

    [Fact]
    public void ComputeRemainder_dataElementAtOrAboveFieldSize_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DataMatrix.ComputeRemainder([256], 4));
    }

    [Fact]
    public void ComputeRemainder_negativeDataElement_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DataMatrix.ComputeRemainder([-1], 4));
    }

    [Fact]
    public void ComputeRemainder_errorCorrectionCountAtOrAboveFieldSize_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DataMatrix.ComputeRemainder([1, 2, 3], 256));
    }

    // A systematic Reed-Solomon codeword — data followed by the check symbols — is a multiple of
    // the generator polynomial, so it evaluates to zero at each of the generator's roots
    // alpha^(firstRoot + i). This holds regardless of how the remainder was produced.
    private static void AssertVanishesAtGeneratorRoots(GaloisField field, int firstRoot, int[] data, int[] remainder)
    {
        var codeword = new int[data.Length + remainder.Length];
        data.CopyTo(codeword, 0);
        remainder.CopyTo(codeword, data.Length);

        for (var i = 0; i < remainder.Length; i++)
        {
            var root = field.Exp(firstRoot + i);
            var acc = 0;
            foreach (var coefficient in codeword)
                acc = field.Multiply(acc, root) ^ coefficient;
            Assert.Equal(0, acc);
        }
    }
}
