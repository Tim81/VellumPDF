// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Builds an <see cref="X509Certificate2"/> whose <c>serialNumber</c> carries a redundant leading
/// pad byte — the mis-issued encoding at the centre of issue #167.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CertificateRequest"/> cannot produce one: it normalizes whatever serial it is given,
/// so <c>00 00 7F 01</c> in comes back out as <c>7F 01</c>. The certificate therefore has to be
/// assembled at the DER level, which is what this does — re-emitting the TBS with a padded serial
/// and letting the enclosing SEQUENCE lengths be recomputed by <see cref="AsnWriter"/>.
/// </para>
/// <para>
/// The result is not self-consistent: its signature covers the original TBS, not the patched one.
/// That does not matter for what these tests check. .NET's X.509 parser does not verify the
/// signature when loading, and the code under test reads only
/// <c>SerialNumberBytes</c> — the point is to exercise the encoders' reaction to the serial, and
/// both this library's writer and the BCL's reject it while reading it back is fine.
/// </para>
/// </remarks>
internal static class NonMinimalSerialCertificate
{
    /// <summary>
    /// Returns a certificate whose serial content octets are <paramref name="paddedSerial"/>
    /// verbatim, defaulting to a redundant <c>0x00</c> ahead of a byte with a clear high bit.
    /// </summary>
    internal static X509Certificate2 Create(byte[]? paddedSerial = null)
    {
        paddedSerial ??= [0x00, 0x01, 0x02, 0x03, 0x04];

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=VellumPdf Non-Minimal Serial", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var original = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var patched = X509CertificateLoader.LoadCertificate(PatchSerial(original.RawData, paddedSerial));

        // The private key has to come back, or the certificate is rejected for lacking one before
        // anything looks at the serial — and a caller hitting this in production would certainly
        // have the key. Patching the serial does not touch subjectPublicKeyInfo, so the original
        // RSA key still matches; CopyWithPrivateKey clones the key material, so disposing the
        // original afterwards is safe.
        return patched.CopyWithPrivateKey(rsa);
    }

    /// <summary>
    /// Rewrites <paramref name="certificateDer"/>'s <c>TBSCertificate.serialNumber</c> to
    /// <paramref name="serial"/>, preserving every other field byte-for-byte.
    /// </summary>
    private static byte[] PatchSerial(byte[] certificateDer, byte[] serial)
    {
        // Certificate ::= SEQUENCE { tbsCertificate TBSCertificate,
        //                            signatureAlgorithm AlgorithmIdentifier,
        //                            signature BIT STRING }
        var certificate = new AsnReader(certificateDer, AsnEncodingRules.DER).ReadSequence();
        var tbsEncoded = certificate.ReadEncodedValue();
        var signatureAlgorithm = certificate.ReadEncodedValue();
        var signature = certificate.ReadEncodedValue();

        // TBSCertificate ::= SEQUENCE { [0] version DEFAULT v1, serialNumber CertificateSerialNumber, … }
        var tbs = new AsnReader(tbsEncoded, AsnEncodingRules.DER).ReadSequence();
        var versionTag = new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true);
        ReadOnlyMemory<byte>? version = tbs.PeekTag() == versionTag ? tbs.ReadEncodedValue() : null;
        tbs.ReadEncodedValue(); // the original serialNumber, discarded

        // Everything from `signature` onward is copied verbatim.
        var remainder = new List<ReadOnlyMemory<byte>>();
        while (tbs.HasData)
            remainder.Add(tbs.ReadEncodedValue());

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) // Certificate
        {
            using (writer.PushSequence()) // TBSCertificate
            {
                if (version is { } v)
                    writer.WriteEncodedValue(v.Span);

                // WriteInteger would reject the non-minimal encoding, which is the whole point of
                // the fixture — so the INTEGER is emitted as a raw TLV instead.
                writer.WriteEncodedValue(EncodeRawInteger(serial));

                foreach (var field in remainder)
                    writer.WriteEncodedValue(field.Span);
            }

            writer.WriteEncodedValue(signatureAlgorithm.Span);
            writer.WriteEncodedValue(signature.Span);
        }

        return writer.Encode();
    }

    /// <summary>
    /// Hand-encodes an ASN.1 INTEGER TLV around <paramref name="content"/> without the
    /// minimal-encoding check <see cref="AsnWriter.WriteInteger(ReadOnlySpan{byte})"/> enforces.
    /// </summary>
    private static byte[] EncodeRawInteger(byte[] content)
    {
        // Certificate serials are short enough that the definite short form (length < 128) always
        // applies; a longer one would need the multi-byte length form.
        Assert.True(content.Length < 0x80, "Test fixture only encodes short-form lengths.");

        var tlv = new byte[content.Length + 2];
        tlv[0] = 0x02; // INTEGER
        tlv[1] = (byte)content.Length;
        content.CopyTo(tlv, 2);
        return tlv;
    }
}
