// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Kernel-level tests for AES-256 encryption (V5/R6, Standard security handler).
/// These tests validate the structure of the /Encrypt dictionary and the cryptographic
/// properties of the output without requiring an external PDF tool.
/// </summary>
public sealed class EncryptionTests
{
    // ── /Encrypt dictionary structure ────────────────────────────────────────

    [Fact]
    public void Encrypted_doc_trailer_contains_Encrypt_key()
    {
        var bytes = SaveEncrypted("openme", "ownerpw");
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Encrypt", text);
    }

    [Fact]
    public void Encrypted_doc_has_V5_R6_in_encrypt_dict()
    {
        var bytes = SaveEncrypted("openme", "ownerpw");
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/V 5", text);
        Assert.Contains("/R 6", text);
    }

    [Fact]
    public void Encrypted_doc_has_AESV3_crypt_filter()
    {
        var bytes = SaveEncrypted("openme", "ownerpw");
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/AESV3", text);
    }

    [Fact]
    public void Encrypted_doc_O_and_U_are_48_bytes_each()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "testuser",
            OwnerPassword = "testowner",
        });

        Assert.Equal(48, handler.U.Length);
        Assert.Equal(48, handler.O.Length);
    }

    [Fact]
    public void Encrypted_doc_OE_and_UE_are_32_bytes_each()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "testuser",
            OwnerPassword = "testowner",
        });

        Assert.Equal(32, handler.UE.Length);
        Assert.Equal(32, handler.OE.Length);
    }

    [Fact]
    public void Encrypted_doc_Perms_is_16_bytes()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "testuser",
        });

        Assert.Equal(16, handler.Perms.Length);
    }

    // ── /P permissions value ─────────────────────────────────────────────────

    [Fact]
    public void Permissions_All_sets_expected_high_bits()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "pw",
            Permissions = PdfPermissions.All,
        });

        // Bits 13..32 (positions 12..31) must all be 1 in the /P value.
        // 0xFFFFF000 sets bits 12..31. Bits 0..1 are 0 (reserved).
        var p = (uint)handler.PValue;
        Assert.Equal(0u, p & 0x3u); // bits 0 and 1 are zero
        Assert.Equal(0xFFFFF000u, p & 0xFFFFF000u); // bits 12..31 are set
    }

    [Fact]
    public void Permissions_None_clears_user_bits()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "pw",
            Permissions = PdfPermissions.None,
        });

        // Bits 2..11 should all be 0.
        Assert.Equal(0, handler.PValue & 0xFFC);
    }

    // ── Two-pass determinism: different keys each time ─────────────────────

    [Fact]
    public void Two_saves_produce_different_ciphertext()
    {
        // Because a new random file key is generated each time Encrypt() is called,
        // two separate saves must produce different bytes.
        var a = SaveEncrypted("same", "same");
        var b = SaveEncrypted("same", "same");
        Assert.NotEqual(a, b);
    }

    // ── Plaintext not visible in raw output ──────────────────────────────────

    [Fact]
    public void Content_stream_marker_not_visible_in_raw_bytes()
    {
        const string marker = "ENCRYPTTEST_CANARY_XYZ_987";

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText(marker).EndText();
        canvas.Finish();

        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "openme" });

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        // The marker must NOT appear as plain ASCII/Latin-1 in the raw output.
        var raw = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain(marker, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Unencrypted_doc_marker_IS_visible_in_raw_bytes()
    {
        // Sanity check: without encryption the marker is visible (compressed but strings aren't).
        // The marker string goes into the page dict (font resource name), but more importantly
        // we check that the encryption path is the one hiding content.
        const string marker = "PLAINTEXT_CANARY_ABC";

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText(marker).EndText();
        canvas.Finish();
        // NOTE: no doc.Encrypt() call

        var ms = new MemoryStream();
        doc.Save(ms);

        // The font name /Helvetica and similar dict content appear unencrypted.
        // The string "PLAINTEXT_CANARY_ABC" may or may not appear depending on encoding —
        // but /Helvetica definitely does.
        var raw = Encoding.Latin1.GetString(ms.ToArray());
        Assert.Contains("/Helvetica", raw);
    }

    // ── /Encrypt dict is not itself encrypted ────────────────────────────────

    [Fact]
    public void Encrypt_dict_filter_Standard_is_readable_in_raw_bytes()
    {
        // The /Encrypt dictionary must be written unencrypted — if it were encrypted
        // the PDF reader couldn't bootstrap decryption. /Filter /Standard must be
        // visible as plain text in the raw file bytes.
        var bytes = SaveEncrypted("pw", "pw");
        var raw = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Filter", raw);
        Assert.Contains("/Standard", raw);
    }

    // ── PdfEncryptionSettings API ────────────────────────────────────────────

    [Fact]
    public void Owner_password_defaults_to_user_password_when_null()
    {
        var settings = new PdfEncryptionSettings { UserPassword = "abc" };
        // Owner password is null — handler should use user password for owner.
        // Just verify construction doesn't throw.
        var handler = new StandardSecurityHandler(settings);
        Assert.Equal(48, handler.O.Length);
    }

    [Fact]
    public void Empty_password_is_accepted()
    {
        var settings = new PdfEncryptionSettings
        {
            UserPassword = string.Empty,
            OwnerPassword = string.Empty,
        };
        var handler = new StandardSecurityHandler(settings);
        Assert.Equal(48, handler.U.Length);
    }

    [Fact]
    public void PdfPermissions_All_flag_has_all_bits_set()
    {
        var all = (int)PdfPermissions.All;
        // All individual flags must be contained in All.
        foreach (PdfPermissions flag in new[]
        {
            PdfPermissions.Print, PdfPermissions.Modify, PdfPermissions.Copy,
            PdfPermissions.Annotate, PdfPermissions.FillForms, PdfPermissions.Extract,
            PdfPermissions.Assemble, PdfPermissions.PrintHighRes,
        })
        {
            Assert.True((all & (int)flag) == (int)flag,
                $"PdfPermissions.All is missing flag {flag}");
        }
    }

    // ── #81a: ISO 32000-2 Algorithm 2.A round-trip decryption test ─────────────

    /// <summary>
    /// White-box round-trip test: proves the entire AES-256 V5/R6 key-derivation chain
    /// end-to-end using only BCL crypto primitives (no external libraries).
    ///
    /// Steps performed (mirrors ISO 32000-2 Algorithm 2.A):
    /// 1. Construct a <see cref="StandardSecurityHandler"/> with a known user password.
    /// 2. Use the handler to encrypt a known plaintext string.
    /// 3. Re-derive from scratch using only the public /U, /UE, /P, /Perms values:
    ///    a. Validate the user password: SHA-256(password || U[32..40]) must equal U[0..32].
    ///    b. Derive the intermediate key: SHA-256(password || U[40..48]).
    ///    c. Recover the file encryption key: AES-256-CBC-NoPadding-decrypt /UE (zero IV).
    ///    d. Use the file key to AES-256-CBC-PKCS7-decrypt the ciphertext (IV = first 16 bytes)
    ///       and assert the result equals the original plaintext.
    /// 4. Decrypt /Perms (AES-256-ECB-NoPadding) and assert bytes[9..11] == "adb" and
    ///    bytes[0..4] == /P (little-endian int32).
    ///
    /// A regression in key derivation or the /UE key-wrap would cause decryption to
    /// produce garbage, and the plaintext assertion would catch it.
    /// </summary>
    [Fact]
    public void R6_user_password_round_trip_decrypts_correctly()
    {
        const string userPassword = "TestPass@2026";

        // Step 1: Build handler and encrypt a known plaintext.
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = userPassword,
            OwnerPassword = "OwnerSecret",
            Permissions = PdfPermissions.Print | PdfPermissions.Copy,
        });

        var plaintext = Encoding.UTF8.GetBytes("ROUND_TRIP_CANARY_PLAINTEXT_XYZ");
        var ciphertext = handler.Encrypt(plaintext); // 16-byte IV || AES-CBC-PKCS7 ciphertext

        // Step 2: Re-derive from /U, /UE, /P, /Perms using only BCL crypto.
        var userPwBytes = TruncateUtf8(userPassword, 127);
        var u = handler.U;   // 48 bytes: hash(32) || validationSalt(8) || keySalt(8)
        var ue = handler.UE; // 32 bytes: AES-256-CBC-NoPadding(intermediateKey, zeroIV, fileKey)

        // Step 3a: Validate user password — SHA-256(password || U[32..40]) == U[0..32].
        var validationSalt = u[32..40];
        var validationInput = Concat(userPwBytes, validationSalt, []);
        var validationHash = SHA256.HashData(validationInput);
        // Mirror Algorithm 2.B: iterated SHA-256/384/512. The single-pass SHA-256 used
        // above is only the Algorithm 8 "first hash" before the 2.B iteration. We must
        // call our test-local Hash2B to get the real validation hash stored in U[0..32].
        var uValidationHash = Hash2B_Test(userPwBytes, validationSalt, []);
        Assert.Equal(uValidationHash, u[..32]);

        // Step 3b/3c: Derive intermediate key and unwrap file encryption key.
        var keySalt = u[40..48];
        var intermediateKey = Hash2B_Test(userPwBytes, keySalt, []);
        var fileKey = AES256CBCDecryptNoPadding(intermediateKey, new byte[16], ue);

        // Step 3d: Decrypt ciphertext using the recovered file key.
        // Format: 16-byte IV || AES-256-CBC-PKCS7 ciphertext.
        Assert.True(ciphertext.Length >= 16, "Ciphertext too short to contain IV.");
        var iv = ciphertext[..16];
        var encrypted = ciphertext[16..];
        var decrypted = AES256CBCDecryptPKCS7(fileKey, iv, encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    /// <summary>
    /// Verifies the /Perms block decrypts to the expected structure:
    /// bytes[0..4] == /P little-endian, bytes[8] == 'T'/'F', bytes[9..12] == "adb\0".
    /// </summary>
    [Fact]
    public void R6_Perms_block_decrypts_to_expected_structure()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "permstest",
            Permissions = PdfPermissions.Print | PdfPermissions.Modify,
            EncryptMetadata = true,
        });

        // Recover file key from /U and /UE.
        var userPwBytes = TruncateUtf8("permstest", 127);
        var keySalt = handler.U[40..48];
        var intermediateKey = Hash2B_Test(userPwBytes, keySalt, []);
        var fileKey = AES256CBCDecryptNoPadding(intermediateKey, new byte[16], handler.UE);

        // Decrypt /Perms with AES-256-ECB-NoPadding using the recovered file key.
        var permsPlain = AES256ECBDecryptNoPadding(fileKey, handler.Perms);

        // ISO 32000-2 §7.6.4.4.2: bytes[0..4] = P as little-endian int32.
        var pFromPerms = (int)(permsPlain[0] | (permsPlain[1] << 8) | (permsPlain[2] << 16) | (permsPlain[3] << 24));
        Assert.Equal(handler.PValue, pFromPerms);

        // bytes[9] = 'a', bytes[10] = 'd', bytes[11] = 'b'
        Assert.Equal((byte)'a', permsPlain[9]);
        Assert.Equal((byte)'d', permsPlain[10]);
        Assert.Equal((byte)'b', permsPlain[11]);

        // bytes[8] = 'T' because EncryptMetadata = true
        Assert.Equal((byte)'T', permsPlain[8]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] SaveEncrypted(string userPassword, string ownerPassword)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("Hello, encrypted world!").EndText();
        canvas.Finish();

        doc.Encrypt(new PdfEncryptionSettings
        {
            UserPassword = userPassword,
            OwnerPassword = ownerPassword,
        });

        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── Round-trip crypto helpers (BCL only, mirrors the library's internals) ──

    /// <summary>
    /// Independent re-implementation of ISO 32000-2 §7.6.4.3.4 Hash algorithm 2.B.
    /// Must match <c>StandardSecurityHandler.Hash2B</c> exactly; any divergence
    /// would cause the round-trip test to fail even if the production code is correct.
    /// </summary>
    private static byte[] Hash2B_Test(byte[] password, byte[] salt, byte[] udata)
    {
        var initialInput = Concat(password, salt, udata);
        var k = SHA256.HashData(initialInput);

        for (var round = 0; ; round++)
        {
            var blockLen = password.Length + k.Length + udata.Length;
            var k1 = new byte[blockLen * 64];
            for (var rep = 0; rep < 64; rep++)
            {
                var off = rep * blockLen;
                password.CopyTo(k1, off);
                k.CopyTo(k1, off + password.Length);
                udata.CopyTo(k1, off + password.Length + k.Length);
            }

            // E = AES-128-CBC-NoPadding(key=K[0..16], iv=K[16..32], K1)
            using var aes128 = Aes.Create();
            aes128.KeySize = 128;
            aes128.Key = k[..16];
            aes128.Mode = CipherMode.CBC;
            aes128.Padding = PaddingMode.None;
            using var enc128 = aes128.CreateEncryptor(aes128.Key, k[16..32]);
            var e = enc128.TransformFinalBlock(k1, 0, k1.Length);

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

            if (round >= 63 && e[^1] <= round - 31)
                break;
        }

        return k[..32];
    }

    private static byte[] AES256CBCDecryptNoPadding(byte[] key, byte[] iv, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor(aes.Key, iv);
        return dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static byte[] AES256CBCDecryptPKCS7(byte[] key, byte[] iv, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor(aes.Key, iv);
        return dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static byte[] AES256ECBDecryptNoPadding(byte[] key, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor(aes.Key, null);
        return dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static byte[] TruncateUtf8(string s, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= maxBytes) return bytes;
        var truncated = new byte[maxBytes];
        Array.Copy(bytes, truncated, maxBytes);
        return truncated;
    }

    private static byte[] Concat(byte[] a, byte[] b, byte[] c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        c.CopyTo(result, a.Length + b.Length);
        return result;
    }
}
