// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;

namespace VellumPdf.Encryption;

/// <summary>
/// AES-256 Standard security handler: V=5, R=6 per ISO 32000-2 §7.6.4.3.
///
/// Password handling note: ISO 32000-2 requires SASLprep normalisation (RFC 4013).
/// This implementation uses UTF-8 bytes of the password truncated to 127 bytes,
/// which is correct for passwords that consist entirely of Unicode codepoints that
/// SASLprep leaves unchanged (i.e. no Unicode normalisation edge-cases). A full
/// SASLprep implementation would require NFC normalisation and prohibited-character
/// filtering — omitted here as a documented simplification.
/// </summary>
public sealed class StandardSecurityHandler : IPdfEncryptor
{
    private readonly byte[] _fileKey; // 32-byte AES-256 file encryption key
    private readonly PdfEncryptionSettings _settings;

    // The computed /U, /O, /UE, /OE, /Perms values, available after construction.
    public byte[] U { get; }
    public byte[] O { get; }
    public byte[] UE { get; }
    public byte[] OE { get; }
    public byte[] Perms { get; }
    public int PValue { get; }

    public StandardSecurityHandler(PdfEncryptionSettings settings)
    {
        _settings = settings;

        // Generate a random 32-byte file encryption key (the master secret).
        _fileKey = new byte[32];
        RandomNumberGenerator.Fill(_fileKey);

        // Derive /P integer (ISO 32000-2 Table 22).
        // Bits 1–2 (positions 0–1 from LSB) are reserved = 0.
        // Bits 7–8 (positions 6–7 from LSB) must be 1 for R >= 3.
        // Bits 13–32 (positions 12–31) are reserved = 1.
        // Pattern: 0xFFFFF000 | enabledLowBits, then clear bits 0 and 1.
        var enabledBits = (int)settings.Permissions;
        PValue = (int)(0xFFFFF000u | (uint)(enabledBits & 0xFFF)) & ~0x3;

        var userPw = PasswordBytes(settings.UserPassword);
        var ownerPw = PasswordBytes(settings.OwnerPassword ?? settings.UserPassword);

        // Algorithm 8: Compute /U and /UE
        Span<byte> uvSalt = stackalloc byte[8];
        Span<byte> ukSalt = stackalloc byte[8];
        RandomNumberGenerator.Fill(uvSalt);
        RandomNumberGenerator.Fill(ukSalt);

        var uHash = Hash2B(userPw, uvSalt, []);
        // U = Hash2B(userPw, validationSalt, empty) || validationSalt(8) || keySalt(8) = 48 bytes
        U = new byte[48];
        uHash.CopyTo(U, 0);
        uvSalt.CopyTo(U.AsSpan(32));
        ukSalt.CopyTo(U.AsSpan(40));

        var uKeyHash = Hash2B(userPw, ukSalt, []);
        UE = AES256CBCEncryptNoPadding(uKeyHash, new byte[16], _fileKey);

        // Algorithm 9: Compute /O and /OE (U is required as "udata")
        Span<byte> ovSalt = stackalloc byte[8];
        Span<byte> okSalt = stackalloc byte[8];
        RandomNumberGenerator.Fill(ovSalt);
        RandomNumberGenerator.Fill(okSalt);

        var oHash = Hash2B(ownerPw, ovSalt, U);
        // O = Hash2B(ownerPw, validationSalt, U) || validationSalt(8) || keySalt(8) = 48 bytes
        O = new byte[48];
        oHash.CopyTo(O, 0);
        ovSalt.CopyTo(O.AsSpan(32));
        okSalt.CopyTo(O.AsSpan(40));

        var oKeyHash = Hash2B(ownerPw, okSalt, U);
        OE = AES256CBCEncryptNoPadding(oKeyHash, new byte[16], _fileKey);

        // Algorithm 10: Compute /Perms
        Perms = ComputePerms(settings.EncryptMetadata);
    }

    // ── IPdfEncryptor ─────────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts <paramref name="data"/> with AES-256-CBC + PKCS#7 padding using the file key.
    /// Output format: 16-byte random IV || ciphertext (ISO 32000-2 §7.6.5.3, V5 — no per-object key).
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> data)
    {
        var iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        using var aes = Aes.Create();
        aes.Key = _fileKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var enc = aes.CreateEncryptor(aes.Key, iv);
        var dataArray = data.ToArray();
        var cipher = enc.TransformFinalBlock(dataArray, 0, dataArray.Length);

        var result = new byte[16 + cipher.Length];
        iv.CopyTo(result, 0);
        cipher.CopyTo(result, 16);
        return result;
    }

    // ── Hash algorithm 2.B (ISO 32000-2 §7.6.4.3.4) ─────────────────────────

    /// <summary>
    /// Hash algorithm 2.B as specified in ISO 32000-2 §7.6.4.3.4.
    /// Used to derive the validation hash stored in /U and /O, and the intermediate
    /// key used to wrap the file encryption key into /UE and /OE.
    /// Returns 32 bytes (first 32 bytes of the final K value).
    /// </summary>
    private static byte[] Hash2B(byte[] password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> udata)
    {
        // Initial K = SHA-256(password || salt || udata)
        var initialInput = Concat(password, salt, udata);
        var k = SHA256.HashData(initialInput);

        for (var round = 0; ; round++)
        {
            // K1 = 64 repetitions of (password || K || udata)
            var blockLen = password.Length + k.Length + udata.Length;
            var k1 = new byte[blockLen * 64];
            for (var rep = 0; rep < 64; rep++)
            {
                var off = rep * blockLen;
                password.CopyTo(k1, off);
                k.CopyTo(k1, off + password.Length);
                udata.CopyTo(k1.AsSpan(off + password.Length + k.Length));
            }

            // E = AES-128-CBC-NoPadding(key=K[0..16], iv=K[16..32], K1)
            var e = AES128CBCEncryptNoPadding(k[..16], k[16..32], k1);

            // Determine next hash based on (sum of first 16 bytes of E) mod 3
            var mod = 0;
            for (var j = 0; j < 16; j++)
                mod += e[j];
            mod %= 3;

            k = mod switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };

            // Termination (ISO 32000-2 §7.6.4.3.4): with the 1-indexed round number
            // r = round + 1, stop when r >= 64 AND last byte of E <= r - 32.
            // In this 0-indexed loop that is: round >= 63 AND e[last] <= round - 31.
            if (round >= 63 && e[^1] <= round - 31)
                break;
        }

        // Return first 32 bytes
        return k[..32];
    }

    // ── Algorithm 10: /Perms ─────────────────────────────────────────────────

    private byte[] ComputePerms(bool encryptMetadata)
    {
        // 16-byte plaintext block per ISO 32000-2 §7.6.4.4.2
        Span<byte> block = stackalloc byte[16];
        // [0..4] = P as little-endian int32
        block[0] = (byte)(PValue & 0xFF);
        block[1] = (byte)((PValue >> 8) & 0xFF);
        block[2] = (byte)((PValue >> 16) & 0xFF);
        block[3] = (byte)((PValue >> 24) & 0xFF);
        // [4..8] = 0xFF × 4
        block[4] = 0xFF;
        block[5] = 0xFF;
        block[6] = 0xFF;
        block[7] = 0xFF;
        // [8] = 'T' if EncryptMetadata else 'F'
        block[8] = (byte)(encryptMetadata ? 'T' : 'F');
        // [9..12] = 'a','d','b' + reserved byte
        block[9] = (byte)'a';
        block[10] = (byte)'d';
        block[11] = (byte)'b';
        // [12..16] = 4 random bytes
        RandomNumberGenerator.Fill(block[12..]);

        return AES256ECBEncryptNoPadding(_fileKey, block);
    }

    // ── AES helpers ──────────────────────────────────────────────────────────

    private static byte[] AES128CBCEncryptNoPadding(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        byte[] data)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor(aes.Key, iv.ToArray());
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AES256CBCEncryptNoPadding(
        byte[] key,
        byte[] iv,
        byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor(aes.Key, iv);
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AES256ECBEncryptNoPadding(byte[] key, ReadOnlySpan<byte> block)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor(aes.Key, null);
        var data = block.ToArray();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    // ── Password encoding ────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a password to bytes. Uses UTF-8, truncated to 127 bytes.
    /// Per the documented SASLprep simplification: passwords that are entirely
    /// ASCII or contain only codepoints SASLprep leaves unchanged will match
    /// correctly. Full SASLprep (NFC + prohibited characters) is not implemented.
    /// </summary>
    private static byte[] PasswordBytes(string? password)
    {
        if (string.IsNullOrEmpty(password)) return [];
        var bytes = Encoding.UTF8.GetBytes(password);
        if (bytes.Length > 127)
        {
            var truncated = new byte[127];
            Array.Copy(bytes, truncated, 127);
            return truncated;
        }
        return bytes;
    }

    // ── Utility ──────────────────────────────────────────────────────────────

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }
}
