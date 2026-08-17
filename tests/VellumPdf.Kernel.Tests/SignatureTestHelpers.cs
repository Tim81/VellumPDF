// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;

namespace VellumPdf.Kernel.Tests;

internal sealed record ContentsInfo(long PosLt, int TokenLen, string HexContent);

/// <summary>
/// The three <c>AlgorithmIdentifier</c>s a CMS <c>SignedData</c> carries for a single-signer
/// detached signature — <c>SignedData.digestAlgorithms</c>'s one entry plus
/// <c>SignerInfo</c>'s own <c>digestAlgorithm</c> and <c>signatureAlgorithm</c> — including
/// whether each one's DER-optional parameters field is present. That presence is a
/// distinction the BCL's <see cref="Oid"/>-based <c>SignerInfo.DigestAlgorithm</c>/
/// <c>SignatureAlgorithm</c> properties don't expose (they surface only the OID string),
/// and <see cref="SignedCms"/> has no managed surface for <c>digestAlgorithms</c> at all.
/// </summary>
internal sealed record SignerInfoAlgorithmIdentifiers(
    string SignedDataDigestOid,
    bool SignedDataDigestHasParameters,
    string DigestOid,
    bool DigestHasParameters,
    string SignatureOid,
    bool SignatureHasParameters);

/// <summary>
/// Shared /ByteRange and /Contents parsing plus BCL <see cref="SignedCms.CheckSignature"/>
/// verification for signed-PDF test assertions. Used by <see cref="SignatureTests"/> and
/// <see cref="ExternalSignerChainTests"/>.
/// </summary>
internal static class SignatureTestHelpers
{
    /// <summary>
    /// Parses /ByteRange and /Contents from signed PDF bytes and performs BCL
    /// <see cref="SignedCms.CheckSignature"/> verification. Throws on any error.
    /// </summary>
    internal static void VerifySignatureOrThrow(byte[] signedBytes)
    {
        var verify = DecodeSignedCms(signedBytes);
        // verifySignatureOnly=true skips certificate chain/trust validation —
        // appropriate for self-signed test certs.
        verify.CheckSignature(verifySignatureOnly: true);
    }

    /// <summary>
    /// Decodes the signature in <paramref name="signedBytes"/> into a detached
    /// <see cref="SignedCms"/> over the content its /ByteRange covers, without verifying it.
    /// </summary>
    internal static SignedCms DecodeSignedCms(byte[] signedBytes)
    {
        var (byteRange, contentsInfo) = ParseSignatureFields(signedBytes);

        // Reconstruct the signed content from the two ByteRange segments.
        var seg0Len = (int)byteRange[1];
        var seg1Start = (int)byteRange[2];
        var seg1Len = (int)byteRange[3];
        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(signedBytes, 0, signedContent, 0, seg0Len);
        Buffer.BlockCopy(signedBytes, seg1Start, signedContent, seg0Len, seg1Len);

        // Decode the /Contents hex string to raw DER bytes.
        // The hex content includes the actual DER bytes followed by zero-padding.
        // SignedCms.Decode uses the DER length field to determine the actual size,
        // so passing the full padded buffer (including trailing zero bytes) is correct.
        var contentsBytes = Convert.FromHexString(contentsInfo.HexContent);

        var cms = new SignedCms(new ContentInfo(signedContent), detached: true);
        cms.Decode(contentsBytes);
        return cms;
    }

    /// <summary>
    /// Parses the /ByteRange array and /Contents hex string from the signed PDF bytes.
    /// Returns the four ByteRange values and the contents token info.
    /// </summary>
    internal static (long[] ByteRange, ContentsInfo Contents) ParseSignatureFields(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);

        // ── Parse /ByteRange [n0 n1 n2 n3] ─────────────────────────────────
        const string byteRangeMarker = "/ByteRange [";
        var brStart = text.IndexOf(byteRangeMarker, StringComparison.Ordinal);
        Assert.True(brStart >= 0, "/ByteRange not found in signed PDF");
        var brBracket = brStart + byteRangeMarker.Length - 1; // index of '['
        var brEnd = text.IndexOf(']', brBracket);
        Assert.True(brEnd >= 0, "/ByteRange closing ']' not found");
        var brContent = text[(brBracket + 1)..brEnd].Trim();
        var brParts = brContent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, brParts.Length);
        var byteRange = brParts.Select(long.Parse).ToArray();

        // ── Parse /Contents <hex…> ──────────────────────────────────────────
        // Locate the '<' of the /Contents hex string by anchoring on /ByteRange:
        // the first '<' after the ByteRange ']' is the /Contents opening angle bracket.
        var posLt = text.IndexOf('<', brEnd);
        Assert.True(posLt >= 0, "/Contents '<' not found after /ByteRange in signed PDF");
        var cEnd = text.IndexOf('>', posLt);
        Assert.True(cEnd >= 0, "/Contents closing '>' not found");
        var hexContent = text[(posLt + 1)..cEnd];
        var tokenLen = 1 + hexContent.Length + 1; // '<' + hex + '>'

        return (byteRange, new ContentsInfo(posLt, tokenLen, hexContent));
    }

    /// <summary>
    /// Decodes /Contents down to <c>SignedData.digestAlgorithms</c>'s one entry and
    /// <c>SignerInfo</c>'s own <c>digestAlgorithm</c>/<c>signatureAlgorithm</c> fields by
    /// walking the DER structure positionally (the field order RFC 5652 §5.1/§5.3 fixes
    /// for <c>SignedData</c>/<c>SignerInfo</c>), reporting each one's OID and whether its
    /// parameters field is present. Both signed PDFs under test always carry a
    /// <c>[0] certificates</c> field, so this doesn't handle its absence.
    /// </summary>
    internal static SignerInfoAlgorithmIdentifiers ExtractSignerInfoAlgorithmIdentifiers(byte[] signedBytes)
    {
        var (_, contents) = ParseSignatureFields(signedBytes);
        var contentsBytes = Convert.FromHexString(contents.HexContent);

        // /Contents is zero-padded past the actual DER length; trim to the single
        // top-level TLV first so the padding isn't misread as more ASN.1 data.
        var topLevel = new AsnReader(contentsBytes, AsnEncodingRules.DER).ReadEncodedValue();

        var contentInfo = new AsnReader(topLevel, AsnEncodingRules.DER).ReadSequence();
        contentInfo.ReadObjectIdentifier(); // contentType (id-signedData)
        var explicitContent = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));

        var signedData = explicitContent.ReadSequence();
        signedData.ReadInteger(); // version
        var (signedDataDigestOid, signedDataDigestHasParameters) = ReadAlgorithmIdentifier(signedData.ReadSetOf()); // digestAlgorithms
        signedData.ReadEncodedValue(); // encapContentInfo
        if (signedData.PeekTag() == new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))
            signedData.ReadEncodedValue(); // certificates [0]

        var signerInfo = signedData.ReadSetOf().ReadSequence();
        signerInfo.ReadInteger(); // version
        signerInfo.ReadEncodedValue(); // sid (IssuerAndSerialNumber)

        var (digestOid, digestHasParameters) = ReadAlgorithmIdentifier(signerInfo);
        signerInfo.ReadEncodedValue(); // signedAttrs [0]
        var (signatureOid, signatureHasParameters) = ReadAlgorithmIdentifier(signerInfo);

        return new SignerInfoAlgorithmIdentifiers(
            signedDataDigestOid, signedDataDigestHasParameters,
            digestOid, digestHasParameters,
            signatureOid, signatureHasParameters);
    }

    private static (string Oid, bool HasParameters) ReadAlgorithmIdentifier(AsnReader parent)
    {
        var algorithmIdentifier = parent.ReadSequence();
        var oid = algorithmIdentifier.ReadObjectIdentifier();
        return (oid, algorithmIdentifier.HasData);
    }

    /// <summary>
    /// Decodes the single <c>ESSCertIDv2</c> from the signature's ESS
    /// <c>signing-certificate-v2</c> signed attribute (RFC 5035), or returns null when the
    /// attribute is absent.
    /// </summary>
    /// <remarks>
    /// Reached through <see cref="SignedCms"/>'s own <c>SignedAttributes</c> rather than a
    /// positional walk: that way the BCL has to have accepted the attribute as part of a
    /// well-formed CMS before these assertions see it, so a structurally broken encoding fails
    /// here rather than being re-parsed by an equally wrong test decoder.
    /// </remarks>
    internal static EssCertIdV2? ExtractSigningCertificateV2(byte[] signedBytes)
    {
        var cms = DecodeSignedCms(signedBytes);

        var attribute = cms.SignerInfos[0].SignedAttributes
            .Cast<CryptographicAttributeObject>()
            .SingleOrDefault(a => a.Oid.Value == SigningCertificateV2Oid);
        if (attribute is null)
            return null;

        Assert.Single(attribute.Values);
        var signingCertificateV2 = new AsnReader(attribute.Values[0].RawData, AsnEncodingRules.DER).ReadSequence();
        var certs = signingCertificateV2.ReadSequence();
        var essCertIdV2 = certs.ReadSequence();

        // Over-emission has to be rejected explicitly, not just under-emission. Reading the first
        // ESSCertIDv2 and stopping would accept an attribute carrying a second reference with an
        // attacker-chosen certHash: RFC 5035 requires only that the FIRST entry identify the signing
        // certificate, so a verifier that scans the list could match the wrong one, and ETSI
        // EN 319 122-1 expects the signing certificate's reference alone. A mutation adding exactly
        // that passed the whole suite before these two checks existed.
        Assert.False(certs.HasData, "SigningCertificateV2.certs should carry exactly one ESSCertIDv2.");
        Assert.False(
            signingCertificateV2.HasData,
            "SigningCertificateV2 should carry no policies field (this library does not model signature policies).");

        // hashAlgorithm is DEFAULT id-sha256, so it may be absent. Present means the next tag
        // is the AlgorithmIdentifier SEQUENCE; absent means it is certHash's OCTET STRING.
        string? hashAlgorithmOid = null;
        var hashAlgorithmHasParameters = false;
        if (essCertIdV2.PeekTag() == Asn1Tag.Sequence)
            (hashAlgorithmOid, hashAlgorithmHasParameters) = ReadAlgorithmIdentifier(essCertIdV2);

        var certHash = essCertIdV2.ReadOctetString();

        byte[]? issuerNameDer = null;
        byte[]? serialNumberDer = null;
        if (essCertIdV2.HasData)
        {
            var issuerSerial = essCertIdV2.ReadSequence();
            var generalNames = issuerSerial.ReadSequence();
            // GeneralName's directoryName alternative — [4], explicit because Name is a CHOICE.
            var directoryName = generalNames.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true));
            issuerNameDer = directoryName.ReadEncodedValue().ToArray();
            serialNumberDer = issuerSerial.ReadEncodedValue().ToArray();
        }

        return new EssCertIdV2(hashAlgorithmOid, hashAlgorithmHasParameters, certHash, issuerNameDer, serialNumberDer);
    }

    /// <summary>id-aa-signingCertificateV2 (RFC 5035 §3).</summary>
    internal const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
}

/// <summary>
/// A decoded <c>ESSCertIDv2</c> (RFC 5035 Appendix A). <see cref="HashAlgorithmOid"/> is null
/// when the DER-optional <c>hashAlgorithm</c> field was omitted, which for a DEFAULT of
/// id-sha256 is what a conformant encoder does for SHA-256. The issuer and serial are kept as
/// raw DER so they can be compared byte-for-byte against the certificate they must identify,
/// rather than through a string form that could normalize away a difference.
/// </summary>
internal sealed record EssCertIdV2(
    string? HashAlgorithmOid,
    bool HashAlgorithmHasParameters,
    byte[] CertHash,
    byte[]? IssuerNameDer,
    byte[]? SerialNumberDer);
