// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;

namespace VellumPdf.Signing;

/// <summary>
/// Obtains revocation evidence (OCSP responses and/or CRLs) for a certificate,
/// for embedding in a PAdES B-LT Document Security Store (DSS).
/// </summary>
public interface IRevocationClient
{
    /// <summary>
    /// Returns DER-encoded revocation evidence for <paramref name="certificate"/>,
    /// as issued by <paramref name="issuer"/>.
    /// </summary>
    /// <param name="certificate">The certificate whose revocation status is sought.</param>
    /// <param name="issuer">The certificate that issued <paramref name="certificate"/>;
    /// used to build the OCSP <c>CertID</c> (issuer name hash and key hash).</param>
    /// <returns>
    /// A <see cref="RevocationData"/> carrying any evidence that could be obtained.
    /// A <see langword="null"/> field means that kind of evidence was not available
    /// (none published, or the fetch failed); an empty result is valid and not an error.
    /// </returns>
    /// <remarks>
    /// Implementations may perform network I/O (for example, an HTTP OCSP request or
    /// a CRL download). Implementations should be resilient: a failure to obtain one
    /// kind of evidence should not prevent returning the other.
    /// </remarks>
    RevocationData GetRevocationData(X509Certificate2 certificate, X509Certificate2 issuer);
}
