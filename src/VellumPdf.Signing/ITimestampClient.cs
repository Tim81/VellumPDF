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
    /// This reaches the network, so expect it to fail for reasons outside your document. The
    /// failures arrive as three different exception types, measured against a loopback authority.
    /// A timeout and a failing HTTP status give <see cref="InvalidOperationException"/>. An
    /// unreachable authority gives <see cref="System.Net.Http.HttpRequestException"/>. A
    /// rejection, or a malformed response body, gives
    /// <see cref="System.Security.Cryptography.CryptographicException"/>.
    /// <para>Attention: the type is the discriminator here, not the message. Catch only
    /// <see cref="InvalidOperationException"/> and an unreachable authority escapes it, because
    /// that arrives as <see cref="System.Net.Http.HttpRequestException"/> instead.</para>
    /// <para>This call waits on the authority, with the implementation's own timeout as the only
    /// bound. If you are on a path that must not block, use
    /// <see cref="GetTimestampTokenAsync"/>.</para>
    /// <para>A returned token is <b>not</b> verified. It is the authority's answer, carried into
    /// the signature as supplied. Whether its certificate chains to anything a verifier trusts is
    /// a separate question that this call does not answer.</para>
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
    /// As <see cref="GetTimestampToken"/>: the request timed out, or the authority answered with
    /// a failing HTTP status.
    /// </exception>
    /// <exception cref="System.Net.Http.HttpRequestException">
    /// As <see cref="GetTimestampToken"/>: the authority could not be reached at all.
    /// </exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// As <see cref="GetTimestampToken"/>: the authority refused the request, or returned a
    /// response that is not a well-formed granted timestamp.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled and the implementation honours it. The
    /// shipped client raises <see cref="TaskCanceledException"/>, which derives from this type, so
    /// catching either works. This is a fourth type the synchronous member cannot produce, and the
    /// default implementation below never produces it.
    /// </exception>
    /// <remarks>
    /// The default implementation forwards to <see cref="GetTimestampToken"/>, so existing
    /// implementations of this interface keep compiling unchanged. Implementations that can
    /// perform the underlying network call asynchronously should override this member.
    /// <para>Attention: the default implementation blocks. It runs the synchronous call on the
    /// calling thread and returns an already-completed task, so the wait happens before the task
    /// reaches you and <paramref name="cancellationToken"/> is never consulted. An implementation
    /// that overrides this member decides both of those for itself.</para>
    /// </remarks>
    Task<byte[]> GetTimestampTokenAsync(ReadOnlyMemory<byte> messageDigest, HashAlgorithmName hashAlgorithm, CancellationToken cancellationToken = default)
        => Task.FromResult(GetTimestampToken(messageDigest.Span, hashAlgorithm));
}
