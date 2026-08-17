// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace VellumPdf.Signing;

/// <summary>
/// The SHA-2 digests this library signs with: their NIST object identifiers and the
/// corresponding hash implementations.
/// </summary>
/// <remarks>
/// Both live here so that the digest a signature <em>claims</em> and the digest it is actually
/// computed with are selected by the same switch over the same input. Kept apart, they are two
/// tables that have to be edited in step — the class of drift that produced the per-hash OID
/// gap in issue #166, where the SHA-384/512 arms of two such tables went unexercised.
/// </remarks>
internal static class Sha2DigestAlgorithm
{
    /// <summary>
    /// The NIST OID for <paramref name="hashAlgorithm"/> (RFC 5754 §2, which also requires
    /// these to be written with an absent parameters field rather than a NULL one).
    /// </summary>
    internal static string Oid(HashAlgorithmName hashAlgorithm) => hashAlgorithm.Name switch
    {
        "SHA256" => "2.16.840.1.101.3.4.2.1",
        "SHA384" => "2.16.840.1.101.3.4.2.2",
        "SHA512" => "2.16.840.1.101.3.4.2.3",
        _ => throw Unsupported(hashAlgorithm),
    };

    /// <summary>Computes the <paramref name="hashAlgorithm"/> digest of <paramref name="data"/>.</summary>
    internal static byte[] Hash(HashAlgorithmName hashAlgorithm, ReadOnlySpan<byte> data) => hashAlgorithm.Name switch
    {
        "SHA256" => SHA256.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        _ => throw Unsupported(hashAlgorithm),
    };

    // SHA-1 and MD5 are excluded deliberately, not merely unimplemented: both are broken for
    // signature use, and PAdES baseline signatures require a SHA-2 digest.
    private static NotSupportedException Unsupported(HashAlgorithmName hashAlgorithm) => new(
        $"Hash algorithm '{hashAlgorithm.Name}' is not supported for signing. Use SHA256, SHA384, or SHA512.");
}
