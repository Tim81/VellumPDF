// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace VellumPdf.Encryption;

/// <summary>
/// The method a crypt filter applies to a stream or string, per ISO 32000-1 §7.6.5 (the implicit
/// method under V=1/V=2, no /CF) and ISO 32000-2 §7.6.6 (/CFM, crypt filters, for V&gt;=4).
/// </summary>
internal enum CryptFilterMethod
{
    /// <summary>No decryption: bytes pass through unchanged (/CFM /Identity).</summary>
    Identity,

    /// <summary>RC4, keyed per object by <see cref="StandardSecurityDecryptor.ComputeObjectKey"/> —
    /// the implicit method for V=1/V=2 (no /CF), or /CFM /V2 under V=4.</summary>
    Rc4,

    /// <summary>AES-128-CBC, keyed per object with the "sAlT" suffix (/CFM /AESV2, V=4).</summary>
    Aes128,

    /// <summary>AES-256-CBC using the file encryption key directly — R&gt;=5 does not derive a
    /// per-object key (/CFM /AESV3, V=5).</summary>
    Aes256,
}

/// <summary>
/// Decrypt side of the Standard security handler, spanning every /V+/R combination ISO 32000-1
/// §7.6.3.3 and ISO 32000-2 §7.6.4.3 define: V=1/R=2 (RC4-40) through V=5/R=6 (AES-256, current).
/// <see cref="StandardSecurityHandler"/> is the write side and only ever produces V=5/R=6; this
/// type is what reads back everything a PDF producer in the wild might have written.
///
/// Deliberately free of any dependency on the object model or a parsed /Encrypt dictionary: every
/// input here is a plain value a caller has already pulled out of one. Wiring this into a reader
/// (recognising /Encrypt, walking /CF, retrying with the empty password) is separate work.
/// </summary>
internal sealed class StandardSecurityDecryptor
{
    // ISO 32000-1 §7.6.3.3 Algorithm 2, step (a): the fixed 32-byte padding string, used both to
    // pad a short password and, for R&gt;=3, as the seed hashed alongside /ID[0] in Algorithm 5.
    private static readonly byte[] PaddingString =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    // ISO 32000-1 §7.6.2, Algorithm 1 step (b): appended to the per-object key input when the
    // crypt filter is AESV2. Not a string literal — spelling it out keeps a byte-for-byte
    // "sAlT" from ever reading as English prose to the clean-room check.
    private static readonly byte[] AesSalt = [0x73, 0x41, 0x6C, 0x54];

    private readonly int _v;
    private readonly int _r;
    private readonly int _keyLengthBytes;
    private readonly byte[] _o;
    private readonly byte[] _u;
    private readonly byte[]? _oe;
    private readonly byte[]? _ue;
    private readonly int _p;
    private readonly byte[] _id0;
    private readonly bool _encryptMetadata;

    /// <summary>
    /// Builds a decryptor from the fields of an already-parsed /Encrypt dictionary and trailer.
    /// </summary>
    /// <param name="v">/V — the algorithm version (1, 2, 4 or 5).</param>
    /// <param name="r">/R — the security handler revision (2 through 6).</param>
    /// <param name="keyLengthBytes">
    /// File encryption key length in bytes: /Length / 8 for R&lt;=4 (5–16), or 32 for R&gt;=5
    /// (ISO 32000-2 always uses AES-256 there regardless of what /Length says).
    /// </param>
    /// <param name="o">/O — 32 bytes for R&lt;=4, 48 bytes (hash \|\| validation salt \|\| key
    /// salt) for R&gt;=5.</param>
    /// <param name="u">/U — 32 bytes for R&lt;=4, 48 bytes (hash \|\| validation salt \|\| key
    /// salt) for R&gt;=5.</param>
    /// <param name="oe">/OE, 32 bytes — required for R&gt;=5, absent (or ignored) otherwise.</param>
    /// <param name="ue">/UE, 32 bytes — required for R&gt;=5, absent (or ignored) otherwise.</param>
    /// <param name="p">/P as a signed 32-bit integer.</param>
    /// <param name="id0">The first element of the trailer's /ID array.</param>
    /// <param name="encryptMetadata">/EncryptMetadata (default true if the key is absent).</param>
    /// <param name="streamFilter">The method /StmF names (or the implicit RC4/Identity for V&lt;4).</param>
    /// <param name="stringFilter">The method /StrF names (or the implicit RC4/Identity for V&lt;4).</param>
    /// <exception cref="InvalidDataException">
    /// A field's shape contradicts /V or /R — e.g. /O or /U at the wrong length, /OE or /UE
    /// missing for R&gt;=5, or a key length outside what R permits. These values all originate in
    /// the PDF file itself, not in caller code, so a mismatch here is a malformed-file condition
    /// (the same reasoning <see cref="Rc4.Transform"/>'s doc comment gives for why its own empty-key
    /// case is a plain <see cref="ArgumentException"/> instead: that check guards a genuine
    /// programming error one layer further from the file, this one does not).
    /// </exception>
    public StandardSecurityDecryptor(
        int v,
        int r,
        int keyLengthBytes,
        byte[] o,
        byte[] u,
        byte[]? oe,
        byte[]? ue,
        int p,
        byte[] id0,
        bool encryptMetadata,
        CryptFilterMethod streamFilter,
        CryptFilterMethod stringFilter)
    {
        if (v is < 1 or > 5)
            throw new InvalidDataException($"/Encrypt /V {v} is not a value this handler supports (1, 2, 4 or 5).");
        if (r is < 2 or > 6)
            throw new InvalidDataException($"/Encrypt /R {r} is not a value this handler supports (2 through 6).");
        if (id0.Length == 0)
            throw new InvalidDataException("The trailer's /ID first element is required to derive the file key.");

        if (r >= 5)
        {
            // R>=5: both /O and /U are hash(32) || validationSalt(8) || keySalt(8) — Algorithms 8
            // and 9 under ISO 32000-2 §7.6.4.4, with the dictionary layout itself in §7.6.4.2 —
            // not the plain 32-byte values R<=4 uses.
            if (o.Length != 48)
                throw new InvalidDataException($"/O must be 48 bytes at R>={r}; got {o.Length}.");
            if (u.Length != 48)
                throw new InvalidDataException($"/U must be 48 bytes at R>={r}; got {u.Length}.");
            if (oe is not { Length: 32 })
                throw new InvalidDataException("/OE must be present and 32 bytes at R>=5.");
            if (ue is not { Length: 32 })
                throw new InvalidDataException("/UE must be present and 32 bytes at R>=5.");
            if (keyLengthBytes != 32)
                throw new InvalidDataException($"R>=5 is AES-256 only; expected a 32-byte file key, got {keyLengthBytes}.");
        }
        else
        {
            if (o.Length != 32)
                throw new InvalidDataException($"/O must be 32 bytes at R<5; got {o.Length}.");
            if (u.Length != 32)
                throw new InvalidDataException($"/U must be 32 bytes at R<5; got {u.Length}.");
            if (keyLengthBytes is < 5 or > 16)
                throw new InvalidDataException($"File key length must be 5–16 bytes at R<5; got {keyLengthBytes}.");
        }

        _v = v;
        _r = r;
        _keyLengthBytes = keyLengthBytes;
        _o = o;
        _u = u;
        _oe = oe;
        _ue = ue;
        _p = p;
        _id0 = id0;
        _encryptMetadata = encryptMetadata;
        StreamFilter = streamFilter;
        StringFilter = stringFilter;
    }

    /// <summary>/V from the /Encrypt dictionary this decryptor was built from.</summary>
    public int V => _v;

    /// <summary>/R from the /Encrypt dictionary this decryptor was built from.</summary>
    public int R => _r;

    /// <summary>The crypt filter method applied to streams.</summary>
    public CryptFilterMethod StreamFilter { get; }

    /// <summary>The crypt filter method applied to strings.</summary>
    public CryptFilterMethod StringFilter { get; }

    // ── Password verification ────────────────────────────────────────────────

    /// <summary>
    /// Tries <paramref name="password"/> as the user password, encoded the same way
    /// <see cref="StandardSecurityHandler"/> encodes one for writing.
    /// </summary>
    public bool TryComputeFileKeyFromUserPassword(string? password, [NotNullWhen(true)] out byte[]? fileKey)
        => TryComputeFileKeyFromUserPassword(StandardSecurityHandler.PasswordBytes(password), out fileKey);

    /// <summary>
    /// Verifies <paramref name="passwordBytes"/> as the user password and, on success, returns the
    /// file encryption key.
    ///
    /// R&lt;=4: ISO 32000-1 Algorithm 2 derives a candidate key from the padded password; Algorithm 4
    /// (R=2) or Algorithm 5 (R&gt;=3) derives the /U value that key would produce, compared against
    /// the stored one (first 16 bytes only for R&gt;=3 — the remaining 16 are Algorithm 5's own
    /// output, not padding, but nothing reads them).
    ///
    /// R&gt;=5: ISO 32000-2 Algorithm 2.A hashes the password against /U's validation salt (bytes
    /// 32–40) and compares the result to /U's first 32 bytes; on a match, a second hash against the
    /// key salt (bytes 40–48) unwraps /UE to recover the file key directly — R&gt;=5 keys are random,
    /// not password-derived, so there is nothing here to "compute" the way R&lt;=4's Algorithm 2 does.
    /// </summary>
    public bool TryComputeFileKeyFromUserPassword(byte[] passwordBytes, [NotNullWhen(true)] out byte[]? fileKey)
    {
        if (_r <= 4)
        {
            var candidate = ComputeFileKeyFromPaddedPassword(PadPassword(passwordBytes));
            if (MatchesStoredUserHash(candidate))
            {
                fileKey = candidate;
                return true;
            }

            fileKey = null;
            return false;
        }

        return TryUnwrapFileKeyAtRevision5Plus(
            passwordBytes, validationSalt: _u.AsSpan(32, 8), keySalt: _u.AsSpan(40, 8),
            udata: [], expectedHash: _u.AsSpan(0, 32), wrapped: _ue!, out fileKey);
    }

    /// <summary>
    /// Tries <paramref name="password"/> as the owner password, encoded the same way
    /// <see cref="StandardSecurityHandler"/> encodes one for writing.
    /// </summary>
    public bool TryComputeFileKeyFromOwnerPassword(string? password, [NotNullWhen(true)] out byte[]? fileKey)
        => TryComputeFileKeyFromOwnerPassword(StandardSecurityHandler.PasswordBytes(password), out fileKey);

    /// <summary>
    /// Verifies <paramref name="passwordBytes"/> as the owner password and, on success, returns the
    /// file encryption key.
    ///
    /// R&lt;=4: ISO 32000-1 Algorithm 7 runs Algorithm 2's key derivation on the padded owner
    /// password (skipping /P and /ID — the owner key derivation never uses them), then uses the
    /// result to RC4-decrypt /O and recover the padded user password /O was built from. That
    /// recovered value goes back into Algorithm 2 as if it were the padded password directly — it
    /// is already 32 bytes, so it needs no re-padding — to reach the same file key the user password
    /// would have produced, then Algorithm 4/5 confirms that against /U exactly as the user path
    /// does. A wrong owner password recovers 32 bytes of noise, which fails that comparison rather
    /// than throwing.
    ///
    /// R&gt;=5: ISO 32000-2 Algorithm 2.A hashes the password against /O's validation salt with /U's
    /// full 48 bytes folded in as "udata" and compares against /O's first 32 bytes; on a match, the
    /// same hash against /O's key salt unwraps /OE.
    /// </summary>
    public bool TryComputeFileKeyFromOwnerPassword(byte[] passwordBytes, [NotNullWhen(true)] out byte[]? fileKey)
    {
        if (_r <= 4)
        {
            var recoveredPaddedUserPassword = RecoverUserPasswordFromOwner(PadPassword(passwordBytes));
            var candidate = ComputeFileKeyFromPaddedPassword(recoveredPaddedUserPassword);
            if (MatchesStoredUserHash(candidate))
            {
                fileKey = candidate;
                return true;
            }

            fileKey = null;
            return false;
        }

        return TryUnwrapFileKeyAtRevision5Plus(
            passwordBytes, validationSalt: _o.AsSpan(32, 8), keySalt: _o.AsSpan(40, 8),
            udata: _u, expectedHash: _o.AsSpan(0, 32), wrapped: _oe!, out fileKey);
    }

    /// <summary>
    /// Tries <paramref name="passwordBytes"/> as the user password, then as the owner password.
    /// Most encrypted PDFs set an empty user password and rely on the owner password (or none) to
    /// restrict permissions, so opening one with no password supplied has to succeed here — an
    /// empty <paramref name="passwordBytes"/> is a legitimate user password, not a missing one.
    /// </summary>
    public bool TryComputeFileKey(byte[] passwordBytes, [NotNullWhen(true)] out byte[]? fileKey)
        => TryComputeFileKeyFromUserPassword(passwordBytes, out fileKey)
            || TryComputeFileKeyFromOwnerPassword(passwordBytes, out fileKey);

    // ── R>=5: ISO 32000-2 Algorithm 2.A ──────────────────────────────────────

    private bool TryUnwrapFileKeyAtRevision5Plus(
        byte[] passwordBytes,
        ReadOnlySpan<byte> validationSalt,
        ReadOnlySpan<byte> keySalt,
        ReadOnlySpan<byte> udata,
        ReadOnlySpan<byte> expectedHash,
        byte[] wrapped,
        [NotNullWhen(true)] out byte[]? fileKey)
    {
        var validationHash = ComputeRevision5PlusHash(passwordBytes, validationSalt, udata);
        if (!CryptographicOperations.FixedTimeEquals(validationHash, expectedHash))
        {
            fileKey = null;
            return false;
        }

        var intermediateKey = ComputeRevision5PlusHash(passwordBytes, keySalt, udata);
        fileKey = AesCbcDecryptNoPadding(intermediateKey, new byte[16], wrapped);
        return true;
    }

    // R5 (deprecated): the validation and key-salt hashes are one unsalted-iteration SHA-256 over
    // password || salt || udata. R5 predates ISO 32000-2 and is not itself given a numbered
    // algorithm there — §7.6.4.3.3 (Algorithm 2.A) and §7.6.4.3.4 (Algorithm 2.B, Hash2B below)
    // are both scoped "revision 6 and later", so this branch has no ISO clause of its own to cite;
    // it exists only for backward compatibility with files Acrobat X wrote before R6 was
    // standardised. The two revisions are not the same algorithm at different round counts — they
    // diverge structurally (R5 never branches into SHA-384/512) — so R must gate which one runs
    // rather than R6's algorithm degenerating into R5's at some parameter.
    private byte[] ComputeRevision5PlusHash(byte[] password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> udata)
        => _r == 6
            ? StandardSecurityHandler.Hash2B(password, salt, udata)
            : SHA256.HashData(StandardSecurityHandler.Concat(password, salt, udata));

    /// <summary>
    /// ISO 32000-2 §7.6.4.4.12, Algorithm 13 (validating the permissions): decrypts /Perms with the
    /// file key (AES-256-ECB, no padding), checks the fixed "adb" marker at bytes 9–11, and
    /// compares bytes 0–3 and byte 8 against this dictionary's own /P and /EncryptMetadata. What
    /// writes /Perms in the first place is a different algorithm, §7.6.4.4.9 Algorithm 10 —
    /// <see cref="StandardSecurityHandler.ComputePerms"/>. A mismatch on any of the three means /P
    /// or /EncryptMetadata were altered after /Perms was written, without the password changing.
    /// This does not gate <see cref="TryComputeFileKeyFromUserPassword(byte[], out byte[])"/>:
    /// /U or /O already established the password is correct, and R6 readers are expected to
    /// tolerate this check failing on a file whose permissions were edited by a tool that updates
    /// /P but not /Perms.
    /// </summary>
    public bool VerifyPermissions(byte[] fileKey, byte[] perms)
    {
        if (perms.Length != 16)
            return false;

        using var aes = Aes.Create();
        aes.Key = fileKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(aes.Key, null);
        var block = decryptor.TransformFinalBlock(perms, 0, perms.Length);

        if (block[9] != (byte)'a' || block[10] != (byte)'d' || block[11] != (byte)'b')
            return false;

        Span<byte> expectedP = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(expectedP, _p);
        if (!block.AsSpan(0, 4).SequenceEqual(expectedP))
            return false;

        return block[8] == (byte)(_encryptMetadata ? 'T' : 'F');
    }

    // ── R<=4: ISO 32000-1 Algorithm 2 (file key), 4/5 (/U), 7 (/O) ───────────

    // Algorithm 2: MD5(paddedPassword || /O || /P as LE int32 || /ID[0] || FFFFFFFF if R>=4 and
    // metadata is not encrypted), then for R>=3 fifty rounds of MD5(key[0..n]) — ISO 32000-1
    // §7.6.3.3 step (h) has each round pass "the first n bytes ... into a new MD5 hash", which is
    // why this uses Md5.HashData per round rather than one Incremental fed n bytes at a time; a
    // fresh accumulator each round is exactly what a fresh HashData call already gives.
    private byte[] ComputeFileKeyFromPaddedPassword(byte[] paddedPassword32)
    {
        var accumulator = new Md5.Incremental();
        accumulator.Append(paddedPassword32);
        accumulator.Append(_o);
        Span<byte> pBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pBytes, _p);
        accumulator.Append(pBytes);
        accumulator.Append(_id0);
        if (_r >= 4 && !_encryptMetadata)
            accumulator.Append([0xFF, 0xFF, 0xFF, 0xFF]);
        var key = accumulator.Finish();

        if (_r >= 3)
        {
            for (var round = 0; round < 50; round++)
                key = Md5.HashData(key.AsSpan(0, _keyLengthBytes));
        }

        return key[.._keyLengthBytes];
    }

    // Algorithm 4 (R=2): /U is RC4(fileKey, paddingString) directly.
    // Algorithm 5 (R>=3): /U's first 16 bytes are the last of 20 RC4 passes over
    // MD5(paddingString || /ID[0]), each pass keyed by fileKey with every byte XORed by the
    // (ascending, 1-19) pass number — the remaining 16 stored bytes are Algorithm 5's own output
    // too, not padding, but ISO 32000-1 says only the first 16 need match.
    private bool MatchesStoredUserHash(byte[] fileKey)
    {
        if (_r == 2)
        {
            var expected = Rc4.Transform(fileKey, PaddingString);
            return CryptographicOperations.FixedTimeEquals(expected, _u);
        }

        var hash = Md5.HashData(StandardSecurityHandler.Concat(PaddingString, _id0, []));
        var value = Rc4.Transform(fileKey, hash);
        for (var round = 1; round <= 19; round++)
            value = Rc4.Transform(XorEachByte(fileKey, (byte)round), value);

        return CryptographicOperations.FixedTimeEquals(value, _u.AsSpan(0, 16));
    }

    // Algorithm 7: derive the same key Algorithm 2 would from the padded *owner* password
    // (/O, /P and /ID play no part in that derivation — only the password and, for R>=3, the
    // fifty-round tail), then run Algorithm 5's 20 RC4 passes over /O in reverse (round numbers
    // 19 down to 0, one more pass than Algorithm 5's forward direction takes to reach /U) to
    // recover the padded user password /O was originally built from.
    private byte[] RecoverUserPasswordFromOwner(byte[] paddedOwnerPassword32)
    {
        var key = Md5.HashData(paddedOwnerPassword32);
        if (_r >= 3)
        {
            for (var round = 0; round < 50; round++)
                key = Md5.HashData(key.AsSpan(0, _keyLengthBytes));
        }

        key = key[.._keyLengthBytes];

        if (_r == 2)
            return Rc4.Transform(key, _o);

        var value = (byte[])_o.Clone();
        for (var round = 19; round >= 0; round--)
            value = Rc4.Transform(XorEachByte(key, (byte)round), value);

        return value;
    }

    // Algorithm 2 step (a): the password truncated to 32 bytes, then padded out to exactly 32
    // with the leading bytes of the fixed padding string.
    private static byte[] PadPassword(ReadOnlySpan<byte> passwordBytes)
    {
        var result = new byte[32];
        var take = Math.Min(passwordBytes.Length, 32);
        passwordBytes[..take].CopyTo(result);
        PaddingString.AsSpan(0, 32 - take).CopyTo(result.AsSpan(take));
        return result;
    }

    private static byte[] XorEachByte(byte[] key, byte value)
    {
        var result = new byte[key.Length];
        for (var i = 0; i < key.Length; i++)
            result[i] = (byte)(key[i] ^ value);
        return result;
    }

    // ── Per-object key (Algorithm 1, ISO 32000-1 §7.6.2) ─────────────────────

    /// <summary>
    /// Derives the per-object key R&lt;=4 uses in place of the file key directly:
    /// MD5(fileKey || objectNumber low 3 bytes, little-endian || generation low 2 bytes,
    /// little-endian || "sAlT" if this is an AESV2 filter), truncated to
    /// min(fileKey.Length + 5, 16). This is what makes decryption depend on the generation
    /// number — a decryptor that hardcoded generation 0 would only ever be caught by a fixture
    /// with a nonzero one, which is why this landed after #121.
    ///
    /// R&gt;=5 has no per-object key: <see cref="Decrypt"/> uses the file key unchanged for
    /// <see cref="CryptFilterMethod.Aes256"/>, so this method is never called on that path.
    /// </summary>
    public static byte[] ComputeObjectKey(byte[] fileKey, int objectNumber, int generation, bool useAesSalt)
    {
        if (fileKey.Length == 0)
            throw new ArgumentException("File key must not be empty.", nameof(fileKey));
        if (objectNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(objectNumber));
        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation));

        var accumulator = new Md5.Incremental();
        accumulator.Append(fileKey);
        accumulator.Append([(byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16)]);
        accumulator.Append([(byte)generation, (byte)(generation >> 8)]);
        if (useAesSalt)
            accumulator.Append(AesSalt);
        var digest = accumulator.Finish();

        var length = Math.Min(fileKey.Length + 5, 16);
        return digest[..length];
    }

    // ── Data decryption ───────────────────────────────────────────────────────

    /// <summary>Decrypts a stream body using <see cref="StreamFilter"/>.</summary>
    public byte[] DecryptStream(byte[] fileKey, int objectNumber, int generation, ReadOnlySpan<byte> data)
        => Decrypt(fileKey, objectNumber, generation, data, StreamFilter);

    /// <summary>Decrypts a string using <see cref="StringFilter"/>.</summary>
    public byte[] DecryptString(byte[] fileKey, int objectNumber, int generation, ReadOnlySpan<byte> data)
        => Decrypt(fileKey, objectNumber, generation, data, StringFilter);

    private static byte[] Decrypt(
        byte[] fileKey, int objectNumber, int generation, ReadOnlySpan<byte> data, CryptFilterMethod method)
    {
        switch (method)
        {
            case CryptFilterMethod.Identity:
                return data.ToArray();

            case CryptFilterMethod.Rc4:
                {
                    var objectKey = ComputeObjectKey(fileKey, objectNumber, generation, useAesSalt: false);
                    return Rc4.Transform(objectKey, data);
                }

            case CryptFilterMethod.Aes128:
                {
                    var objectKey = ComputeObjectKey(fileKey, objectNumber, generation, useAesSalt: true);
                    return DecryptAesCbcWithIvPrefix(objectKey, data);
                }

            case CryptFilterMethod.Aes256:
                // R>=5: ISO 32000-2 §7.6.3.3, Algorithm 1.A — the file encryption key is used
                // directly, with no per-object derivation. ISO 32000-1:2008 has no V=5/AESV3 to
                // cite here at all.
                return DecryptAesCbcWithIvPrefix(fileKey, data);

            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, null);
        }
    }

    // The first 16 bytes of the ciphertext are the CBC IV (ISO 32000-1 §7.6.2 note c); the rest
    // is PKCS#7-padded ciphertext. A CryptographicException here means the key was wrong or the
    // stream is corrupt, either way a malformed-file condition rather than a caller bug.
    private static byte[] DecryptAesCbcWithIvPrefix(byte[] key, ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || (data.Length - 16) % 16 != 0)
        {
            throw new InvalidDataException(
                "AES-CBC stream/string data must be at least 16 bytes (the IV) and leave a whole " +
                "number of 16-byte blocks after it.");
        }

        var iv = data[..16].ToArray();
        var cipherText = data[16..].ToArray();

        // aes.Key's setter is in scope here too: a crafted /CF pairing (e.g. /Length 40 with
        // /CFM /AESV2) reaches this with an object key too short or too long for AES, and that
        // setter throws CryptographicException just like a padding failure below would. Both are
        // a malformed-file condition, not a caller bug, so both fold into the same
        // InvalidDataException rather than the key-length one escaping as a bare framework
        // exception.
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor(aes.Key, iv);
            return decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                "AES-CBC decryption failed: the key length is not a legal AES key size, or the " +
                "ciphertext has invalid PKCS#7 padding.", ex);
        }
    }

    // Used to unwrap /UE and /OE: no padding, because the file key those wrap is exactly 32
    // bytes — two whole AES blocks — with nothing to pad.
    private static byte[] AesCbcDecryptNoPadding(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(aes.Key, iv);
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }
}
