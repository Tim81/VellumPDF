// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;

namespace VellumPdf.Signing;

/// <summary>
/// Normalizes a certificate serial number to DER's minimal two's-complement form, for the places
/// this library encodes or compares one itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>X509Certificate2.SerialNumberBytes</c> returns the certificate's raw serial content octets
/// verbatim, and .NET's X.509 parser accepts a serial carrying a redundant leading pad byte even
/// though DER requires the shortest possible two's-complement encoding (ITU-T X.690 §8.3.2).
/// </para>
/// <para>
/// That laxity is not peculiar to this library's hand-rolled writer: <see cref="AsnWriter"/>
/// enforces the DER rule strictly and throws, and so does the BCL's own
/// <c>IssuerAndSerialNumberAsn.Encode</c> inside
/// <see cref="System.Security.Cryptography.Pkcs.SignedCms"/>. So a certificate with a
/// non-minimally-encoded serial cannot be signed with by the in-process
/// <see cref="System.Security.Cryptography.Pkcs.CmsSigner"/> path at all, no matter what this type
/// does — see <see cref="PdfCmsSigner"/>, which rejects it up front with an actionable message
/// rather than letting the BCL raise an opaque one from deep in its encoder (issue #167).
/// </para>
/// <para>
/// Where this library writes or compares the serial itself, normalization is both possible and
/// required: <see cref="ExternalSignerCms"/> (the CMS <c>SignerInfo.IssuerAndSerialNumber</c>),
/// <see cref="SigningCertificateV2"/> (the ESS <c>ESSCertIDv2.issuerSerial</c>), and
/// <see cref="HttpRevocationClient"/> (the OCSP <c>CertID.serialNumber</c> it writes, and the CRL
/// <c>revokedCertificates</c> entries it compares against — a real CA's CRL is DER, so its serials
/// are already minimal and a raw-versus-minimal comparison silently never matches).
/// </para>
/// </remarks>
internal static class Asn1SerialNumber
{
    /// <summary>
    /// Writes <paramref name="serial"/> — a big-endian, signed two's-complement value as carried in
    /// the certificate — as an ASN.1 INTEGER, normalizing it to DER's minimal form first.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="serial"/> is empty.</exception>
    internal static void Write(AsnWriter writer, ReadOnlySpan<byte> serial)
        => writer.WriteInteger(Normalize(serial));

    /// <summary>
    /// Returns <paramref name="serial"/> reduced to DER's minimal two's-complement encoding of the
    /// same value, as a slice of the input. A genuinely negative serial (a leading <c>0xFF</c> run
    /// whose following byte also has its high bit set) and a load-bearing <c>0x00</c> pad (one
    /// whose following byte has its high bit set, without which the value would read as negative)
    /// are both returned unchanged.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="serial"/> is empty.</exception>
    internal static ReadOnlySpan<byte> Normalize(ReadOnlySpan<byte> serial)
    {
        // An empty serial is rejected rather than treated as zero: writing 0 would silently name a
        // *different* certificate. X.690 §8.3.1 requires an INTEGER to have at least one content
        // octet, so no certificate that .NET's X.509 parser accepted can produce this — the guard
        // exists so a future caller passing an unvalidated span fails loudly instead of quietly.
        if (serial.IsEmpty)
            throw new ArgumentException(
                "A certificate serial number cannot be empty; ASN.1 requires an INTEGER to have at "
                + "least one content octet (ITU-T X.690 §8.3.1).",
                nameof(serial));

        var start = 0;
        while (start < serial.Length - 1)
        {
            var current = serial[start];
            var nextHighBitSet = (serial[start + 1] & 0x80) != 0;
            var isRedundantZeroPad = current == 0x00 && !nextHighBitSet;
            var isRedundantOnesPad = current == 0xFF && nextHighBitSet;
            if (!isRedundantZeroPad && !isRedundantOnesPad)
                break;
            start++;
        }

        return serial[start..];
    }

    /// <summary>
    /// Whether <paramref name="serial"/> is already in DER's minimal two's-complement form — that
    /// is, whether an encoder that enforces the DER rule will accept it as-is.
    /// </summary>
    internal static bool IsMinimal(ReadOnlySpan<byte> serial)
        => !serial.IsEmpty && Normalize(serial).Length == serial.Length;
}
