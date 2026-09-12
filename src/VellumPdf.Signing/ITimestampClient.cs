// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace VellumPdf.Signing;

/// <summary>
/// Obtains an RFC 3161 timestamp token from a Time Stamping Authority (TSA).
/// </summary>
/// <remarks>
/// The synchronous <see cref="GetTimestampToken"/> is kept alongside
/// <see cref="GetTimestampTokenAsync"/> for the same reasons set out on
/// <see cref="IRevocationClient"/>: it is the required member, the async one is
/// default-implemented, and the blocking-on-async hazard that argued for removing it has been fixed
/// in the shipped implementation rather than avoided by deleting the surface. Prefer the async
/// member in new code.
/// </remarks>
public interface ITimestampClient
{
    /// <summary>
    /// Returns a DER-encoded RFC 3161 <c>TimeStampToken</c> (a CMS <c>ContentInfo</c>)
    /// over the given message digest.
    /// </summary>
    /// <param name="messageDigest">The hash value to be timestamped.</param>
    /// <param name="hashAlgorithm">The algorithm used to compute <paramref name="messageDigest"/>.</param>
    /// <returns>A DER-encoded RFC 3161 <c>TimeStampToken</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The request timed out, or the authority answered with a failing HTTP status.
    /// </exception>
    /// <exception cref="System.Net.Http.HttpRequestException">
    /// The authority could not be reached at all.
    /// </exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The authority answered, but refused the request or returned a response that is not a
    /// well-formed granted timestamp.
    /// </exception>
    /// <remarks>
    /// <b>This reaches the network, and the failures arrive as three different exception
    /// types.</b> Measured against a loopback authority: a timeout and a failing HTTP status give
    /// <see cref="InvalidOperationException"/>; an unreachable authority gives
    /// <see cref="System.Net.Http.HttpRequestException"/>; and a rejection, or a malformed
    /// response body, gives
    /// <see cref="System.Security.Cryptography.CryptographicException"/>. So the type is the
    /// discriminator, not the message, and catching only
    /// <see cref="InvalidOperationException"/> lets the most likely failure of the three — an
    /// authority that cannot be reached — escape.
    /// <para><b>Do not call this where blocking is unacceptable.</b> It waits on the authority,
    /// with the implementation's own timeout as the only bound; use
    /// <see cref="GetTimestampTokenAsync"/> where that matters.</para>
    /// <para><b>Do not treat a returned token as verified.</b> It is the authority's answer,
    /// carried into the signature as supplied. Whether its certificate chains to anything a
    /// verifier trusts is a separate question this call does not answer.</para>
    /// </remarks>
    byte[] GetTimestampToken(ReadOnlySpan<byte> messageDigest, HashAlgorithmName hashAlgorithm);

    /// <summary>
    /// Asynchronously returns a DER-encoded RFC 3161 <c>TimeStampToken</c> (a CMS
    /// <c>ContentInfo</c>) over the given message digest.
    /// </summary>
    /// <param name="messageDigest">The hash value to be timestamped.</param>
    /// <param name="hashAlgorithm">The algorithm used to compute <paramref name="messageDigest"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A DER-encoded RFC 3161 <c>TimeStampToken</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// As <see cref="GetTimestampToken"/>: the authority could not be reached, timed out,
    /// answered with a failing HTTP status, or returned a malformed response.
    /// </exception>
    /// <remarks>
    /// The default implementation forwards to <see cref="GetTimestampToken"/>, so existing
    /// implementations of this interface keep compiling unchanged. Implementations that can
    /// perform the underlying network call asynchronously should override this member.
    /// <para><b>Do not assume this does not block.</b> An implementation that has not overridden
    /// it runs the synchronous call on the calling thread and returns a completed task, so the
    /// wait happens before the task is handed back and
    /// <paramref name="cancellationToken"/> is never consulted.</para>
    /// </remarks>
    Task<byte[]> GetTimestampTokenAsync(ReadOnlyMemory<byte> messageDigest, HashAlgorithmName hashAlgorithm, CancellationToken cancellationToken = default)
        => Task.FromResult(GetTimestampToken(messageDigest.Span, hashAlgorithm));
}
