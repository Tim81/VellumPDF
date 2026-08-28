// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Encryption;

/// <summary>
/// User-facing settings for AES-256 PDF encryption (Standard security handler V5/R6).
/// </summary>
public sealed class PdfEncryptionSettings
{
    /// <summary>
    /// Password required to open the document. Null or empty means no user password
    /// (the document opens without a password but may still restrict permissions).
    ///
    /// <para><b>Character-set note:</b> passwords are encoded as UTF-8 and truncated to
    /// 127 bytes before use. This implementation does not apply SASLprep normalisation
    /// (RFC 4013 / ISO 32000-2 §7.6.4.3). For full interoperability with all PDF readers,
    /// use passwords that consist entirely of printable ASCII characters (U+0020–U+007E);
    /// passwords that contain non-ASCII or Unicode-composed characters may not be accepted
    /// by readers that implement the full SASLprep profile.</para>
    /// </summary>
    public string? UserPassword { get; init; }

    /// <summary>
    /// Owner password, meaning full access — permission restrictions in <see cref="Permissions"/>
    /// bind everyone else. Null falls back to <see cref="UserPassword"/> as the owner password too,
    /// so a document with only a user password still restricts a viewer who supplies it. Empty is
    /// accepted only when <see cref="UserPassword"/> is also empty or null, matching that fallback;
    /// an empty owner password beside a non-empty user password is rejected, because it would derive
    /// <c>/O</c> from nothing and let anyone open the document with no password at owner privilege.
    ///
    /// <para><b>Character-set note:</b> passwords are encoded as UTF-8 and truncated to
    /// 127 bytes before use. This implementation does not apply SASLprep normalisation
    /// (RFC 4013 / ISO 32000-2 §7.6.4.3). For full interoperability with all PDF readers,
    /// use passwords that consist entirely of printable ASCII characters (U+0020–U+007E);
    /// passwords that contain non-ASCII or Unicode-composed characters may not be accepted
    /// by readers that implement the full SASLprep profile.</para>
    /// </summary>
    public string? OwnerPassword { get; init; }

    /// <summary>Access permissions. Defaults to <see cref="PdfPermissions.All"/>.</summary>
    public PdfPermissions Permissions { get; init; } = PdfPermissions.All;

    /// <summary>
    /// When true (default) the XMP metadata stream is encrypted along with the rest of the
    /// document. Setting this false leaves that stream's body as cleartext XML in the saved file,
    /// readable without the password: the title, author, subject, language, creator tool, producer,
    /// and the creation and modification dates. The <c>/Info</c> dictionary's strings and the page
    /// content remain encrypted. Only set this false when metadata-driven cataloguing or indexing over an
    /// encrypted file is a requirement that outweighs that exposure.
    /// </summary>
    public bool EncryptMetadata { get; init; } = true;
}
