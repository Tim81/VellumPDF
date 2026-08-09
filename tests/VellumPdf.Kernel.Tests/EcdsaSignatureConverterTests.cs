// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for <see cref="EcdsaSignatureConverter.RawToDer"/>: converting a raw IEEE P1363
/// (<c>r || s</c>) ECDSA signature — the format Azure Key Vault's ECDSA sign operation
/// returns — to the DER <c>ECDSA-Sig-Value</c> sequence CMS requires.
/// </summary>
public sealed class EcdsaSignatureConverterTests
{
    [Theory]
    [InlineData("nistP256")]
    [InlineData("nistP384")]
    [InlineData("nistP521")]
    public void RawToDer_roundTrips_realEcdsaSignature(string curveName)
    {
        var curve = curveName switch
        {
            "nistP256" => ECCurve.NamedCurves.nistP256,
            "nistP384" => ECCurve.NamedCurves.nistP384,
            _ => ECCurve.NamedCurves.nistP521,
        };
        using var ecdsa = ECDsa.Create(curve);

        var digest = SHA256.HashData("VellumPdf ECDSA raw-to-DER round trip"u8);
        var raw = ecdsa.SignHash(digest); // .NET's ECDsa produces raw IEEE P1363 (r || s) by default.

        var der = EcdsaSignatureConverter.RawToDer(raw);

        Assert.True(ecdsa.VerifyHash(digest, der, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void RawToDer_throws_onOddLength()
    {
        Assert.Throws<ArgumentException>(() => EcdsaSignatureConverter.RawToDer(new byte[3]));
    }

    [Fact]
    public void RawToDer_throws_onEmpty()
    {
        Assert.Throws<ArgumentException>(() => EcdsaSignatureConverter.RawToDer(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void RawToDer_preserves_numeric_r_and_s_values()
    {
        // r and s each carry a leading zero pad byte in the fixed-width raw encoding,
        // exercising the trim/re-pad logic without depending on a random signature
        // happening to produce one.
        const int half = 32;
        var raw = new byte[half * 2];
        raw[1] = 0x80;
        raw[half + 1] = 0x42;

        var expectedR = new BigInteger(raw.AsSpan(0, half), isUnsigned: true, isBigEndian: true);
        var expectedS = new BigInteger(raw.AsSpan(half, half), isUnsigned: true, isBigEndian: true);

        var der = EcdsaSignatureConverter.RawToDer(raw);

        var reader = new AsnReader(der, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var r = sequence.ReadInteger();
        var s = sequence.ReadInteger();
        sequence.ThrowIfNotEmpty();
        reader.ThrowIfNotEmpty();

        Assert.Equal(expectedR, r);
        Assert.Equal(expectedS, s);
    }
}
