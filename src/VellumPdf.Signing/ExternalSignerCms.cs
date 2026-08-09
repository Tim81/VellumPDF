// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace VellumPdf.Signing;

/// <summary>
/// Hand-assembles a detached CMS SignedData/SignerInfo (RFC 5652) around a signature
/// produced by an <see cref="IExternalSigner"/>. <see cref="CmsSigner"/>/<see cref="SignedCms"/>
/// only support a synchronous, in-process private key, so this bypasses them for the
/// signature-computation step and reuses <see cref="SignedCms"/> only to validate the
/// result and to attach unsigned attributes afterward.
/// </summary>
internal static class ExternalSignerCms
{
    // id-data (RFC 5652 §5.2) — the eContentType for a detached signature; there is no
    // CMS-encapsulated content, only the /ByteRange bytes hashed and signed externally.
    private const string IdData = "1.2.840.113549.1.7.1";

    // id-signedData (RFC 5652 §5.1).
    private const string IdSignedData = "1.2.840.113549.1.7.2";

    // PKCS#9 attribute type OIDs (RFC 2985; imported into CMS by RFC 5652 §11).
    private const string IdContentType = "1.2.840.113549.1.9.3";
    private const string IdMessageDigest = "1.2.840.113549.1.9.4";
    private const string IdSigningTime = "1.2.840.113549.1.9.5";

    // RSA PKCS#1 v1.5 signature algorithm OIDs (RFC 8017 Appendix C).
    private const string Sha256WithRsaEncryption = "1.2.840.113549.1.1.11";
    private const string Sha384WithRsaEncryption = "1.2.840.113549.1.1.12";
    private const string Sha512WithRsaEncryption = "1.2.840.113549.1.1.13";

    // ECDSA-with-SHA2 signature algorithm OIDs (RFC 5758 §3.2). RFC 5758 requires the
    // AlgorithmIdentifier parameters field to be omitted for these.
    private const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";
    private const string EcdsaWithSha384 = "1.2.840.10045.4.3.3";
    private const string EcdsaWithSha512 = "1.2.840.10045.4.3.4";

    /// <summary>
    /// Builds a detached CMS SignedData over <paramref name="signedContent"/> using
    /// <see cref="PdfSignatureSettings.ExternalSigner"/>, and returns it decoded into a
    /// <see cref="SignedCms"/> so the caller can attach unsigned attributes (an RFC 3161
    /// timestamp token) the same way it does for the local-key signing path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The assembled CMS structure could not be decoded (an internal encoding bug in this
    /// method, not the caller's fault), or it decoded but failed its own signature
    /// verification — almost always because <see cref="IExternalSigner.SignAsync"/> returned
    /// a signature in the wrong format (see <see cref="IExternalSigner"/>'s documentation for
    /// the exact format expected).
    /// </exception>
    internal static async Task<SignedCms> BuildAsync(
        byte[] signedContent,
        PdfSignatureSettings settings,
        CancellationToken cancellationToken)
    {
        var signer = settings.ExternalSigner!;
        var certificate = settings.Certificate;
        var hashAlgorithm = signer.HashAlgorithm;
        var signingTime = settings.SigningTime ?? DateTimeOffset.UtcNow;

        var digestOid = DigestAlgorithmOid(hashAlgorithm);
        var messageDigest = HashData(hashAlgorithm, signedContent);

        // ── Signed attributes: contentType, messageDigest, signingTime ─────────
        var attributes = new[]
        {
            EncodeAttribute(IdContentType, w => w.WriteObjectIdentifier(IdData)),
            EncodeAttribute(IdMessageDigest, w => w.WriteOctetString(messageDigest)),
            EncodeAttribute(IdSigningTime, w => WriteTime(w, signingTime)),
        };

        // RFC 5652 §5.4: the digest is computed over the attributes under a universal
        // SET OF tag. The [0] IMPLICIT tag used when embedding in SignerInfo below is
        // explicitly NOT used for this — hashing the [0]-tagged bytes instead produces a
        // signature that no verifier will accept.
        var signedAttrsForDigest = EncodeSetOf(attributes, tag: null);
        var digestToSign = HashData(hashAlgorithm, signedAttrsForDigest);

        var signature = await signer.SignAsync(digestToSign, cancellationToken).ConfigureAwait(false);

        var signedAttrsForEmbed = EncodeSetOf(attributes, ContextTag(0));

        using var ecdsaPublicKey = certificate.GetECDsaPublicKey();
        var isEc = ecdsaPublicKey is not null;
        var signatureAlgorithmOid = SignatureAlgorithmOid(isEc, hashAlgorithm);
        var chainCertificates = BuildCertificateChain(certificate);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) // ContentInfo
        {
            writer.WriteObjectIdentifier(IdSignedData);
            using (writer.PushSequence(ContextTag(0))) // [0] EXPLICIT content
            using (writer.PushSequence()) // SignedData
            {
                writer.WriteInteger(1); // version
                using (writer.PushSetOf()) // digestAlgorithms
                {
                    WriteAlgorithmIdentifier(writer, digestOid, includeNullParams: true);
                }
                using (writer.PushSequence()) // encapContentInfo (no eContent — detached)
                {
                    writer.WriteObjectIdentifier(IdData);
                }
                using (writer.PushSetOf(ContextTag(0))) // [0] certificates
                {
                    foreach (var cert in chainCertificates)
                        writer.WriteEncodedValue(cert);
                }
                using (writer.PushSetOf()) // signerInfos
                using (writer.PushSequence()) // SignerInfo
                {
                    writer.WriteInteger(1); // version (sid = issuerAndSerialNumber)
                    using (writer.PushSequence()) // IssuerAndSerialNumber
                    {
                        writer.WriteEncodedValue(certificate.IssuerName.RawData);
                        WriteSerialNumber(writer, certificate.SerialNumberBytes.ToArray());
                    }
                    WriteAlgorithmIdentifier(writer, digestOid, includeNullParams: true);
                    writer.WriteEncodedValue(signedAttrsForEmbed);
                    WriteAlgorithmIdentifier(writer, signatureAlgorithmOid, includeNullParams: !isEc);
                    writer.WriteOctetString(signature);
                }
            }
        }

        var cms = new SignedCms(new ContentInfo(signedContent), detached: true);
        try
        {
            cms.Decode(writer.Encode());
        }
        catch (CryptographicException ex)
        {
            // A decode failure means the bytes built above are not well-formed CMS — a bug in
            // this method's ASN.1 construction, not something the caller's IExternalSigner did.
            throw new InvalidOperationException(
                "ExternalSignerCms built a CMS structure that could not be decoded. This is an " +
                "internal encoding bug in ExternalSignerCms, not a problem with the external " +
                "signer's output.",
                ex);
        }

        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The external signer produced a signature that failed verification. Check " +
                "that IExternalSigner.SignAsync returned the signature in the format " +
                "SignerInfo.signature requires — see the IExternalSigner.SignAsync " +
                "documentation (RSA: PKCS#1 v1.5 bytes; EC: the DER ECDSA-Sig-Value " +
                "sequence, not raw r || s — see EcdsaSignatureConverter).",
                ex);
        }

        return cms;
    }

    private static Asn1Tag ContextTag(int tagValue) => new(TagClass.ContextSpecific, tagValue, isConstructed: true);

    private static void WriteAlgorithmIdentifier(AsnWriter writer, string oid, bool includeNullParams)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            if (includeNullParams)
                writer.WriteNull();
        }
    }

    private static byte[] EncodeAttribute(string attributeTypeOid, Action<AsnWriter> writeValue)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(attributeTypeOid);
            using (writer.PushSetOf())
                writeValue(writer);
        }
        return writer.Encode();
    }

    private static byte[] EncodeSetOf(byte[][] elements, Asn1Tag? tag)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSetOf(tag))
        {
            foreach (var element in elements)
                writer.WriteEncodedValue(element);
        }
        return writer.Encode();
    }

    private static void WriteTime(AsnWriter writer, DateTimeOffset time)
    {
        // CMS SigningTime (RFC 2985) is the same Time CHOICE X.509 Validity uses:
        // UTCTime for 1950-2049, GeneralizedTime otherwise.
        if (time.Year is >= 1950 and <= 2049)
            writer.WriteUtcTime(time);
        else
            writer.WriteGeneralizedTime(time);
    }

    // Mirrors HttpRevocationClient.WriteSerialNumber: the serial number is already a
    // big-endian, signed two's-complement value as carried in the certificate, so it is
    // written with WriteInteger (not WriteIntegerUnsigned).
    private static void WriteSerialNumber(AsnWriter writer, byte[] serial)
    {
        if (serial.Length == 0)
        {
            writer.WriteInteger(0);
            return;
        }

        writer.WriteInteger(serial);
    }

    private static List<byte[]> BuildCertificateChain(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.ChainPolicy.VerificationTime = certificate.NotBefore;

        var certificates = new List<byte[]>();
        if (chain.Build(certificate))
        {
            foreach (var element in chain.ChainElements)
                certificates.Add(element.Certificate.RawData);
        }
        else
        {
            certificates.Add(certificate.RawData);
        }
        return certificates;
    }

    private static string DigestAlgorithmOid(HashAlgorithmName hashAlgorithm) => hashAlgorithm.Name switch
    {
        "SHA256" => "2.16.840.1.101.3.4.2.1",
        "SHA384" => "2.16.840.1.101.3.4.2.2",
        "SHA512" => "2.16.840.1.101.3.4.2.3",
        _ => throw new NotSupportedException(
            $"IExternalSigner.HashAlgorithm '{hashAlgorithm.Name}' is not supported. Use SHA256, SHA384, or SHA512."),
    };

    private static string SignatureAlgorithmOid(bool isEc, HashAlgorithmName hashAlgorithm) => (isEc, hashAlgorithm.Name) switch
    {
        (true, "SHA256") => EcdsaWithSha256,
        (true, "SHA384") => EcdsaWithSha384,
        (true, "SHA512") => EcdsaWithSha512,
        (false, "SHA256") => Sha256WithRsaEncryption,
        (false, "SHA384") => Sha384WithRsaEncryption,
        (false, "SHA512") => Sha512WithRsaEncryption,
        _ => throw new NotSupportedException(
            $"IExternalSigner.HashAlgorithm '{hashAlgorithm.Name}' is not supported. Use SHA256, SHA384, or SHA512."),
    };

    private static byte[] HashData(HashAlgorithmName hashAlgorithm, ReadOnlySpan<byte> data) => hashAlgorithm.Name switch
    {
        "SHA256" => SHA256.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        _ => throw new NotSupportedException(
            $"IExternalSigner.HashAlgorithm '{hashAlgorithm.Name}' is not supported. Use SHA256, SHA384, or SHA512."),
    };
}
