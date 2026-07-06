// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="ReedSolomonGf256"/>. The degree-10 generator polynomial is cross-checked
/// against the alpha-exponent coefficients published in the Thonky QR code tutorial
/// ("How to Create a Generator Polynomial"), which cross-checks against ISO/IEC 18004 Annex A.
/// </summary>
public sealed class ReedSolomonGf256Tests
{
    [Fact]
    public void GetGeneratorPolynomial_degree10_matchesPublishedAlphaExponents()
    {
        // alpha^0 x^10 + alpha^251 x^9 + alpha^67 x^8 + alpha^46 x^7 + alpha^61 x^6
        //   + alpha^118 x^5 + alpha^70 x^4 + alpha^64 x^3 + alpha^94 x^2 + alpha^32 x + alpha^45
        int[] expectedExponents = [0, 251, 67, 46, 61, 118, 70, 64, 94, 32, 45];

        var coefficients = ReedSolomonGf256.GetGeneratorPolynomial(10);

        Assert.Equal(11, coefficients.Length);
        var actualExponents = new int[coefficients.Length];
        for (var i = 0; i < coefficients.Length; i++) actualExponents[i] = ReedSolomonGf256.Log(coefficients[i]);
        Assert.Equal(expectedExponents, actualExponents);
    }

    [Fact]
    public void GetGeneratorPolynomial_isMonic_leadingCoefficientIsOne()
    {
        for (var degree = 1; degree <= 30; degree++)
            Assert.Equal(1, ReedSolomonGf256.GetGeneratorPolynomial(degree)[0]);
    }

    [Fact]
    public void GetGeneratorPolynomial_cachesAndReturnsEquivalentResult()
    {
        var first = ReedSolomonGf256.GetGeneratorPolynomial(18);
        var second = ReedSolomonGf256.GetGeneratorPolynomial(18);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ExpAndLog_areInverses_forEveryNonZeroElement()
    {
        for (var value = 1; value <= 255; value++)
            Assert.Equal((byte)value, ReedSolomonGf256.Exp(ReedSolomonGf256.Log((byte)value)));
    }

    [Fact]
    public void Multiply_byOne_isIdentity()
    {
        for (var value = 0; value <= 255; value++)
            Assert.Equal((byte)value, ReedSolomonGf256.Multiply((byte)value, 1));
    }

    [Fact]
    public void Multiply_byZero_isZero()
    {
        Assert.Equal(0, ReedSolomonGf256.Multiply(200, 0));
        Assert.Equal(0, ReedSolomonGf256.Multiply(0, 200));
    }

    [Fact]
    public void ComputeRemainder_returnsExactlyEcLengthBytes()
    {
        byte[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
        var remainder = ReedSolomonGf256.ComputeRemainder(data, 10);
        Assert.Equal(10, remainder.Length);
    }
}
