// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// Arithmetic over GF(2^m) for an arbitrary field size and primitive polynomial: exponent and
/// discrete-log tables for a chosen primitive element, derived at construction by repeated
/// carry-less multiplication reduced modulo the primitive polynomial. Nothing is transcribed
/// from a reference table — the same guarantee <see cref="ReedSolomonGf256"/> makes for GF(256),
/// generalized here to any binary extension field so it also covers the 10- and 12-bit fields
/// Aztec Code (ISO/IEC 24778) uses for its larger symbol sizes.
/// </summary>
internal sealed class GaloisField
{
    /// <summary>x^4 + x + 1, GF(16) — Aztec Code's mode-message Reed-Solomon field.</summary>
    internal static readonly GaloisField Gf16 = new(16, 0x13);

    /// <summary>x^6 + x + 1, GF(64) — Aztec Code's 6-bit-codeword symbol sizes.</summary>
    internal static readonly GaloisField Gf64 = new(64, 0x43);

    /// <summary>
    /// x^8 + x^5 + x^3 + x^2 + 1, GF(256) — Data Matrix ECC 200 (ISO/IEC 16022) and Aztec
    /// Code's 8-bit-codeword symbol sizes. This is not the field <see cref="ReedSolomonGf256"/>
    /// uses: QR (ISO/IEC 18004) is GF(256) too, but with primitive polynomial 0x11D instead.
    /// </summary>
    internal static readonly GaloisField Gf256 = new(256, 0x12D);

    /// <summary>x^10 + x^3 + 1, GF(1024) — Aztec Code's 10-bit-codeword symbol sizes.</summary>
    internal static readonly GaloisField Gf1024 = new(1024, 0x409);

    /// <summary>x^12 + x^6 + x^5 + x^3 + 1, GF(4096) — Aztec Code's 12-bit-codeword symbol sizes.</summary>
    internal static readonly GaloisField Gf4096 = new(4096, 0x1069);

    // Extended to 2*(Size-1) entries so Exp lookups for a sum of two exponents (each already
    // reduced modulo Size-1) never need an explicit modulo; entry i for i >= Size-1 repeats
    // entry i - (Size-1).
    private readonly int[] _exp;
    private readonly int[] _log;

    /// <summary>The field size: a power of two, e.g. 256 for GF(256).</summary>
    internal int Size { get; }

    /// <summary>
    /// Builds GF(<paramref name="fieldSize"/>) from its primitive polynomial and primitive
    /// element. <paramref name="primitivePolynomial"/> is the degree-m polynomial (m =
    /// log2(fieldSize)) packed as an (m+1)-bit integer with the x^m term set — e.g. 0x13 for
    /// x^4 + x + 1. <paramref name="generator"/> is the primitive element (alpha); every
    /// non-zero field element is some power of it.
    /// </summary>
    internal GaloisField(int fieldSize, int primitivePolynomial, int generator = 2)
    {
        if (fieldSize < 4 || (fieldSize & (fieldSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(fieldSize), fieldSize, "Field size must be a power of two of at least 4.");

        Size = fieldSize;
        var order = fieldSize - 1; // size of the multiplicative group
        _exp = new int[order * 2];
        _log = new int[fieldSize];

        var x = 1;
        for (var i = 0; i < order; i++)
        {
            _exp[i] = x;
            _log[x] = i;
            x = MultiplyModPoly(x, generator, fieldSize, primitivePolynomial);
        }

        for (var i = order; i < _exp.Length; i++) _exp[i] = _exp[i - order];
    }

    /// <summary>Multiplies two field elements.</summary>
    internal int Multiply(int a, int b)
    {
        if (a == 0 || b == 0) return 0;
        return _exp[_log[a] + _log[b]];
    }

    /// <summary>Returns the primitive element raised to <paramref name="power"/>, reducing the exponent modulo Size-1.</summary>
    internal int Exp(int power)
    {
        var order = Size - 1;
        var reduced = power % order;
        if (reduced < 0) reduced += order;
        return _exp[reduced];
    }

    /// <summary>Returns the discrete log (base the field's primitive element) of a non-zero field element.</summary>
    internal int Log(int x)
    {
        if (x <= 0 || x >= Size)
            throw new ArgumentOutOfRangeException(nameof(x), x, "log(0) is undefined and x must be a field element.");
        return _log[x];
    }

    /// <summary>Returns the multiplicative inverse of a non-zero field element.</summary>
    internal int Inverse(int x)
    {
        var log = Log(x); // validates x is a non-zero field element
        return Exp((Size - 1) - log);
    }

    /// <summary>
    /// Carry-less (XOR) multiplication of two elements below <paramref name="fieldSize"/>,
    /// reduced modulo <paramref name="primitivePolynomial"/> one bit at a time — plain GF(2^m)
    /// multiplication, used only while deriving the exp/log tables above.
    /// </summary>
    private static int MultiplyModPoly(int a, int b, int fieldSize, int primitivePolynomial)
    {
        var result = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) result ^= a;
            b >>= 1;
            a <<= 1;
            if ((a & fieldSize) != 0) a ^= primitivePolynomial;
        }

        return result;
    }
}
