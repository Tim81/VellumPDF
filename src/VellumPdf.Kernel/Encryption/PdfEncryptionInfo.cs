// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Encryption;

/// <summary>
/// The cipher a Standard security handler crypt filter applies, as reported by
/// <see cref="PdfEncryptionInfo.StreamCipher"/>. Mirrors <c>CryptFilterMethod</c> (the internal Kernel
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
    public PdfCipherAlgorithm StreamCipher { get; }

    /// <summary>
    /// The cipher the resolved <c>/StrF</c> crypt filter applies to strings. Usually the same as
    /// <see cref="StreamCipher"/> — producers name one crypt filter for both — but ISO 32000-2 Table 20
    /// lets a document give strings and streams different ones, and this reports what it did.
    /// <para>Never <see cref="PdfCipherAlgorithm.Unsupported"/>: a <c>/StrF</c> naming a filter the
    /// reader cannot apply is refused when the document is opened, because a string that cannot be
    /// decrypted is one the caller would otherwise receive as ciphertext. Only
    /// <see cref="StreamCipher"/> carries that value, where the document still opens and the failure
    /// arrives at the first stream.</para>
    /// </summary>
    public PdfCipherAlgorithm StringCipher { get; }

    /// <summary>
    /// The file encryption key length, in bits. 40, 128 and 256 are what producers write in
    /// practice, but ISO 32000-1 Table 20 allows any multiple of 8 from 40 to 128 at <c>/V</c> 2,
    /// and this reports the length actually IN FORCE rather than rounding to the common three.
    /// That is not always the length the document declares: <c>/V</c> 1 and <c>/R</c> 2 are 40-bit
    /// whatever <c>/Length</c> says, <c>/R</c> 5 and 6 are 256-bit, and at <c>/V</c> 4 the crypt
    /// filter's own length is the one that applies.
    /// </summary>
    public int KeyLengthBits { get; }

    /// <summary><c>/P</c>, decoded into the individual permission flags it grants.</summary>
    /// <remarks>
    /// At <c>/R</c> 5 and 6 this is the copy sealed inside <c>/Perms</c> under the file key, not the
    /// dictionary's <c>/P</c> — the two are inputs to nothing at those revisions, so an editor can
    /// change <c>/P</c> without invalidating anything, and the sealed copy is the one the document's
    /// author chose. Where they disagree, other tools report the dictionary's value: a file whose
    /// <c>/P</c> was edited to forbid printing shows as print-forbidden elsewhere and as
    /// print-allowed here. That is the more defensible answer, and it is deliberately the permissive
    /// one.
    /// <para>
    /// It is not, however, a value an untrusted document cannot choose. <c>/Perms</c> settles a
    /// disagreement only where there is a <c>/Perms</c> to settle it: ISO 32000-2 Table 21 does not
    /// require the entry, and an editor with no key at all can delete it, or corrupt one byte of its
    /// ciphertext so that Algorithm 13's <c>adb</c> marker no longer checks out. Either way this
    /// falls back to the dictionary's <c>/P</c> and reports whatever that editor wrote — as qpdf,
    /// poppler and pdfium also do, since refusing the file outright over an optional entry would
    /// make this the only library that cannot open it. So this is what the author granted on a
    /// document that carries a recoverable <c>/Perms</c>, and what the file claims on one that does
    /// not; a caller enforcing restrictions from an untrusted source cannot tell the two apart from
    /// this property alone.
    /// </para>
    /// </remarks>
    public PdfPermissions Permissions { get; }

    /// <summary>
    /// <c>/EncryptMetadata</c> (default <see langword="true"/> when the key is absent from
    /// <c>/Encrypt</c>): whether the XMP metadata stream is encrypted along with the rest of the
    /// document, or left as cleartext XML.
    /// <para>
    /// Reported as the dictionary states it, and at <c>/R</c> 5 and 6 nothing authenticates that:
    /// the flag stops feeding key derivation above <c>/R</c> 4, so an editor can flip it with no key
    /// and change whether this reader decrypts the metadata stream. Byte 8 of the <c>/Perms</c> block
    /// seals the writer's answer and could detect the edit; it is deliberately not compared, for the
    /// same reason <see cref="Permissions"/> falls back rather than refusing the file. The result is
    /// a corrupted metadata stream, not access to anything.
    /// </para>
    /// </summary>
    public bool EncryptMetadata { get; }

    /// <summary>
    /// <see langword="true"/> when the password supplied to <c>PdfReader.Open</c> authenticated as
    /// the OWNER password; <see langword="false"/> when it authenticated as the user password.
    /// At R&lt;=4 an owner password always also authenticates as the user password (ISO 32000-1
    /// Algorithm 7 recovers the user password from the owner one and re-derives the same file key),
    /// so this is <see langword="true"/> whenever the supplied password is the higher-privilege one,
    /// including the case where one password is both.
    /// </summary>
    public bool IsOwnerAccess { get; }

    /// <summary>
    /// Builds an encryption summary from values already extracted from a parsed <c>/Encrypt</c>
    /// dictionary. Internal, not public: <c>VellumPdf.Reader</c> is the only producer of this type
    /// (via <c>PdfDocumentReader.Encryption</c>) and already has the friend grant, while this
    /// package is Stable — a public eight-parameter constructor would freeze that parameter list at
    /// the next release, so a later addition here would cost an overload rather than a property.
    /// </summary>
    internal PdfEncryptionInfo(
        int v,
        int r,
        PdfCipherAlgorithm cipher,
        PdfCipherAlgorithm stringCipher,
        int keyLengthBits,
        PdfPermissions permissions,
        bool encryptMetadata,
        bool isOwnerAccess)
    {
        V = v;
        R = r;
        StreamCipher = cipher;
        StringCipher = stringCipher;
        KeyLengthBits = keyLengthBits;
        Permissions = permissions;
        EncryptMetadata = encryptMetadata;
        IsOwnerAccess = isOwnerAccess;
    }
}
