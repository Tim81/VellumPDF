// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// Reed-Solomon error correction over an injected <see cref="GaloisField"/>, generalizing
/// <see cref="ReedSolomonGf256"/>'s generator-polynomial cache and LFSR remainder loop to any
/// binary extension field and any first consecutive root. Data Matrix (ISO/IEC 16022) and Aztec
/// Code (ISO/IEC 24778) both build g(x) = Product_(i=1..n) (x - alpha^i) — i.e. a first root of
/// alpha^1 — over different fields depending on symbol size, so this is the one engine both call
/// into rather than duplicating it per symbology.
/// </summary>
internal sealed class ReedSolomonBinary
{
    private readonly GaloisField _field;
    private readonly int _firstRoot;
    private readonly ConcurrentDictionary<int, int[]> _generatorCache = new();

    /// <param name="field">The field the codewords and generator polynomial live in.</param>
    /// <param name="firstRoot">
    /// The exponent (of the field's primitive element) of the generator polynomial's first
    /// consecutive root: g(x) = Product_(i=0..degree-1) (x - alpha^(firstRoot + i)).
    /// </param>
    internal ReedSolomonBinary(GaloisField field, int firstRoot)
    {
        ArgumentNullException.ThrowIfNull(field);
        _field = field;
        _firstRoot = firstRoot;
    }

    // Bundles the per-instance state the generator-polynomial builder needs, so it can be a
    // `static` lambda (no captured `this`) passed through ConcurrentDictionary's state-passing
    // GetOrAdd overload instead of allocating a fresh capturing closure on every call.
    private readonly record struct GeneratorState(GaloisField Field, int FirstRoot);

    /// <summary>
    /// Returns the monic generator polynomial of the given <paramref name="degree"/>, as field
    /// coefficients ordered from x^degree (always 1, included) down to x^0. Computed on first
    /// request per degree and cached.
    /// </summary>
    internal int[] GetGeneratorPolynomial(int degree)
    {
        if (degree < 1)
            throw new ArgumentOutOfRangeException(nameof(degree), degree, "Generator polynomial degree must be at least 1.");

        return _generatorCache.GetOrAdd(degree, static (d, state) =>
        {
            // Build low-to-high (coefficient of x^k at index k), starting from the constant
            // polynomial "1", multiplying in one root (x - alpha^(firstRoot + i)) at a time.
            var coefficients = new[] { 1 };
            for (var i = 0; i < d; i++)
            {
                var root = state.Field.Exp(state.FirstRoot + i);
                var next = new int[coefficients.Length + 1];
                for (var k = 0; k < next.Length; k++)
                {
                    var shifted = k >= 1 && k - 1 < coefficients.Length ? coefficients[k - 1] : 0;
                    var scaled = k < coefficients.Length ? state.Field.Multiply(coefficients[k], root) : 0;
                    next[k] = shifted ^ scaled;
                }

                coefficients = next;
            }

            // Reverse to high-to-low (index 0 = x^degree, always 1; index degree = x^0).
            Array.Reverse(coefficients);
            return coefficients;
        }, new GeneratorState(_field, _firstRoot));
    }

    /// <summary>
    /// Computes the <paramref name="errorCorrectionCount"/> Reed-Solomon check symbols for
    /// <paramref name="data"/>: the remainder of (data as a polynomial) * x^errorCorrectionCount
    /// divided by the degree-<paramref name="errorCorrectionCount"/> generator polynomial, via a
    /// linear-feedback shift register (one pass over the data, no explicit polynomial
    /// multiplication).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="errorCorrectionCount"/> is less than 1 or at least the field size (the
    /// generator polynomial's roots would wrap around and repeat), or an element of
    /// <paramref name="data"/> is outside <c>[0, field.Size)</c> — unlike <c>byte</c>, <c>int</c>
    /// data has no implicit range guard, so an out-of-field value must be rejected explicitly
    /// rather than left to fail as an opaque array-index error deep in the field arithmetic.
    /// </exception>
    internal int[] ComputeRemainder(ReadOnlySpan<int> data, int errorCorrectionCount)
    {
        if (errorCorrectionCount < 1)
            throw new ArgumentOutOfRangeException(nameof(errorCorrectionCount), errorCorrectionCount, "Error-correction count must be at least 1.");
        if (errorCorrectionCount >= _field.Size)
            throw new ArgumentOutOfRangeException(nameof(errorCorrectionCount), errorCorrectionCount, $"Error-correction count must be less than the field size ({_field.Size}); the generator polynomial's roots would wrap around and repeat otherwise.");

        foreach (var value in data)
            if (value < 0 || value >= _field.Size)
                throw new ArgumentOutOfRangeException(nameof(data), value, $"Each data element must be in [0, {_field.Size}) for this field.");

        var generator = GetGeneratorPolynomial(errorCorrectionCount);
        var remainder = new int[errorCorrectionCount];

        foreach (var value in data)
        {
            var factor = value ^ remainder[0];
            Array.Copy(remainder, 1, remainder, 0, errorCorrectionCount - 1);
            remainder[errorCorrectionCount - 1] = 0;
            for (var i = 0; i < errorCorrectionCount; i++)
                remainder[i] ^= _field.Multiply(generator[i + 1], factor);
        }

        return remainder;
    }
}
