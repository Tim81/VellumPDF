// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Field-axiom tests for <see cref="GaloisField"/> across all five singletons. The reference
/// multiplication used to cross-check is an independent carry-less multiply reduced modulo the
/// same primitive polynomial, so a table transcription error would show up as a mismatch.
/// </summary>
public sealed class GaloisFieldTests
{
    // (field, primitive polynomial) pairs, matching the singletons' construction arguments.
    public static TheoryData<string> FieldNames => new() { "Gf16", "Gf64", "Gf256", "Gf1024", "Gf4096" };

    private static (GaloisField Field, int Poly) Resolve(string name) => name switch
    {
        "Gf16" => (GaloisField.Gf16, 0x13),
        "Gf64" => (GaloisField.Gf64, 0x43),
        "Gf256" => (GaloisField.Gf256, 0x12D),
        "Gf1024" => (GaloisField.Gf1024, 0x409),
        "Gf4096" => (GaloisField.Gf4096, 0x1069),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown field."),
    };

    // Carry-less multiply of two elements, reduced modulo the primitive polynomial — the field
    // multiplication written out directly, with no exp/log tables.
    private static int ReferenceMultiply(int a, int b, int size, int poly)
    {
        var result = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) result ^= a;
            b >>= 1;
            a <<= 1;
            if ((a & size) != 0) a ^= poly;
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void Multiply_byOne_isIdentity(string name)
    {
        var (field, _) = Resolve(name);
        for (var a = 0; a < field.Size; a++)
            Assert.Equal(a, field.Multiply(a, 1));
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void Multiply_byZero_isZero(string name)
    {
        var (field, _) = Resolve(name);
        for (var a = 0; a < field.Size; a++)
        {
            Assert.Equal(0, field.Multiply(a, 0));
            Assert.Equal(0, field.Multiply(0, a));
        }
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void Multiply_byInverse_isOne_forEveryNonZeroElement(string name)
    {
        var (field, _) = Resolve(name);
        for (var a = 1; a < field.Size; a++)
            Assert.Equal(1, field.Multiply(a, field.Inverse(a)));
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void ExpAndLog_roundTrip_overTheWholeField(string name)
    {
        var (field, _) = Resolve(name);
        var order = field.Size - 1;

        // Log(Exp(i)) == i for every exponent in one full period.
        for (var i = 0; i < order; i++)
            Assert.Equal(i, field.Log(field.Exp(i)));

        // Exp(Log(x)) == x for every non-zero element.
        for (var x = 1; x < field.Size; x++)
            Assert.Equal(x, field.Exp(field.Log(x)));
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void Exp_isPeriodic_withPeriodSizeMinusOne(string name)
    {
        var (field, _) = Resolve(name);
        var order = field.Size - 1;
        Assert.Equal(1, field.Exp(0));
        Assert.Equal(1, field.Exp(order));
        Assert.Equal(field.Exp(1), field.Exp(order + 1));
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void Multiply_matchesIndependentReference(string name)
    {
        var (field, poly) = Resolve(name);
        var size = field.Size;

        // Full cross-product for the small fields; a strided sample for the two large ones so
        // the test stays fast while still touching every left operand.
        var step = size <= 256 ? 1 : size / 256;
        for (var a = 0; a < size; a++)
            for (var b = 0; b < size; b += step)
                Assert.Equal(ReferenceMultiply(a, b, size, poly), field.Multiply(a, b));
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void Multiply_distributesOverAddition(string name)
    {
        var (field, _) = Resolve(name);
        var size = field.Size;

        // a*(b + c) == a*b + a*c, where addition is XOR. Spot-checked on a spread of triples.
        int[] samples = [0, 1, 2, 3, 5, 7, size / 3, size / 2, size - 2, size - 1];
        foreach (var a in samples)
            foreach (var b in samples)
                foreach (var c in samples)
                {
                    var lhs = field.Multiply(a, b ^ c);
                    var rhs = field.Multiply(a, b) ^ field.Multiply(a, c);
                    Assert.Equal(rhs, lhs);
                }
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public void Multiply_isAssociative_spotChecks(string name)
    {
        var (field, _) = Resolve(name);
        var size = field.Size;
        int[] samples = [1, 2, 3, 6, size / 4, size / 2, size - 1];
        foreach (var a in samples)
            foreach (var b in samples)
                foreach (var c in samples)
                    Assert.Equal(
                        field.Multiply(field.Multiply(a, b), c),
                        field.Multiply(a, field.Multiply(b, c)));
    }

    [Fact]
    public void Size_reportsEachField()
    {
        Assert.Equal(16, GaloisField.Gf16.Size);
        Assert.Equal(64, GaloisField.Gf64.Size);
        Assert.Equal(256, GaloisField.Gf256.Size);
        Assert.Equal(1024, GaloisField.Gf1024.Size);
        Assert.Equal(4096, GaloisField.Gf4096.Size);
    }

    [Fact]
    public void Gf256_usesDataMatrixPolynomial_notQrPolynomial()
    {
        // Data Matrix / Aztec-8 use 0x12D; QR uses 0x11D. The two GF(256) fields differ: element
        // 2^7 = 128, doubled, reduces by a different polynomial, so 2^8 differs between them.
        // Under 0x12D, alpha^8 = 0x2D; under 0x11D it would be 0x1D. Confirm we built 0x12D's.
        Assert.Equal(0x2D, GaloisField.Gf256.Exp(8));
    }

    [Fact]
    public void Log_ofZero_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GaloisField.Gf256.Log(0));
    }

    [Fact]
    public void Constructor_rejectsNonPowerOfTwoSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GaloisField(255, 0x11D));
    }

    [Fact]
    public void Constructor_rejectsFieldSizeBelowFour()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GaloisField(2, 0x7));
    }

    [Fact]
    public void Constructor_rejectsNonPrimitivePolynomial()
    {
        // x^4 + x^3 + x^2 + x + 1 (0x1F) is irreducible over GF(2) but not primitive: its root's
        // multiplicative order is 5, not 15, so the exponent cycle closes early. 0x13 (x^4 + x +
        // 1, Gf16's actual polynomial) is primitive and must keep working.
        Assert.Throws<ArgumentException>(() => new GaloisField(16, 0x1F));
    }

    [Theory]
    [InlineData(4, 3)]   // Gf16
    [InlineData(6, 3)]   // Gf64
    [InlineData(10, 9)]  // Gf1024
    [InlineData(12, 105)] // Gf4096
    public void Exp_anchorValue_matchesIndependentlyComputedTable(int power, int expected)
    {
        var field = power switch
        {
            4 => GaloisField.Gf16,
            6 => GaloisField.Gf64,
            10 => GaloisField.Gf1024,
            12 => GaloisField.Gf4096,
            _ => throw new ArgumentOutOfRangeException(nameof(power)),
        };

        Assert.Equal(expected, field.Exp(power));
    }

    [Fact]
    public void Exp_negativeExponent_wrapsToEquivalentPositiveExponent()
    {
        Assert.Equal(GaloisField.Gf256.Exp(254), GaloisField.Gf256.Exp(-1));
    }
}
