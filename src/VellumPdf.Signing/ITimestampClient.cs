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
    /// Two unrelated causes share this type. The first is the authority: it answered and refused
    /// the request, or returned a response that is not a well-formed granted timestamp. The second
    /// never reaches the authority: <paramref name="hashAlgorithm"/> names no algorithm the
    /// platform knows, and the request fails while it is being built. Do not read the second as a
    /// rejection. <c>default(HashAlgorithmName)</c> is the value a struct field starts at, and it
    /// gives <c>Unknown algorithm ''</c> without the authority ever being contacted.
    /// </exception>
    /// <remarks>
    /// This reaches the network, so expect it to fail for reasons outside your document.
    /// A timeout and a failing HTTP status give <see cref="InvalidOperationException"/>. An
    /// unreachable authority gives <see cref="System.Net.Http.HttpRequestException"/>. A
    /// rejection, or a malformed response body, gives
    /// <see cref="System.Security.Cryptography.CryptographicException"/>. An unknown
    /// <paramref name="hashAlgorithm"/> shares that last type and never reaches the authority.
    /// <para>This call waits on the authority. What bounds the wait is the implementation's
    /// business: the shipped client applies its own timeout, and a caller-supplied
    /// <c>HttpClient</c> carries a second one that applies independently.
    /// <see cref="GetTimestampTokenAsync"/> avoids the wait only on an implementation that
    /// overrides it. The default below does not; it forwards here and blocks exactly as this call
    /// does.</para>
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
    /// As <see cref="GetTimestampToken"/>, both of its causes: the authority refused the request or
    /// returned a malformed response, or <paramref name="hashAlgorithm"/> names no algorithm the
    /// platform knows. The second is raised while the request is built, so the default
    /// implementation below raises it before a task exists rather than returning a faulted
    /// one.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled and the implementation honours it. The
    /// shipped client raises <see cref="TaskCanceledException"/>, which derives from this type, so
    /// catching either works. The default implementation below ignores the token and so never
    /// raises it. Neither statement binds an implementation you write yourself.
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
