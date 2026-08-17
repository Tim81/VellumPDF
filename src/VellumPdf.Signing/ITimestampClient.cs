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
    byte[] GetTimestampToken(ReadOnlySpan<byte> messageDigest, HashAlgorithmName hashAlgorithm);

    /// <summary>
    /// Asynchronously returns a DER-encoded RFC 3161 <c>TimeStampToken</c> (a CMS
    /// <c>ContentInfo</c>) over the given message digest.
    /// </summary>
    /// <param name="messageDigest">The hash value to be timestamped.</param>
    /// <param name="hashAlgorithm">The algorithm used to compute <paramref name="messageDigest"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A DER-encoded RFC 3161 <c>TimeStampToken</c>.</returns>
    /// <remarks>
    /// The default implementation forwards to <see cref="GetTimestampToken"/>, so existing
    /// implementations of this interface keep compiling unchanged. Implementations that can
    /// perform the underlying network call asynchronously should override this member.
    /// </remarks>
    Task<byte[]> GetTimestampTokenAsync(ReadOnlyMemory<byte> messageDigest, HashAlgorithmName hashAlgorithm, CancellationToken cancellationToken = default)
        => Task.FromResult(GetTimestampToken(messageDigest.Span, hashAlgorithm));
}
