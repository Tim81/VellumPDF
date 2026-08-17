// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;

namespace VellumPdf.Signing;

/// <summary>
/// Writes a certificate serial number as a minimally-encoded ASN.1 INTEGER. Shared by
/// <see cref="ExternalSignerCms"/> (the CMS <c>SignerInfo.IssuerAndSerialNumber</c>) and
/// <see cref="HttpRevocationClient"/> (the OCSP request's <c>CertID.serialNumber</c>), both
/// of which read the serial from an <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/>
/// this library did not issue and cannot assume is minimally encoded.
/// </summary>
internal static class Asn1SerialNumber
{
    /// <summary>
    /// Writes <paramref name="serial"/> — a big-endian, signed two's-complement value as
    /// carried in the certificate — as an ASN.1 INTEGER, normalizing it to DER's minimal
    /// form first.
    /// </summary>
    /// <remarks>
    /// <c>X509Certificate2.SerialNumberBytes</c> returns the certificate's raw serial
    /// content octets verbatim, and .NET's X.509 parser accepts a serial with a redundant
    /// leading pad byte even though DER requires the shortest possible two's-complement
    /// encoding. <see cref="AsnWriter"/>'s <c>WriteInteger</c> enforces that DER rule
    /// strictly and throws on a non-minimal encoding, so a mis-issued certificate carrying
    /// such a pad needs normalizing first.
    /// </remarks>
    internal static void Write(AsnWriter writer, byte[] serial)
    {
        if (serial.Length == 0)
        {
            writer.WriteInteger(0);
            return;
        }

        writer.WriteInteger(NormalizeToMinimalTwosComplement(serial));
    }

    // A genuine negative serial — a leading 0xFF run whose next byte also has its high bit
    // set — is not padding and must round-trip unchanged; only a lead byte that does not
    // change the represented value is stripped.
    private static byte[] NormalizeToMinimalTwosComplement(byte[] value)
    {
        var start = 0;
        while (start < value.Length - 1)
        {
            var current = value[start];
            var nextHighBitSet = (value[start + 1] & 0x80) != 0;
            var isRedundantZeroPad = current == 0x00 && !nextHighBitSet;
            var isRedundantOnesPad = current == 0xFF && nextHighBitSet;
            if (!isRedundantZeroPad && !isRedundantOnesPad)
                break;
            start++;
        }

        return start == 0 ? value : value[start..];
    }
}
