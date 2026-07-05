// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// GF(256) arithmetic and Reed-Solomon error-correction codewords for QR and Micro QR
/// (ISO/IEC 18004). The exponent/log tables are derived at startup from the primitive
/// polynomial x^8 + x^4 + x^3 + x^2 + 1 (0x11D) with primitive element alpha = 2 — nothing
/// is transcribed from a reference table.
/// </summary>
internal static class ReedSolomonGf256
{
    private const int PrimitivePoly = 0x11D;

    // Extended to 512 entries so Exp lookups for a sum of two exponents (0..254 each) never
    // need an explicit modulo; entry i for i >= 255 repeats entry i - 255.
    private static readonly byte[] ExpTable = new byte[512];
    private static readonly byte[] LogTable = new byte[256];

    private static readonly ConcurrentDictionary<int, byte[]> GeneratorCache = new();

    static ReedSolomonGf256()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            ExpTable[i] = (byte)x;
            LogTable[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= PrimitivePoly;
        }

        for (var i = 255; i < ExpTable.Length; i++) ExpTable[i] = ExpTable[i - 255];
    }

    /// <summary>Multiplies two GF(256) elements.</summary>
    internal static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        return ExpTable[LogTable[a] + LogTable[b]];
    }

    /// <summary>Returns alpha^<paramref name="power"/> (alpha = 2), reducing the exponent modulo 255.</summary>
    internal static byte Exp(int power)
    {
        var reduced = power % 255;
        if (reduced < 0) reduced += 255;
        return ExpTable[reduced];
    }

    /// <summary>Returns the discrete log (base alpha = 2) of a non-zero GF(256) element.</summary>
    internal static int Log(byte value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value), value, "log(0) is undefined in GF(256).");
        return LogTable[value];
    }

    /// <summary>
    /// Returns the monic Reed-Solomon generator polynomial of the given <paramref name="degree"/>,
    /// g(x) = Product of (x - alpha^i) for i = 0..degree-1, as GF(256) coefficients ordered from
    /// x^degree (always 1, included) down to x^0. Computed on first request per degree and cached.
    /// </summary>
    internal static byte[] GetGeneratorPolynomial(int degree)
    {
        if (degree < 1)
            throw new ArgumentOutOfRangeException(nameof(degree), degree, "Generator polynomial degree must be at least 1.");

        return GeneratorCache.GetOrAdd(degree, static d =>
        {
            // Build low-to-high (coefficient of x^k at index k), starting from the constant
            // polynomial "1", multiplying in one root (x - alpha^i) at a time.
            var coefficients = new byte[] { 1 };
            for (var i = 0; i < d; i++)
            {
                var root = Exp(i);
                var next = new byte[coefficients.Length + 1];
                for (var k = 0; k < next.Length; k++)
                {
                    var shifted = k >= 1 && k - 1 < coefficients.Length ? coefficients[k - 1] : (byte)0;
                    var scaled = k < coefficients.Length ? Multiply(coefficients[k], root) : (byte)0;
                    next[k] = (byte)(shifted ^ scaled);
                }

                coefficients = next;
            }

            // Reverse to high-to-low (index 0 = x^degree, always 1; index degree = x^0).
            Array.Reverse(coefficients);
            return coefficients;
        });
    }

    /// <summary>
    /// Computes the <paramref name="ecLength"/> Reed-Solomon error-correction codewords for
    /// <paramref name="data"/>: the remainder of (data as a polynomial) * x^ecLength divided by
    /// the degree-<paramref name="ecLength"/> generator polynomial, via a linear-feedback
    /// shift register (one pass over the data, no explicit polynomial multiplication).
    /// </summary>
    internal static byte[] ComputeRemainder(ReadOnlySpan<byte> data, int ecLength)
    {
        if (ecLength < 1)
            throw new ArgumentOutOfRangeException(nameof(ecLength), ecLength, "EC length must be at least 1.");

        var generator = GetGeneratorPolynomial(ecLength);
        var remainder = new byte[ecLength];

        foreach (var b in data)
        {
            var factor = (byte)(b ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, ecLength - 1);
            remainder[ecLength - 1] = 0;
            for (var i = 0; i < ecLength; i++)
                remainder[i] ^= Multiply(generator[i + 1], factor);
        }

        return remainder;
    }
}
