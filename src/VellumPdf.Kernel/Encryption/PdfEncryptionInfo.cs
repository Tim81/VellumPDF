// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Encryption;

/// <summary>
/// The cipher a Standard security handler crypt filter applies, as reported by
/// <see cref="PdfEncryptionInfo.Cipher"/>. Mirrors <c>CryptFilterMethod</c> (the internal Kernel
/// enum the decrypt side is keyed on) without exposing that type or any key material.
/// </summary>
public enum PdfCipherAlgorithm
{
    /// <summary>No encryption applied by the resolved stream crypt filter (<c>/CFM /Identity</c>,
    /// or a document whose <c>/StmF</c> is absent and therefore defaults to Identity).</summary>
    Identity,

    /// <summary>RC4, keyed per object — the implicit method under V=1/V=2, or <c>/CFM /V2</c> under V&gt;=4.</summary>
    Rc4,

    /// <summary>AES-128-CBC, keyed per object (<c>/CFM /AESV2</c>).</summary>
    Aes128,

    /// <summary>AES-256-CBC using the file encryption key directly (<c>/CFM /AESV3</c>).</summary>
    Aes256,

    /// <summary>
    /// <c>/StmF</c> names a <c>/CF</c> entry the document does not define, or a <c>/CFM</c> this
    /// handler does not implement. The document opened (password authentication only needs
    /// <c>/O</c>, <c>/U</c>, <c>/OE</c>, <c>/UE</c>, none of which touch <c>/CF</c>), but decoding
    /// a stream will throw.
    /// </summary>
    Unsupported,
}

/// <summary>
/// Read-only summary of a decrypted document's Standard security handler settings, as recorded
/// in its <c>/Encrypt</c> dictionary. Exposes no key material: not the file encryption key, not
/// <c>/O</c>/<c>/U</c>/<c>/OE</c>/<c>/UE</c>, nothing an attacker could use to skip password
/// verification on a copy of the file.
/// </summary>
public sealed class PdfEncryptionInfo
{
    /// <summary><c>/V</c> — the algorithm version (1, 2, 4, or 5).</summary>
    public int V { get; }

    /// <summary><c>/R</c> — the security handler revision (2 through 6).</summary>
    public int R { get; }

    /// <summary>The cipher the resolved <c>/StmF</c> crypt filter applies to streams.</summary>
    public PdfCipherAlgorithm Cipher { get; }

    /// <summary>The file encryption key length, in bits (40, 128, or 256).</summary>
    public int KeyLengthBits { get; }

    /// <summary><c>/P</c>, decoded into the individual permission flags it grants.</summary>
    public PdfPermissions Permissions { get; }

    /// <summary>
    /// <c>/EncryptMetadata</c> (default <see langword="true"/> when the key is absent from
    /// <c>/Encrypt</c>): whether the XMP metadata stream is encrypted along with the rest of the
    /// document, or left as cleartext XML.
    /// </summary>
    public bool EncryptMetadata { get; }

    /// <summary>
    /// <see langword="true"/> when the password supplied to <c>PdfReader.Open</c> authenticated as
    /// the OWNER password; <see langword="false"/> when it authenticated as the user password.
    /// At R&lt;=4 an owner password always also authenticates as the user password (ISO 32000-1
    /// Algorithm 7 recovers the user password from the owner one and re-derives the same file key),
    /// so this is <see langword="true"/> whenever the supplied password is the higher-privilege one,
    /// including the case where the same password serves as both.
    /// </summary>
    public bool IsOwnerAccess { get; }

    /// <summary>Builds an encryption summary from values already extracted from a parsed <c>/Encrypt</c> dictionary.</summary>
    public PdfEncryptionInfo(
        int v,
        int r,
        PdfCipherAlgorithm cipher,
        int keyLengthBits,
        PdfPermissions permissions,
        bool encryptMetadata,
        bool isOwnerAccess)
    {
        V = v;
        R = r;
        Cipher = cipher;
        KeyLengthBits = keyLengthBits;
        Permissions = permissions;
        EncryptMetadata = encryptMetadata;
        IsOwnerAccess = isOwnerAccess;
    }
}
