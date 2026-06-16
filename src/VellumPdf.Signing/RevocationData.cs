// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Signing;

/// <summary>
/// Carries optional DER-encoded revocation evidence for a single
/// (certificate, issuer) pair, for embedding in a PAdES B-LT
/// Document Security Store (DSS).
/// </summary>
/// <remarks>
/// Either field may be <see langword="null"/> when the corresponding
/// evidence could not be obtained (for example, no OCSP responder or
/// CRL distribution point was published, or the network fetch failed).
/// </remarks>
public sealed class RevocationData
{
    /// <summary>
    /// A DER-encoded OCSP <c>OCSPResponse</c> (RFC 6960) for the certificate,
    /// or <see langword="null"/> if none is available.
    /// </summary>
    public ReadOnlyMemory<byte>? Ocsp { get; init; }

    /// <summary>
    /// A DER-encoded <c>CertificateList</c> (an X.509 CRL, RFC 5280) covering
    /// the certificate, or <see langword="null"/> if none is available.
    /// </summary>
    public ReadOnlyMemory<byte>? Crl { get; init; }

    /// <summary>
    /// <see langword="true"/> when neither an OCSP response nor a CRL is present.
    /// </summary>
    public bool IsEmpty => Ocsp is null && Crl is null;
}
