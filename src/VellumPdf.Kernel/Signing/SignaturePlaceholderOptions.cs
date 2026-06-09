// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>
/// Non-cryptographic options used when writing the signature-placeholder structure
/// into the PDF body. Consumed by <see cref="PdfDocument.PrepareForSigning"/>.
/// The actual CMS computation lives in <c>VellumPdf.Signing</c>.
/// </summary>
public sealed class SignaturePlaceholderOptions
{
    /// <summary>
    /// PDF signature sub-filter. Default is "ETSI.CAdES.detached" (PAdES B-B).
    /// Use "adbe.pkcs7.detached" for legacy compatibility.
    /// </summary>
    public string SubFilter { get; init; } = "ETSI.CAdES.detached";

    /// <summary>
    /// Reserved space in bytes for the DER-encoded CMS signature blob in the /Contents
    /// hex string. Default is 8192.
    /// </summary>
    public int EstimatedSignatureSizeBytes { get; init; } = 8192;

    /// <summary>Optional signer name written to /Name in the signature dictionary.</summary>
    public string? SignerName { get; init; }

    /// <summary>Optional reason for signing, written to /Reason.</summary>
    public string? Reason { get; init; }

    /// <summary>Optional signing location, written to /Location.</summary>
    public string? Location { get; init; }

    /// <summary>Optional contact information, written to /ContactInfo.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// Signing time written to /M. When null, <see cref="DateTimeOffset.UtcNow"/> is
    /// used at the time <see cref="PdfDocument.PrepareForSigning"/> is called.
    /// </summary>
    public DateTimeOffset? SigningTime { get; init; }
}
