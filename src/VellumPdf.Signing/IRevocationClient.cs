// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.X509Certificates;

namespace VellumPdf.Signing;

/// <summary>
/// Obtains revocation evidence (OCSP responses and/or CRLs) for a certificate,
/// for embedding in a PAdES B-LT Document Security Store (DSS).
/// </summary>
/// <remarks>
/// <para>
/// <strong>On having both a synchronous and an asynchronous member.</strong> Reviewed for v2.0 and
/// kept deliberately. <see cref="GetRevocationData"/> is the required member and
/// <see cref="GetRevocationDataAsync"/> is default-implemented, so removing the synchronous one
/// would break every existing implementation of this interface, and the synchronous
/// <c>Sign</c> overloads depend on it. The reason to consider removing it was that the shipped
/// implementation blocked on an asynchronous call, which risks deadlock on a synchronization
/// context and thread-pool starvation under load; that has been fixed instead, by using genuinely
/// synchronous HTTP APIs (<see cref="System.Net.Http.HttpClient.Send(System.Net.Http.HttpRequestMessage, System.Threading.CancellationToken)"/>
/// and <c>HttpContent.ReadAsStream</c>) rather than by deleting the surface. Prefer
/// <see cref="GetRevocationDataAsync"/> in new code; the synchronous path stays supported for 2.x.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Asynchronously returns DER-encoded revocation evidence for <paramref name="certificate"/>,
    /// as issued by <paramref name="issuer"/>.
    /// </summary>
    /// <param name="certificate">The certificate whose revocation status is sought.</param>
    /// <param name="issuer">The certificate that issued <paramref name="certificate"/>;
    /// used to build the OCSP <c>CertID</c> (issuer name hash and key hash).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="RevocationData"/> carrying any evidence that could be obtained.
    /// A <see langword="null"/> field means that kind of evidence was not available
    /// (none published, or the fetch failed); an empty result is valid and not an error.
    /// </returns>
    /// <remarks>
    /// The default implementation forwards to <see cref="GetRevocationData"/>, so existing
    /// implementations of this interface keep compiling unchanged. Implementations that can
    /// perform the underlying network I/O asynchronously should override this member.
    /// </remarks>
    Task<RevocationData> GetRevocationDataAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken = default)
        => Task.FromResult(GetRevocationData(certificate, issuer));
}
