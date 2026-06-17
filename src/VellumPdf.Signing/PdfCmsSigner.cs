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

    // ── CMS signature computation ─────────────────────────────────────────────

    private static byte[] ComputeCmsSignature(byte[] signedContent, PdfSignatureSettings settings)
    {
        var signer = new CmsSigner(settings.Certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.WholeChain,
        };

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

}
