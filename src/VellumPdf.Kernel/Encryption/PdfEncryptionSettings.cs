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
    /// </summary>
    public string? UserPassword { get; init; }

    /// <summary>
    /// Owner password. When null the user password is used as the owner password.
    /// </summary>
    public string? OwnerPassword { get; init; }

    /// <summary>Access permissions. Defaults to <see cref="PdfPermissions.All"/>.</summary>
    public PdfPermissions Permissions { get; init; } = PdfPermissions.All;

    /// <summary>
    /// When true (default) the document metadata stream is encrypted.
    /// PDF/A workflows typically require false; most general use cases keep true.
    /// </summary>
    public bool EncryptMetadata { get; init; } = true;
}
