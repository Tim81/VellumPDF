// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;

namespace VellumPdf.Signing;

/// <summary>
/// Settings for PAdES/PKCS#7 digital signature creation.
/// The certificate MUST include a private key (i.e. <see cref="X509Certificate2.HasPrivateKey"/>
/// must return true).
/// </summary>
public sealed class PdfSignatureSettings
{
    /// <summary>
    /// The signing certificate with private key.
    /// </summary>
    public required X509Certificate2 Certificate { get; init; }

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
}
