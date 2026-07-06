// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// Reed-Solomon error correction over the integers mod 929 with primitive root 3, as used by
/// PDF417 (ISO/IEC 15438). Unlike <see cref="ReedSolomonGf256"/>, 929 is prime, so this is
/// ordinary modular integer arithmetic rather than a binary extension field: addition and
/// subtraction are genuinely different operations.
/// </summary>
internal static class ReedSolomonGf929
{
    /// <summary>The PDF417 codeword modulus.</summary>
    internal const int Modulus = 929;

    private const int PrimitiveRoot = 3;

    private static readonly ConcurrentDictionary<int, int[]> GeneratorCache = new();

    /// <summary>Reduces a value into the range [0, 929).</summary>
    internal static int Mod(long value)
    {
        var reduced = (int)(value % Modulus);
        return reduced < 0 ? reduced + Modulus : reduced;
    }

    /// <summary>
    /// Returns the degree for a PDF417 error-correction level (0-8): 2^(level + 1) check
    /// codewords, per ISO/IEC 15438.
    /// </summary>
    internal static int DegreeForLevel(int errorCorrectionLevel)
    {
        if (errorCorrectionLevel is < 0 or > 8)
            throw new ArgumentOutOfRangeException(
                nameof(errorCorrectionLevel), errorCorrectionLevel, "PDF417 error-correction level must be between 0 and 8.");
        return 1 << (errorCorrectionLevel + 1);
    }

    /// <summary>
    /// Returns the monic generator polynomial g(x) = Product of (x - 3^i) for i = 1..degree,
    /// as integer coefficients mod 929 ordered from x^degree (always 1) down to x^0.
    /// Computed on first request per degree and cached.
    /// </summary>
    internal static int[] GetGeneratorPolynomial(int degree)
    {
        if (degree < 1)
            throw new ArgumentOutOfRangeException(nameof(degree), degree, "Generator polynomial degree must be at least 1.");

        return GeneratorCache.GetOrAdd(degree, static d =>
        {
            // Build low-to-high (coefficient of x^k at index k), starting from the constant
            // polynomial "1", multiplying in one root (x - 3^i) at a time.
            var coefficients = new[] { 1 };
            var root = 1;
            for (var i = 0; i < d; i++)
            {
                root = Mod((long)root * PrimitiveRoot); // root = 3^(i + 1) after this update
                var next = new int[coefficients.Length + 1];
                for (var k = 0; k < next.Length; k++)
                {
                    var shifted = k >= 1 && k - 1 < coefficients.Length ? coefficients[k - 1] : 0;
                    var scaled = k < coefficients.Length ? Mod((long)coefficients[k] * root) : 0;
                    next[k] = Mod(shifted - scaled);
                }

                coefficients = next;
            }

            // Reverse to high-to-low (index 0 = x^degree, always 1; index degree = x^0).
            Array.Reverse(coefficients);
            return coefficients;
        });
    }

    /// <summary>
    /// Computes the systematic Reed-Solomon check codewords for a message polynomial (integer
    /// coefficients mod 929, ordered highest degree first): the negated remainder of
    /// message(x) * x^degree divided by the degree-<paramref name="degree"/> generator
    /// polynomial, via schoolbook polynomial long division.
    /// </summary>
    internal static int[] ComputeCheckCodewords(ReadOnlySpan<int> messageHighToLow, int degree)
    {
        var generator = GetGeneratorPolynomial(degree);

        // `remainder` starts as message(x) * x^degree: the message coefficients followed by
        // `degree` zero coefficients for the low-order terms.
        var remainder = new int[messageHighToLow.Length + degree];
        messageHighToLow.CopyTo(remainder);

        for (var i = 0; i < messageHighToLow.Length; i++)
        {
            var coefficient = remainder[i];
            if (coefficient == 0) continue;
            for (var j = 0; j <= degree; j++)
                remainder[i + j] = Mod(remainder[i + j] - Mod((long)coefficient * generator[j]));
        }

        var checkCodewords = new int[degree];
        for (var i = 0; i < degree; i++)
            checkCodewords[i] = Mod(-remainder[messageHighToLow.Length + i]);
        return checkCodewords;
    }
}
