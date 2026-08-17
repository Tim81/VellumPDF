// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VellumPdf.Signing;

/// <summary>
/// Encodes the ESS <c>signing-certificate-v2</c> signed attribute (RFC 5035 §3), which binds a
/// CMS signature to the one certificate that produced it.
/// </summary>
/// <remarks>
/// <para>
/// Without this attribute, a <c>SignerInfo</c> identifies its signing certificate only by issuer
/// and serial — data an attacker controls when substituting a different certificate that carries
/// the same public key. Hashing the certificate itself into the signed attributes closes that
/// substitution, which is why the CAdES profile that <c>/SubFilter ETSI.CAdES.detached</c> claims
/// requires the attribute to be present (issue #168).
/// </para>
/// <para>
/// Both signing paths encode it through this one method. Two independent encoders is how the
/// non-minimal serial bug in issue #167 came to exist in <c>ExternalSignerCms</c> and
/// <c>HttpRevocationClient</c> at once.
/// </para>
/// </remarks>
internal static class SigningCertificateV2
{
    /// <summary>
    /// <c>id-aa-signingCertificateV2</c> (RFC 5035 §3):
    /// <c>{ iso(1) member-body(2) us(840) rsadsi(113549) pkcs(1) pkcs9(9) smime(16) id-aa(2) 47 }</c>.
    /// </summary>
    internal const string AttributeOid = "1.2.840.113549.1.9.16.2.47";

    /// <summary>
    /// Encodes the attribute's value — a DER <c>SigningCertificateV2</c> — for
    /// <paramref name="certificate"/>, hashed with <paramref name="hashAlgorithm"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RFC 5035 Appendix A defines the structure written here:
    /// </para>
    /// <code>
    /// SigningCertificateV2 ::= SEQUENCE {
    ///     certs         SEQUENCE OF ESSCertIDv2,
    ///     policies      SEQUENCE OF PolicyInformation OPTIONAL }
    /// ESSCertIDv2 ::= SEQUENCE {
    ///     hashAlgorithm AlgorithmIdentifier DEFAULT {algorithm id-sha256},
    ///     certHash      Hash,                   -- OCTET STRING
    ///     issuerSerial  IssuerSerial OPTIONAL }
    /// IssuerSerial ::= SEQUENCE {
    ///     issuer        GeneralNames,
    ///     serialNumber  CertificateSerialNumber }
    /// </code>
    /// <para>
    /// <c>policies</c> is omitted: it constrains which certificate policies the signer asserts,
    /// which is a signature-policy concern this library does not model. Only one
    /// <c>ESSCertIDv2</c> is written, for the signing certificate — the rest of the chain already
    /// travels in <c>SignedData.certificates</c>, and RFC 5035 requires only the signing
    /// certificate's own reference to come first.
    /// </para>
    /// </remarks>
    internal static byte[] Encode(X509Certificate2 certificate, HashAlgorithmName hashAlgorithm)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSequence()) // SigningCertificateV2
        using (writer.PushSequence()) // certs
        using (writer.PushSequence()) // ESSCertIDv2
        {
            // hashAlgorithm carries DEFAULT id-sha256, and DER forbids encoding a value equal to
            // its DEFAULT — so for SHA-256 the field is omitted rather than written. For SHA-384
            // and SHA-512 it is written with the parameters field absent, per RFC 5754 §2.
            if (hashAlgorithm != HashAlgorithmName.SHA256)
            {
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(Sha2DigestAlgorithm.Oid(hashAlgorithm));
            }

            // certHash is computed over the entire DER-encoded certificate, signature included —
            // not over the TBS portion (RFC 5035 §4).
            writer.WriteOctetString(Sha2DigestAlgorithm.Hash(hashAlgorithm, certificate.RawData));

            // issuerSerial is optional, and RFC 5035 notes it "would normally be present unless
            // the value can be inferred from other information". It is written here because a
            // CAdES verifier is entitled to cross-check it against SignerInfo's own
            // IssuerAndSerialNumber and reject a mismatch. Both are built from the same
            // certificate through the same serial normalization, so they cannot disagree.
            using (writer.PushSequence()) // IssuerSerial
            {
                // GeneralNames ::= SEQUENCE OF GeneralName, and the issuer DN is the
                // directoryName alternative — GeneralName's [4], explicit because Name is a
                // CHOICE. IssuerName.RawData is that Name already DER-encoded.
                using (writer.PushSequence()) // GeneralNames
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
                    writer.WriteEncodedValue(certificate.IssuerName.RawData);

                Asn1SerialNumber.Write(writer, certificate.SerialNumberBytes.Span);
            }
        }

        return writer.Encode();
    }
}
