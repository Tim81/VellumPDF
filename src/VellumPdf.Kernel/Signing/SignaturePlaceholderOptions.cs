// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>
/// Non-cryptographic options used when writing the signature-placeholder structure
/// into the PDF body. Consumed by <see cref="PdfDocument.PrepareForSigning"/>.
/// The actual CMS computation lives in <c>VellumPdf.Signing</c>.
/// </summary>
public sealed class SignaturePlaceholderOptions
{
    /// <summary>
    /// PDF signature sub-filter, written as the signature dictionary's <c>/SubFilter</c>. Default
    /// is <see cref="SubFilterEtsiCAdESDetached"/> (PAdES). Use
    /// <see cref="SubFilterAdbePkcs7Detached"/> for legacy compatibility.
    /// </summary>
    /// <remarks>
    /// Validated for the same reason <c>PdfSignatureSettings.SubFilter</c> is: the value goes
    /// straight into <c>/SubFilter</c>, so an unrecognised one produces a signature dictionary
    /// naming a format its CMS content does not match. This is the second public path to that
    /// dictionary — reachable through <see cref="PdfDocument.PrepareForSigning"/> — and leaving it
    /// unvalidated would have held the rule on only one of the two.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not one of the supported sub-filters.</exception>
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
    /// Reserved space in bytes for the DER-encoded CMS signature blob in the /Contents
    /// hex string. Default is 8192.
    /// </summary>
    public int EstimatedSignatureSizeBytes { get; init; } = 8192;

    /// <summary>Optional signer name written to /Name in the signature dictionary.</summary>
    /// <remarks>
    /// <strong>No character is transliterated, and no value is refused.</strong> The value is written as a PDF
    /// text string (ISO 32000-2, Table 255). ASCII and Latin-1 text keeps one byte per character; a value
    /// holding any character PDFDocEncoding cannot carry as the same byte, such as U+0141 or U+2014, is
    /// written as UTF-16BE with a byte order mark and reaches the file as itself. Up to 2.3.3 the value was
    /// encoded with <c>Encoding.Latin1</c>, whose fallback rewrote such a character into a look-alike
    /// (U+0141 became <c>L</c>, U+2014 became <c>-</c>) without reporting it, so a signature could name a
    /// different person than the caller gave.
    /// <para><strong>Do not</strong> pass a lone UTF-16 surrogate: it is not a character, and it is written as
    /// U+FFFD. A <see langword="null"/> or empty value omits the entry.</para>
    /// </remarks>
    public string? SignerName { get; init; }

    /// <summary>Optional reason for signing, written to /Reason.</summary>
    /// <remarks>
    /// <strong>No character is transliterated, and no value is refused.</strong> The value is written as a PDF
    /// text string (ISO 32000-2, Table 255). ASCII and Latin-1 text keeps one byte per character; a value
    /// holding any character PDFDocEncoding cannot carry as the same byte, such as U+0141 or U+2014, is
    /// written as UTF-16BE with a byte order mark and reaches the file as itself. Up to 2.3.3 the value was
    /// encoded with <c>Encoding.Latin1</c>, whose fallback rewrote such a character into a look-alike
    /// (U+0141 became <c>L</c>, U+2014 became <c>-</c>) without reporting it, so a signature could name a
    /// different person than the caller gave.
    /// <para><strong>Do not</strong> pass a lone UTF-16 surrogate: it is not a character, and it is written as
    /// U+FFFD. A <see langword="null"/> or empty value omits the entry.</para>
    /// </remarks>
    public string? Reason { get; init; }

    /// <summary>Optional signing location, written to /Location.</summary>
    /// <remarks>
    /// <strong>No character is transliterated, and no value is refused.</strong> The value is written as a PDF
    /// text string (ISO 32000-2, Table 255). ASCII and Latin-1 text keeps one byte per character; a value
    /// holding any character PDFDocEncoding cannot carry as the same byte, such as U+0141 or U+2014, is
    /// written as UTF-16BE with a byte order mark and reaches the file as itself. Up to 2.3.3 the value was
    /// encoded with <c>Encoding.Latin1</c>, whose fallback rewrote such a character into a look-alike
    /// (U+0141 became <c>L</c>, U+2014 became <c>-</c>) without reporting it, so a signature could name a
    /// different person than the caller gave.
    /// <para><strong>Do not</strong> pass a lone UTF-16 surrogate: it is not a character, and it is written as
    /// U+FFFD. A <see langword="null"/> or empty value omits the entry.</para>
    /// </remarks>
    public string? Location { get; init; }

    /// <summary>Optional contact information, written to /ContactInfo.</summary>
    /// <remarks>
    /// <strong>No character is transliterated, and no value is refused.</strong> The value is written as a PDF
    /// text string (ISO 32000-2, Table 255). ASCII and Latin-1 text keeps one byte per character; a value
    /// holding any character PDFDocEncoding cannot carry as the same byte, such as U+0141 or U+2014, is
    /// written as UTF-16BE with a byte order mark and reaches the file as itself. Up to 2.3.3 the value was
    /// encoded with <c>Encoding.Latin1</c>, whose fallback rewrote such a character into a look-alike
    /// (U+0141 became <c>L</c>, U+2014 became <c>-</c>) without reporting it, so a signature could name a
    /// different person than the caller gave.
    /// <para><strong>Do not</strong> pass a lone UTF-16 surrogate: it is not a character, and it is written as
    /// U+FFFD. A <see langword="null"/> or empty value omits the entry.</para>
    /// </remarks>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// Signing time written to /M. When null, <see cref="DateTimeOffset.UtcNow"/> is
    /// used at the time <see cref="PdfDocument.PrepareForSigning"/> is called.
    /// </summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>
    /// Zero-based index of the page to which the invisible signature widget annotation
    /// is added. Default is 0 (the first page). The caller is responsible for ensuring
    /// this is a valid page index; <see cref="PdfDocument.PrepareForSigning"/> throws
    /// <see cref="ArgumentOutOfRangeException"/> if the value is out of range.
    /// </summary>
    public int SignaturePage { get; init; } = 0;
}
