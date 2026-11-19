// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VellumPdf.Signing;

/// <summary>
/// Settings for PAdES/PKCS#7 digital signature creation.
/// The certificate must include a private key (i.e. <see cref="X509Certificate2.HasPrivateKey"/>
/// must return true), or <see cref="ExternalPrivateKey"/> or <see cref="ExternalSigner"/>
/// must be supplied.
/// </summary>
public sealed class PdfSignatureSettings
{
    /// <summary>
    /// The signing certificate. When neither <see cref="ExternalPrivateKey"/> nor
    /// <see cref="ExternalSigner"/> is set, this certificate must include a private key.
    /// </summary>
    public required X509Certificate2 Certificate { get; init; }

    /// <summary>
    /// An externally-held private key to sign with, for certificates whose key is not
    /// attached to <see cref="Certificate"/> (for example, a certificate fetched from a
    /// cloud key vault, or a key held on a PKCS#11 device without Windows CNG integration).
    /// When <see langword="null"/> (the default), the key attached to <see cref="Certificate"/>
    /// is used instead, unless <see cref="ExternalSigner"/> is set. <see cref="Certificate"/>
    /// is still required in either case, for the public key, subject, and certificate chain.
    /// </summary>
    public AsymmetricAlgorithm? ExternalPrivateKey { get; init; }

    /// <summary>
    /// An asynchronous external signer to sign with — for a cloud KMS or remote HSM where
    /// the signing call itself is a network round-trip (Azure Key Vault, AWS KMS, GCP KMS,
    /// some PKCS#11 setups), and blocking a thread on <see cref="ExternalPrivateKey"/> to
    /// bridge that call is undesirable. Only supported via <c>SignAsync</c>; the synchronous
    /// <c>Sign</c> overloads throw when this is set. When both <see cref="ExternalSigner"/>
    /// and <see cref="ExternalPrivateKey"/> are set, <see cref="ExternalSigner"/> takes
    /// precedence. <see cref="Certificate"/> is still required, for the public key, subject,
    /// and certificate chain.
    /// </summary>
    public IExternalSigner? ExternalSigner { get; init; }

    /// <summary>Optional signer name written to /Name in the signature dictionary.</summary>
    /// <remarks>
    /// <strong>No value is refused, and no character is rewritten into another.</strong> The value
    /// is written as a PDF text string (ISO 32000-2, Table 255). A value made only of U+0020 to
    /// U+007E, U+00A1 to U+00FF except U+00AD, and U+0000 to U+0017 is written one byte per
    /// character, unless it begins with U+00FE U+00FF or U+00EF U+00BB U+00BF. Any other
    /// value, including one with a character above U+00FF, is written as UTF-16BE with a byte
    /// order mark and reaches the file as written.
    /// <para><strong>Do not</strong> pass a lone UTF-16 surrogate or U+001B. A lone surrogate is
    /// not a character and is written as U+FFFD. U+001B is written as it stands, and a reader takes
    /// U+001B inside a UTF-16BE string for the start of a language escape sequence (7.9.2.2.2).
    /// Version 3.0 will refuse both.</para>
    /// <para>A <see langword="null"/> or empty value is ignored: the entry is not written.</para>
    /// </remarks>
    public string? SignerName { get; init; }

    /// <summary>Optional reason for signing, written to /Reason.</summary>
    /// <remarks>
    /// <strong>No value is refused, and no character is rewritten into another.</strong> The value
    /// is written as a PDF text string (ISO 32000-2, Table 255). A value made only of U+0020 to
    /// U+007E, U+00A1 to U+00FF except U+00AD, and U+0000 to U+0017 is written one byte per
    /// character, unless it begins with U+00FE U+00FF or U+00EF U+00BB U+00BF. Any other
    /// value, including one with a character above U+00FF, is written as UTF-16BE with a byte
    /// order mark and reaches the file as written.
    /// <para><strong>Do not</strong> pass a lone UTF-16 surrogate or U+001B. A lone surrogate is
    /// not a character and is written as U+FFFD. U+001B is written as it stands, and a reader takes
    /// U+001B inside a UTF-16BE string for the start of a language escape sequence (7.9.2.2.2).
    /// Version 3.0 will refuse both.</para>
    /// <para>A <see langword="null"/> or empty value is ignored: the entry is not written.</para>
    /// </remarks>
    public string? Reason { get; init; }

    /// <summary>Optional signing location, written to /Location.</summary>
    /// <remarks>
    /// <strong>No value is refused, and no character is rewritten into another.</strong> The value
    /// is written as a PDF text string (ISO 32000-2, Table 255). A value made only of U+0020 to
    /// U+007E, U+00A1 to U+00FF except U+00AD, and U+0000 to U+0017 is written one byte per
    /// character, unless it begins with U+00FE U+00FF or U+00EF U+00BB U+00BF. Any other
    /// value, including one with a character above U+00FF, is written as UTF-16BE with a byte
    /// order mark and reaches the file as written.
    /// <para><strong>Do not</strong> pass a lone UTF-16 surrogate or U+001B. A lone surrogate is
    /// not a character and is written as U+FFFD. U+001B is written as it stands, and a reader takes
    /// U+001B inside a UTF-16BE string for the start of a language escape sequence (7.9.2.2.2).
    /// Version 3.0 will refuse both.</para>
    /// <para>A <see langword="null"/> or empty value is ignored: the entry is not written.</para>
    /// </remarks>
    public string? Location { get; init; }

    /// <summary>Optional contact information, written to /ContactInfo.</summary>
    /// <remarks>
    /// <strong>No value is refused, and no character is rewritten into another.</strong> The value
    /// is written as a PDF text string (ISO 32000-2, Table 255). A value made only of U+0020 to
    /// U+007E, U+00A1 to U+00FF except U+00AD, and U+0000 to U+0017 is written one byte per
    /// character, unless it begins with U+00FE U+00FF or U+00EF U+00BB U+00BF. Any other
    /// value, including one with a character above U+00FF, is written as UTF-16BE with a byte
    /// order mark and reaches the file as written.
    /// <para><strong>Do not</strong> pass a lone UTF-16 surrogate or U+001B. A lone surrogate is
    /// not a character and is written as U+FFFD. U+001B is written as it stands, and a reader takes
    /// U+001B inside a UTF-16BE string for the start of a language escape sequence (7.9.2.2.2).
    /// Version 3.0 will refuse both.</para>
    /// <para>A <see langword="null"/> or empty value is ignored: the entry is not written.</para>
    /// </remarks>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// Signing time. When null, <see cref="DateTimeOffset.UtcNow"/> is used at sign time.
    /// </summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>
    /// Reserved space in bytes for the DER-encoded CMS signature blob in the /Contents
    /// hex string. Default is 8192. Increase if signing with a very long certificate chain.
    /// </summary>
    public int EstimatedSignatureSizeBytes { get; init; } = 8192;

    /// <summary>
    /// PDF signature sub-filter, written as the signature dictionary's <c>/SubFilter</c>. Default is
    /// <c>"ETSI.CAdES.detached"</c> (PAdES). Use <c>"adbe.pkcs7.detached"</c> for legacy
    /// compatibility.
    /// </summary>
    /// <remarks>
    /// Only the two values this library actually produces a conforming signature for are accepted.
    /// It stays a <see cref="string"/> rather than becoming an enum so that a future sub-filter can
    /// be added without a breaking API change — but it is validated, because the value goes straight
    /// into <c>/SubFilter</c>, and an unrecognised one yields a signature dictionary that names a
    /// format the CMS blob does not match. That is worth failing on at assignment rather than
    /// discovering in a verifier.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The value is not one of the supported sub-filters.
    /// </exception>
    public string SubFilter
    {
        get;
        init
        {
            if (!SupportedSubFilters.Contains(value, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"'{value}' is not a supported signature sub-filter. Use "
                    + $"{string.Join(" or ", SupportedSubFilters.Select(s => $"\"{s}\""))}. The value is "
                    + "written verbatim as the signature dictionary's /SubFilter, so an unrecognised "
                    + "one produces a signature that claims a format its CMS content does not match.",
                    nameof(SubFilter));
            }

            field = value;
        }
    } = SubFilterEtsiCAdESDetached;

    /// <summary><c>ETSI.CAdES.detached</c> — the PAdES sub-filter, and the default.</summary>
    public const string SubFilterEtsiCAdESDetached = "ETSI.CAdES.detached";

    /// <summary><c>adbe.pkcs7.detached</c> — the legacy sub-filter, carrying no ETSI profile obligation.</summary>
    public const string SubFilterAdbePkcs7Detached = "adbe.pkcs7.detached";

    private static readonly string[] SupportedSubFilters =
        [SubFilterEtsiCAdESDetached, SubFilterAdbePkcs7Detached];

    /// <summary>
    /// Whether <see cref="SubFilter"/> commits this signature to the ETSI PAdES profile, which
    /// constrains which signed attributes may appear. One predicate rather than a comparison at
    /// each site, because the CMS is assembled by two independent builders and the last time a
    /// signing rule lived in two places they disagreed (issue #167).
    /// </summary>
    internal bool IsPadesProfile
        => string.Equals(SubFilter, SubFilterEtsiCAdESDetached, StringComparison.Ordinal);

    /// <summary>
    /// Optional RFC 3161 timestamp client. When set, an RFC 3161 <c>TimeStampToken</c> is
    /// obtained over the CMS signature value and embedded as an unsigned attribute
    /// (OID 1.2.840.113549.1.9.16.2.14), producing a PAdES B-T signature.
    /// When <see langword="null"/> (the default), no timestamp is added and the
    /// signature conforms to PAdES B-B.
    /// </summary>
    public ITimestampClient? TimestampClient { get; init; }

    /// <summary>
    /// Zero-based index of the page to which the invisible signature widget annotation
    /// is added. Default is 0 (the first page). An out-of-range value throws
    /// <see cref="ArgumentOutOfRangeException"/> during signing.
    /// </summary>
    public int SignaturePage { get; init; } = 0;

    /// <summary>
    /// PAdES conformance level to produce. Default is <see cref="PadesLevel.B_B"/>.
    /// <list type="bullet">
    ///   <item><see cref="PadesLevel.B_T"/> requires <see cref="TimestampClient"/>.</item>
    ///   <item><see cref="PadesLevel.B_LT"/> requires both <see cref="TimestampClient"/>
    ///     and <see cref="RevocationClient"/>.</item>
    ///   <item><see cref="PadesLevel.B_LTA"/> requires both <see cref="TimestampClient"/>
    ///     and <see cref="RevocationClient"/>.</item>
    /// </list>
    /// </summary>
    public PadesLevel Level { get; init; } = PadesLevel.B_B;

    /// <summary>
    /// Optional revocation client used to fetch OCSP responses and/or CRLs for each
    /// certificate in the signing chain. Required when <see cref="Level"/> is
    /// <see cref="PadesLevel.B_LT"/> or <see cref="PadesLevel.B_LTA"/>.
    /// When <see langword="null"/> (the default), no revocation evidence is collected.
    /// </summary>
    public IRevocationClient? RevocationClient { get; init; }
}
