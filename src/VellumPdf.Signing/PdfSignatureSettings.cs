// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VellumPdf.Signing;

/// <summary>
/// Settings for PAdES/PKCS#7 digital signature creation.
/// The certificate must include a private key (i.e. <see cref="X509Certificate2.HasPrivateKey"/>
/// must return true), or <see cref="ExternalPrivateKey"/> or <see cref="ExternalSigner"/>
/// must be supplied.
/// </summary>
public sealed class PdfSignatureSettings
{
    /// <summary>
    /// The signing certificate. When neither <see cref="ExternalPrivateKey"/> nor
    /// <see cref="ExternalSigner"/> is set, this certificate must include a private key.
    /// </summary>
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>
    /// An externally-held private key to sign with, for certificates whose key is not
    /// attached to <see cref="Certificate"/> (for example, a certificate fetched from a
    /// cloud key vault, or a key held on a PKCS#11 device without Windows CNG integration).
    /// When <see langword="null"/> (the default), the key attached to <see cref="Certificate"/>
    /// is used instead, unless <see cref="ExternalSigner"/> is set. <see cref="Certificate"/>
    /// is still required in either case, for the public key, subject, and certificate chain.
    /// </summary>
    public AsymmetricAlgorithm? ExternalPrivateKey { get; init; }

    /// <summary>
    /// An asynchronous external signer to sign with — for a cloud KMS or remote HSM where
    /// the signing call itself is a network round-trip (Azure Key Vault, AWS KMS, GCP KMS,
    /// some PKCS#11 setups), and blocking a thread on <see cref="ExternalPrivateKey"/> to
    /// bridge that call is undesirable. Only supported via <c>SignAsync</c>; the synchronous
    /// <c>Sign</c> overloads throw when this is set. When both <see cref="ExternalSigner"/>
    /// and <see cref="ExternalPrivateKey"/> are set, <see cref="ExternalSigner"/> takes
    /// precedence. <see cref="Certificate"/> is still required, for the public key, subject,
    /// and certificate chain.
    /// </summary>
    public IExternalSigner? ExternalSigner { get; init; }

    /// <summary>Optional signer name written to /Name in the signature dictionary.</summary>
    public string? SignerName { get; init; }

    /// <summary>Optional reason for signing, written to /Reason.</summary>
    public string? Reason { get; init; }

    /// <summary>Optional signing location, written to /Location.</summary>
    public string? Location { get; init; }

    /// <summary>Optional contact information, written to /ContactInfo.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// Signing time. When null, <see cref="DateTimeOffset.UtcNow"/> is used at sign time.
    /// </summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>
    /// Reserved space in bytes for the DER-encoded CMS signature blob in the /Contents
    /// hex string. Default is 8192. Increase if signing with a very long certificate chain.
    /// </summary>
    public int EstimatedSignatureSizeBytes { get; init; } = 8192;

    /// <summary>
    /// PDF signature sub-filter. Default is "ETSI.CAdES.detached" (PAdES B-B).
    /// Use "adbe.pkcs7.detached" for legacy compatibility.
    /// </summary>
    public string SubFilter { get; init; } = "ETSI.CAdES.detached";

    /// <summary>
    /// Optional RFC 3161 timestamp client. When set, an RFC 3161 <c>TimeStampToken</c> is
    /// obtained over the CMS signature value and embedded as an unsigned attribute
    /// (OID 1.2.840.113549.1.9.16.2.14), producing a PAdES B-T signature.
    /// When <see langword="null"/> (the default), no timestamp is added and the
    /// signature conforms to PAdES B-B.
    /// </summary>
    public ITimestampClient? TimestampClient { get; init; }

    /// <summary>
    /// Zero-based index of the page to which the invisible signature widget annotation
    /// is added. Default is 0 (the first page). An out-of-range value throws
    /// <see cref="ArgumentOutOfRangeException"/> during signing.
    /// </summary>
    public int SignaturePage { get; init; } = 0;

    /// <summary>
    /// PAdES conformance level to produce. Default is <see cref="PadesLevel.B_B"/>.
    /// <list type="bullet">
    ///   <item><see cref="PadesLevel.B_T"/> requires <see cref="TimestampClient"/>.</item>
    ///   <item><see cref="PadesLevel.B_LT"/> requires both <see cref="TimestampClient"/>
    ///     and <see cref="RevocationClient"/>.</item>
    ///   <item><see cref="PadesLevel.B_LTA"/> requires both <see cref="TimestampClient"/>
    ///     and <see cref="RevocationClient"/>.</item>
    /// </list>
    /// </summary>
    public PadesLevel Level { get; init; } = PadesLevel.B_B;

    /// <summary>
    /// Optional revocation client used to fetch OCSP responses and/or CRLs for each
    /// certificate in the signing chain. Required when <see cref="Level"/> is
    /// <see cref="PadesLevel.B_LT"/> or <see cref="PadesLevel.B_LTA"/>.
    /// When <see langword="null"/> (the default), no revocation evidence is collected.
    /// </summary>
    public IRevocationClient? RevocationClient { get; init; }
}
