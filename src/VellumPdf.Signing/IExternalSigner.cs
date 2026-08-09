// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace VellumPdf.Signing;

/// <summary>
/// Signs a CMS signed-attributes digest asynchronously, for a cloud KMS or remote HSM
/// where the signing call itself is a network round-trip (Azure Key Vault, AWS KMS,
/// GCP KMS, some PKCS#11 setups).
/// </summary>
/// <remarks>
/// This interface is deliberately async-only, with no synchronous member: signing via
/// <see cref="IExternalSigner"/> is only supported through <c>SignAsync</c>, never the
/// synchronous <c>Sign</c> overloads. A synchronous fallback would only invite wrapping a
/// blocking network call behind it, which defeats the point of this interface — for a
/// local, synchronous key (including one backed by Windows CNG or a PKCS#11 device that
/// answers in-process), use <see cref="PdfSignatureSettings.ExternalPrivateKey"/> instead.
/// </remarks>
public interface IExternalSigner
{
    /// <summary>
    /// The hash algorithm used both to compute the digest passed to <see cref="SignAsync"/>
    /// and as the CMS <c>SignerInfo</c> digest algorithm.
    /// </summary>
    HashAlgorithmName HashAlgorithm { get; }

    /// <summary>
    /// Asynchronously signs <paramref name="signedAttributesDigest"/> — the digest of the
    /// CMS <c>SignerInfo</c> signed attributes, not the document or its content digest —
    /// and returns the raw signature bytes ready to embed as <c>SignerInfo.signature</c>.
    /// </summary>
    /// <param name="signedAttributesDigest">
    /// The <see cref="HashAlgorithm"/> digest of the DER-encoded signed attributes.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// For an RSA certificate, the PKCS#1 v1.5 signature bytes — the format Azure Key
    /// Vault's and AWS KMS's RSA sign operations already return. RSASSA-PSS is not
    /// supported: the CMS <c>SignerInfo.signatureAlgorithm</c> this library writes for an
    /// RSA certificate is always <c>rsaEncryption</c> (PKCS#1 v1.5), so a PSS signature
    /// (Azure Key Vault <c>PS256</c>/<c>PS384</c>/<c>PS512</c>, AWS KMS
    /// <c>RSASSA_PSS_SHA_256</c> and similar) fails verification even though the signer
    /// itself succeeded — configure the KMS/HSM key for PKCS#1 v1.5 signing instead. For an
    /// EC certificate, the DER-encoded <c>ECDSA-Sig-Value</c> sequence, not a raw
    /// <c>r || s</c> concatenation: Azure Key Vault's ECDSA sign operation returns raw
    /// <c>r || s</c> and needs converting first with
    /// <see cref="EcdsaSignatureConverter.RawToDer"/>.
    /// </returns>
    Task<byte[]> SignAsync(ReadOnlyMemory<byte> signedAttributesDigest, CancellationToken cancellationToken = default);
}
