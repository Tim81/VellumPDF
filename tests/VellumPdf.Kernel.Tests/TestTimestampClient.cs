// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// An in-process <see cref="ITimestampClient"/> that issues real RFC 3161
/// <c>TimeStampToken</c> objects locally without any network calls.
/// Uses only BCL types — no additional package dependencies.
/// </summary>
internal sealed class TestTimestampClient : ITimestampClient
{
    // OID 1.3.6.1.5.5.7.3.8 — id-kp-timeStamping
    private const string IdKpTimeStampingOid = "1.3.6.1.5.5.7.3.8";

    // OID 1.2.840.113549.1.9.16.1.4 — id-ct-TSTInfo
    private const string IdCtTstInfoOid = "1.2.840.113549.1.9.16.1.4";

    // SHA-256 algorithm OID
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    // Fixed policy OID used by this test TSA.
    private const string TestPolicyOid = "1.3.6.1.4.1.99999.1";

    // OID 1.2.840.113549.1.9.16.2.47 — id-smime-aa-signingCertificateV2 (RFC 5035)
    // TryDecode requires either this or id-smime-aa-signingCertificate (v1) in SignedAttributes.
    private const string IdSmimeAaSigningCertV2Oid = "1.2.840.113549.1.9.16.2.47";

    private readonly DateTimeOffset _genTime;
    private readonly X509Certificate2 _tsaCert;

    /// <summary>
    /// Initialises the test TSA client.
    /// </summary>
    /// <param name="genTime">
    /// The fixed <c>genTime</c> to embed in every token.
    /// Pass a pinned value to keep tests deterministic.
    /// </param>
    public TestTimestampClient(DateTimeOffset genTime)
    {
        _genTime = genTime;
        _tsaCert = CreateTsaCertificate(genTime);
    }

    /// <inheritdoc/>
    public byte[] GetTimestampToken(ReadOnlySpan<byte> messageDigest, HashAlgorithmName hashAlgorithm)
    {
        // Normalise hash algorithm name — FriendlyName may be lowercase (e.g. "sha256").
        var normalisedAlg = NormaliseHashAlgorithm(hashAlgorithm);
        var tstInfoDer = BuildTstInfo(messageDigest, normalisedAlg);
        return WrapInSignedCms(tstInfoDer);
    }

    // ── Hash algorithm helpers ─────────────────────────────────────────────────

    private static HashAlgorithmName NormaliseHashAlgorithm(HashAlgorithmName alg)
    {
        var name = alg.Name ?? string.Empty;
        return name.ToUpperInvariant() switch
        {
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA512" => HashAlgorithmName.SHA512,
            "SHA1" => HashAlgorithmName.SHA1,
            _ => throw new NotSupportedException(
                $"Hash algorithm '{name}' is not supported by TestTimestampClient."),
        };
    }

    // ── TSTInfo builder ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the DER-encoded TSTInfo structure per RFC 3161 §2.4.2.
    /// </summary>
    private byte[] BuildTstInfo(ReadOnlySpan<byte> messageDigest, HashAlgorithmName hashAlgorithm)
    {
        // Resolve the hash algorithm OID.
        var hashOid = hashAlgorithm.Name switch
        {
            "SHA256" => Sha256Oid,
            _ => throw new NotSupportedException(
                $"Hash algorithm '{hashAlgorithm.Name}' is not supported by TestTimestampClient."),
        };

        // genTime must be UTC, format yyyyMMddHHmmssZ (no fractional seconds).
        var utc = _genTime.UtcDateTime;

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) // TSTInfo SEQUENCE
        {
            // version INTEGER { v1(1) }
            writer.WriteInteger(1);

            // policy OBJECT IDENTIFIER
            writer.WriteObjectIdentifier(TestPolicyOid);

            // messageImprint MessageImprint ::= SEQUENCE {
            //   hashAlgorithm  AlgorithmIdentifier,
            //   hashedMessage  OCTET STRING }
            using (writer.PushSequence())
            {
                // AlgorithmIdentifier ::= SEQUENCE { algorithm OID, parameters ANY OPTIONAL }
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(hashOid);
                    writer.WriteNull(); // SHA-256 parameters = NULL
                }
                writer.WriteOctetString(messageDigest);
            }

            // serialNumber INTEGER (fixed value for determinism)
            writer.WriteInteger(1);

            // genTime GeneralizedTime
            writer.WriteGeneralizedTime(utc, omitFractionalSeconds: true);
        }

        return writer.Encode();
    }

    // ── SignedCms wrapper ──────────────────────────────────────────────────────

    /// <summary>
    /// Wraps the <paramref name="tstInfoDer"/> in a <see cref="SignedCms"/> with content
    /// type <c>id-ct-TSTInfo</c>, signed by the internal TSA certificate.
    ///
    /// Adds the <c>id-smime-aa-signingCertificateV2</c> signed attribute (RFC 5035)
    /// required by <see cref="Rfc3161TimestampToken.TryDecode"/> to locate the signer cert.
    /// </summary>
    private byte[] WrapInSignedCms(byte[] tstInfoDer)
    {
        var contentInfo = new ContentInfo(new Oid(IdCtTstInfoOid), tstInfoDer);
        var cms = new SignedCms(contentInfo);

        var cmsSigner = new CmsSigner(_tsaCert)
        {
            DigestAlgorithm = new Oid(Sha256Oid),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };

        // Add id-smime-aa-signingCertificateV2 signed attribute.
        // This is required: Rfc3161TimestampToken.TryDecode returns false if neither
        // signingCertificate nor signingCertificateV2 is present in the SignedAttributes.
        //
        // SigningCertificateV2 ::= SEQUENCE {
        //   certs SEQUENCE OF ESSCertIDv2,
        //   policies [0] ... OPTIONAL }
        // ESSCertIDv2 ::= SEQUENCE {
        //   hashAlgorithm AlgorithmIdentifier OPTIONAL,  -- default SHA-256 means omit
        //   certHash OCTET STRING,
        //   issuerSerial IssuerSerial OPTIONAL }
        var certHash = SHA256.HashData(_tsaCert.RawData);
        var attrWriter = new AsnWriter(AsnEncodingRules.DER);
        using (attrWriter.PushSequence()) // SigningCertificateV2
        {
            using (attrWriter.PushSequence()) // certs
            {
                using (attrWriter.PushSequence()) // ESSCertIDv2
                {
                    // hashAlgorithm omitted (default = SHA-256 per RFC 5035 §4)
                    attrWriter.WriteOctetString(certHash);
                    // issuerSerial omitted (optional)
                }
            }
        }
        var signingCertV2Der = attrWriter.Encode();
        cmsSigner.SignedAttributes.Add(
            new AsnEncodedData(new Oid(IdSmimeAaSigningCertV2Oid), signingCertV2Der));

        cms.ComputeSignature(cmsSigner, silent: true);
        return cms.Encode();
    }

    // ── TSA certificate factory ────────────────────────────────────────────────

    /// <summary>
    /// Creates a self-signed RSA-2048 certificate with the <c>id-kp-timeStamping</c>
    /// Extended Key Usage extension (critical), suitable for issuing RFC 3161 tokens.
    /// </summary>
    /// <param name="genTime">
    /// The genTime that will be embedded in tokens. The certificate's <c>NotBefore</c> is
    /// set one day before <paramref name="genTime"/> so that
    /// <see cref="Rfc3161TimestampToken.TryDecode"/>'s certificate-validity check passes.
    /// </param>
    private static X509Certificate2 CreateTsaCertificate(DateTimeOffset genTime)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Test TSA",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Critical Extended Key Usage: id-kp-timeStamping (OID 1.3.6.1.5.5.7.3.8)
        var eku = new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(IdKpTimeStampingOid) },
            critical: true);
        req.CertificateExtensions.Add(eku);

        // NotBefore must be strictly before genTime; NotAfter must be after genTime.
        return req.CreateSelfSigned(
            genTime.AddDays(-1),
            genTime.AddYears(1));
    }
}
