// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;

namespace VellumPdf.Signing;

/// <summary>
/// Converts an ECDSA signature between the raw IEEE P1363 format some cloud KMS
/// providers return and the ASN.1 DER format CMS requires.
/// </summary>
public static class EcdsaSignatureConverter
{
    /// <summary>
    /// Converts a raw IEEE P1363 (<c>r || s</c>) ECDSA signature — the format returned by
    /// Azure Key Vault's <c>Sign</c> operation for <c>ES256</c>/<c>ES384</c>/<c>ES512</c>,
    /// per RFC 7518 §3.4 — to the DER <c>ECDSA-Sig-Value</c> sequence that
    /// <see cref="IExternalSigner.SignAsync"/> must return for an EC certificate.
    /// </summary>
    /// <param name="rawSignature">
    /// The raw signature: <c>r</c> and <c>s</c>, each a fixed-length unsigned big-endian
    /// integer matching the curve's field size, concatenated with no separator.
    /// </param>
    /// <returns>The DER encoding of <c>ECDSA-Sig-Value ::= SEQUENCE { r INTEGER, s INTEGER }</c>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="rawSignature"/> is empty or has an odd length (so <c>r</c> and
    /// <c>s</c> cannot be split into two equal halves).
    /// </exception>
    public static byte[] RawToDer(ReadOnlySpan<byte> rawSignature)
    {
        if (rawSignature.Length == 0 || rawSignature.Length % 2 != 0)
            throw new ArgumentException(
                "Raw ECDSA signature must be a nonzero, even number of bytes (r and s of equal length).",
                nameof(rawSignature));

        var half = rawSignature.Length / 2;
        var r = TrimLeadingZeros(rawSignature[..half]);
        var s = TrimLeadingZeros(rawSignature[half..]);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteIntegerUnsigned(r);
            writer.WriteIntegerUnsigned(s);
        }
        return writer.Encode();
    }

    // Strips redundant leading zero bytes so AsnWriter.WriteIntegerUnsigned never sees an
    // over-padded value (it throws when the 9 most significant bits are all unset). Leaves
    // at least one byte, and WriteIntegerUnsigned itself adds back a single 0x00 pad when
    // the trimmed value's high bit is set, keeping the DER INTEGER unsigned and minimal.
    private static ReadOnlySpan<byte> TrimLeadingZeros(ReadOnlySpan<byte> value)
    {
        var i = 0;
        while (i < value.Length - 1 && value[i] == 0)
            i++;
        return value[i..];
    }
}
