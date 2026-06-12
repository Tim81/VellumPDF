// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace VellumPdf.Signing;

/// <summary>
/// Obtains an RFC 3161 timestamp token from a Time Stamping Authority (TSA).
/// </summary>
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
}
