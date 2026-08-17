// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Document;

namespace VellumPdf.Signing;

/// <summary>
/// Computes a PAdES/PKCS#7 detached CMS signature over unsigned PDF placeholder bytes
/// and patches the result in-place.
///
/// Algorithm:
/// 1. Locate the /Contents &lt;…&gt; hex token by anchoring on the unique /ByteRange placeholder.
/// 2. Compute ByteRange as the two segments that exclude the &lt;…&gt; token.
/// 3. Overwrite the /ByteRange placeholder digits in-place (fixed-width fields).
/// 4. Concatenate the two ByteRange segments and compute a detached SHA-256 CMS
///    signature using <see cref="SignedCms"/>.
/// 5. Hex-encode the DER signature and overwrite the /Contents placeholder in-place.
/// 6. Write the patched bytes to the output stream.
/// </summary>
internal static class PdfCmsSigner
{
    // Default reserved size for a timestamped (B-T) signature. A real TSA token embeds its
    // own certificate chain on top of the signer's chain, so reserve generously (32 KB) to fit
    // common public-TSA chains without the caller having to tune the size. An over-estimate only
    // pads the /Contents hex with unused zeros; the explicit guard below still catches a genuine
    // overflow with an actionable message.
    private const int TimestampedDefaultReserve = 32768;

    /// <summary>
    /// Returns the effective /Contents reserve to use for <paramref name="settings"/>.
    /// When a timestamp client is configured and the caller left
    /// <see cref="PdfSignatureSettings.EstimatedSignatureSizeBytes"/> at its public default
    /// (8192), a larger value is returned so the common timestamped path does not trip the
    /// size guard.  An explicitly chosen value is always honoured.
    /// </summary>
    internal static int EffectiveReserve(PdfSignatureSettings settings)
        => (settings.TimestampClient is not null && settings.EstimatedSignatureSizeBytes == 8192)
            ? TimestampedDefaultReserve
            : settings.EstimatedSignatureSizeBytes;

    /// <summary>
    /// Signs a PDF document previously written to <paramref name="unsignedBytes"/> and writes
    /// the signed result to <paramref name="output"/>.
    /// </summary>
    internal static void Sign(
        byte[] unsignedBytes,
        PdfSignatureSettings settings,
        Stream output)
    {
        var effectiveReserve = EffectiveReserve(settings);

        // ── Step 1: locate the /Contents placeholder ──────────────────────────
        // Anchor on the /ByteRange placeholder (unique, and overwritten in-place so it
        // leaves no trace in the output). The bytes between the end of the ByteRange
        // placeholder and the opening '<' of the /Contents hex string are the fixed
        // sequence "]\n/Contents " — no caller-controlled data intervenes, so adversarial
        // metadata (Reason/Location/… containing "/Contents <") cannot match first.
        var posLt = SignaturePlaceholderPatcher.LocateContentsToken(unsignedBytes, effectiveReserve, out var hexLen);

        // ── Steps 2–3: compute and patch /ByteRange in-place ──────────────────
        var (br0, br1, br2, br3) = SignaturePlaceholderPatcher.ComputeAndPatchByteRange(unsignedBytes, posLt, hexLen);

        // ── Step 4: build signed content (two segments concatenated) ─────────
        var signedContent = SignaturePlaceholderPatcher.BuildSignedContent(unsignedBytes, br0, br1, br2, br3);

        // ── Step 5: compute detached CMS signature ────────────────────────────
        var sig = ComputeCmsSignature(signedContent, settings);

        // ── Step 6: validate size and hex-encode into the /Contents placeholder ─
        SignaturePlaceholderPatcher.PatchContents(unsignedBytes, posLt, hexLen, sig, "CMS signature");

        // ── Step 7: write to output ────────────────────────────────────────────
        output.Write(unsignedBytes, 0, unsignedBytes.Length);
    }

    /// <summary>
    /// Asynchronously signs a PDF document previously written to <paramref name="unsignedBytes"/>
    /// and writes the signed result to <paramref name="output"/>.
    /// </summary>
    internal static async Task SignAsync(
        byte[] unsignedBytes,
        PdfSignatureSettings settings,
        Stream output,
        CancellationToken cancellationToken)
    {
        var effectiveReserve = EffectiveReserve(settings);

        var posLt = SignaturePlaceholderPatcher.LocateContentsToken(unsignedBytes, effectiveReserve, out var hexLen);
        var (br0, br1, br2, br3) = SignaturePlaceholderPatcher.ComputeAndPatchByteRange(unsignedBytes, posLt, hexLen);
        var signedContent = SignaturePlaceholderPatcher.BuildSignedContent(unsignedBytes, br0, br1, br2, br3);

        var sig = await ComputeCmsSignatureAsync(signedContent, settings, cancellationToken).ConfigureAwait(false);

        SignaturePlaceholderPatcher.PatchContents(unsignedBytes, posLt, hexLen, sig, "CMS signature");

        await output.WriteAsync(unsignedBytes, cancellationToken).ConfigureAwait(false);
    }

    // ── CMS signature computation ─────────────────────────────────────────────

    /// <summary>
    /// The digest this path signs with. Named once because the
    /// <c>signing-certificate-v2</c> attribute below has to hash the signing certificate with
    /// the same algorithm the signature itself uses (RFC 5035 §4).
    /// </summary>
    private static readonly HashAlgorithmName SignatureDigest = HashAlgorithmName.SHA256;

    /// <summary>
    /// Rejects a certificate whose serial number is not minimally DER-encoded, before
    /// <see cref="SignedCms"/> is reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET's X.509 parser accepts a serial carrying a redundant leading pad byte, but every DER
    /// encoder rejects it — including the BCL's own <c>IssuerAndSerialNumberAsn.Encode</c>, which
    /// <see cref="SignedCms.ComputeSignature(CmsSigner)"/> calls while building the
    /// <c>SignerInfo</c>. Left alone, that surfaces as <c>ArgumentException: The first 9 bits of
    /// the integer value all have the same value</c> from deep inside the BCL, which says nothing
    /// about the certificate or what to do about it (issue #167).
    /// </para>
    /// <para>
    /// The serial cannot be normalized on this path the way <see cref="ExternalSignerCms"/>
    /// normalizes it: the encoding happens inside <see cref="SignedCms"/>, from the
    /// <see cref="X509Certificate2"/> itself, so there is nothing for this library to rewrite.
    /// Re-issuing the certificate is the only real fix, so the failure names that.
    /// </para>
    /// <para>
    /// <strong>Reachable on Windows only.</strong> Whether a certificate with such a serial can be
    /// loaded at all is platform-dependent: Windows accepts it, while Linux's OpenSSL-backed parser
    /// rejects it as <c>ASN1 corrupted data</c> before an <see cref="X509Certificate2"/> exists. So
    /// on non-Windows platforms this check cannot fire — the certificate never gets far enough to
    /// be passed in. The guard is kept unconditional rather than platform-gated because the cost is
    /// one span comparison and the alternative is a platform-specific code path guarding against a
    /// platform-specific parser behaviour, which is harder to reason about than the check itself.
    /// </para>
    /// </remarks>
    private static void ValidateCertificateSerial(PdfSignatureSettings settings)
    {
        if (Asn1SerialNumber.IsMinimal(settings.Certificate.SerialNumberBytes.Span))
            return;

        throw new ArgumentException(
            "settings.Certificate has a serial number that is not minimally DER-encoded: its "
            + $"content octets are 0x{Convert.ToHexString(settings.Certificate.SerialNumberBytes.Span)}, "
            + "which carries a redundant leading pad byte. ITU-T X.690 §8.3.2 requires the shortest "
            + "two's-complement encoding, so the CMS SignerInfo cannot be built from this "
            + "certificate — .NET's X.509 parser tolerates the encoding when reading, but every DER "
            + "encoder rejects it when writing. The certificate is mis-issued and needs re-issuing "
            + "by its CA. Signing with PdfSignatureSettings.ExternalSigner does work, because that "
            + "path encodes the SignerInfo itself and normalizes the serial on the way.",
            nameof(settings));
    }

    /// <summary>
    /// Creates the <see cref="CmsSigner"/> to use for <paramref name="settings"/>. Uses the
    /// private key attached to <see cref="PdfSignatureSettings.Certificate"/> unless
    /// <see cref="PdfSignatureSettings.ExternalPrivateKey"/> is set, in which case that key is
    /// used instead — the certificate is still supplied for its public key, subject, and chain.
    /// </summary>
    private static CmsSigner CreateSigner(PdfSignatureSettings settings)
    {
        ValidateCertificateSerial(settings);

        var signer = settings.ExternalPrivateKey is null
            ? new CmsSigner(settings.Certificate)
            : new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, settings.Certificate, settings.ExternalPrivateKey);
        signer.DigestAlgorithm = new Oid(Sha2DigestAlgorithm.Oid(SignatureDigest));
        signer.IncludeOption = X509IncludeOption.WholeChain;

        // ESS signing-certificate-v2 (RFC 5035), required by the CAdES profile that
        // PdfSignatureSettings.SubFilter claims by default. Added here rather than at each
        // ComputeCmsSignature call site so the sync and async paths cannot drift apart.
        signer.SignedAttributes.Add(new AsnEncodedData(
            new Oid(SigningCertificateV2.AttributeOid),
            SigningCertificateV2.Encode(settings.Certificate, SignatureDigest)));

        return signer;
    }

    private static byte[] ComputeCmsSignature(byte[] signedContent, PdfSignatureSettings settings)
    {
        if (settings.ExternalSigner is not null)
            throw new NotSupportedException(
                "PdfSignatureSettings.ExternalSigner requires an async signing call and is " +
                "not supported by the synchronous Sign overloads. Use SignAsync instead.");

        var signer = CreateSigner(settings);

        var signingTime = settings.SigningTime ?? DateTimeOffset.UtcNow;
        signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));

        var cms = new SignedCms(new ContentInfo(signedContent), detached: true);
        cms.ComputeSignature(signer);

        if (settings.TimestampClient is not null)
        {
            var si = cms.SignerInfos[0];
            var signatureValue = si.GetSignature();
            var digest = SHA256.HashData(signatureValue);
            var tokenDer = settings.TimestampClient.GetTimestampToken(digest, HashAlgorithmName.SHA256);
            // Ensure the returned data decodes as a valid RFC 3161 token before embedding.
            if (!Rfc3161TimestampToken.TryDecode(tokenDer, out var token, out _))
                throw new InvalidOperationException("Timestamp client returned data that is not a valid RFC 3161 token.");
            // Defense in depth: a custom ITimestampClient could return a structurally valid token
            // that was computed over unrelated data. Confirm the token actually stamps THIS
            // signature's digest with the algorithm we asked for, so we never embed a timestamp
            // that does not cover the signature.
            var tokenInfo = token!.TokenInfo;
            if (tokenInfo.HashAlgorithmId.Value != "2.16.840.1.101.3.4.2.1" // SHA-256
                || !tokenInfo.GetMessageHash().Span.SequenceEqual(digest))
                throw new InvalidOperationException(
                    "The RFC 3161 timestamp token does not cover the signature digest.");
            // OID 1.2.840.113549.1.9.16.2.14 = id-aa-signatureTimeStampToken (RFC 3161 unsigned attribute)
            si.AddUnsignedAttribute(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.14"), tokenDer));
        }

        return cms.Encode();
    }

    private static async Task<byte[]> ComputeCmsSignatureAsync(byte[] signedContent, PdfSignatureSettings settings, CancellationToken cancellationToken)
    {
        SignedCms cms;

        if (settings.ExternalSigner is not null)
        {
            cms = await ExternalSignerCms.BuildAsync(signedContent, settings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var signer = CreateSigner(settings);

            var signingTime = settings.SigningTime ?? DateTimeOffset.UtcNow;
            signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));

            cms = new SignedCms(new ContentInfo(signedContent), detached: true);
            cms.ComputeSignature(signer);
        }

        await EmbedTimestampIfConfiguredAsync(cms, settings, cancellationToken).ConfigureAwait(false);

        return cms.Encode();
    }

    /// <summary>
    /// Obtains an RFC 3161 timestamp over <paramref name="cms"/>'s signature value and
    /// embeds it as an unsigned attribute, when <see cref="PdfSignatureSettings.TimestampClient"/>
    /// is set. Unsigned attributes don't affect the signature, so this applies identically
    /// regardless of whether <paramref name="cms"/> was produced by <see cref="CmsSigner"/>
    /// or by <see cref="ExternalSignerCms"/>.
    /// </summary>
    private static async Task EmbedTimestampIfConfiguredAsync(SignedCms cms, PdfSignatureSettings settings, CancellationToken cancellationToken)
    {
        if (settings.TimestampClient is null)
            return;

        var si = cms.SignerInfos[0];
        var signatureValue = si.GetSignature();
        var digest = SHA256.HashData(signatureValue);
        var tokenDer = await settings.TimestampClient.GetTimestampTokenAsync(digest, HashAlgorithmName.SHA256, cancellationToken).ConfigureAwait(false);
        // Ensure the returned data decodes as a valid RFC 3161 token before embedding.
        if (!Rfc3161TimestampToken.TryDecode(tokenDer, out var token, out _))
            throw new InvalidOperationException("Timestamp client returned data that is not a valid RFC 3161 token.");
        // Defense in depth: a custom ITimestampClient could return a structurally valid token
        // that was computed over unrelated data. Confirm the token actually stamps THIS
        // signature's digest with the algorithm we asked for, so we never embed a timestamp
        // that does not cover the signature.
        var tokenInfo = token!.TokenInfo;
        if (tokenInfo.HashAlgorithmId.Value != "2.16.840.1.101.3.4.2.1" // SHA-256
            || !tokenInfo.GetMessageHash().Span.SequenceEqual(digest))
            throw new InvalidOperationException(
                "The RFC 3161 timestamp token does not cover the signature digest.");
        // OID 1.2.840.113549.1.9.16.2.14 = id-aa-signatureTimeStampToken (RFC 3161 unsigned attribute)
        si.AddUnsignedAttribute(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.14"), tokenDer));
    }
}
