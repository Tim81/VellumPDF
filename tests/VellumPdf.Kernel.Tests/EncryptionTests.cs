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
    public void Permissions_reserved_bits_7_and_8_are_always_set()
    {
        // ISO 32000-2 Table 22: bits 7-8 (positions 6-7 from LSB) must be 1 for R >= 3,
        // regardless of the caller's requested permissions. PdfPermissions has no flag
        // at 1<<6/1<<7, so nothing the caller passes can turn these off.
        var allOff = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "pw",
            Permissions = PdfPermissions.None,
        });
        var allOn = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "pw",
            Permissions = PdfPermissions.All,
        });

        Assert.Equal(0xC0, allOff.PValue & 0xC0);
        Assert.Equal(0xC0, allOn.PValue & 0xC0);
    }

    [Fact]
    public void Permissions_None_clears_user_bits()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "pw",
            Permissions = PdfPermissions.None,
        });

        // Bits 2..5 and 8..11 should be 0; bits 6..7 (0xC0) are forced to 1
        // regardless of the requested permissions (ISO 32000-2 Table 22).
        Assert.Equal(0, handler.PValue & 0xF3C);
        Assert.Equal(0xC0, handler.PValue & 0xC0);
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

    // ── #188: PDF/UA-1 + encryption ──────────────────────────────────────────

    [Fact]
    public void PdfUA1_encrypted_doc_still_carries_StructTreeRoot_and_MarkInfo()
    {
        // Regression for #188: the PDF/A-only encryption guard used to catch PdfUA1
        // (Conformance != None), which meant an accessible + encrypted document could
        // never be saved at all. Once allowed, the accessibility structure the writer
        // already builds for Tagged/PdfUA1 documents must still show up unencrypted —
        // /StructTreeRoot, /MarkInfo and their catalog entries are dictionary structure,
        // not string/stream content, so they are never passed through the encryptor.
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfUA1, Tagged = true, Language = "en-US" };
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "openme" });

        var ms = new MemoryStream();
        doc.Save(ms);
        var raw = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/StructTreeRoot", raw, StringComparison.Ordinal);
        Assert.Contains("/MarkInfo", raw, StringComparison.Ordinal);
        Assert.Contains("/Lang", raw, StringComparison.Ordinal);
        Assert.Contains("/DisplayDocTitle true", raw, StringComparison.Ordinal);
    }

    // ── #182: /EncryptMetadata false must exempt the metadata stream body ────

    [Fact]
    public void EncryptMetadata_false_leaves_metadata_stream_body_readable_in_raw_bytes()
    {
        // Regression for #182: /EncryptMetadata false was only ever written into the
        // /Encrypt dict and the /Perms block — nothing exempted the /Metadata object
        // itself, so its XML body was encrypted anyway, contradicting the flag it
        // shipped right next to (ISO 32000-2 §7.6.2). Assert on the actual XML bytes,
        // not just the presence of the /EncryptMetadata key in the dict: the dict key
        // was already present and correct while this bug shipped.
        const string title = "Metadata exemption test";
        const string keywords = "InfoStaysEncryptedWitness";
        using var doc = new PdfDocument();
        doc.Info.Title = title;
        doc.Info.Keywords = keywords;
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "openme", EncryptMetadata = false });

        var ms = new MemoryStream();
        doc.Save(ms);
        var raw = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/EncryptMetadata false", raw, StringComparison.Ordinal);
        Assert.Contains("<?xpacket begin", raw, StringComparison.Ordinal);
        Assert.Contains("<x:xmpmeta", raw, StringComparison.Ordinal);

        // Value level, not just structural markers. The CHANGELOG tells users this flag exposes
        // the title, so pin the title text itself and pin it inside the packet: /Info carries the
        // same string and stays encrypted, so a cleartext hit after <?xpacket can only be the XMP.
        var packet = raw[raw.IndexOf("<?xpacket begin", StringComparison.Ordinal)..];
        Assert.Contains("dc:title", packet, StringComparison.Ordinal);
        Assert.Contains(title, packet, StringComparison.Ordinal);

        // And the exemption has to stay narrow. Widening the predicate to every object leaves the
        // whole document cleartext, which every other assertion here tolerates. Keywords is the
        // witness because XmpMetadataWriter has no pdf:Keywords branch (#199), so it reaches /Info
        // and nowhere else. Search the bytes as written: PdfLiteralString.FromUnicode emits UTF-16BE
        // with a BOM even for ASCII, so searching the plain text passes vacuously and reads as
        // coverage it is not.
        Assert.DoesNotContain(AsWritten(keywords), raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// A metadata string exactly as it lands in the file, mirroring both steps the writer applies:
    /// <see cref="PdfLiteralString.FromUnicode"/> encodes /Info values as UTF-16BE even for pure
    /// ASCII, and <c>WriteTo</c> then escapes <c>(</c>, <c>)</c>, <c>\</c>, LF and CR.
    /// <para>
    /// Mirroring the escaping is what keeps this honest. A needle built from the unescaped bytes
    /// stops being a substring the moment a value contains a character with one of those bytes in
    /// either half — which for a DoesNotContain assertion is a silent pass, indistinguishable
    /// from the property actually holding. Encoding by hand rather than through
    /// <c>Encoding.BigEndianUnicode</c> for the same reason: that would fold a lone surrogate to
    /// U+FFFD, where the writer emits the raw code unit.
    /// </para>
    /// </summary>
    private static string AsWritten(string value)
    {
        var sb = new StringBuilder();
        foreach (var unit in value)
        {
            Append(sb, (char)(unit >> 8));
            Append(sb, (char)(unit & 0xFF));
        }

        return sb.ToString();

        static void Append(StringBuilder sb, char b)
        {
            switch (b)
            {
                case '(' or ')' or '\\': sb.Append('\\').Append(b); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(b); break;
            }
        }
    }

    [Fact]
    public void EncryptMetadata_true_encrypts_the_metadata_stream_body()
    {
        // Inverse of the above: with the default (true), the metadata stream body
        // must NOT be readable in the raw output — otherwise the exemption logic
        // could be exempting every object rather than just /Metadata.
        const string title = "Metadata encryption default test";
        using var doc = new PdfDocument();
        doc.Info.Title = title;
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "openme" });

        var ms = new MemoryStream();
        doc.Save(ms);
        var raw = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/EncryptMetadata true", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("<?xpacket begin", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("<x:xmpmeta", raw, StringComparison.Ordinal);

        // The title reaches both /Info and the XMP packet, and both must be ciphertext here. They
        // need separate searches: the packet holds it as ASCII, while /Info goes through
        // PdfLiteralString.FromUnicode and is UTF-16BE with a BOM even for pure ASCII. Searching
        // only the plain text would never see a cleartext /Info, so it would report a coverage of
        // /Info that it does not have.
        Assert.DoesNotContain(title, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(AsWritten(title), raw, StringComparison.Ordinal);
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
        // Bytes 4-7 are 0xFF, which Algorithm 10 states outright. Nothing in this library reads them
        // back — the reader takes /P from bytes 0-3 and the marker from 9-11 — so only a validator
        // elsewhere would notice them being wrong, which is exactly why they need asserting here.
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, permsPlain[4..8]);

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

    /// <summary>
    /// ISO 32000-2 §7.6.4.3.3 truncates the password to 127 bytes before hashing. Both sides of this
    /// library call the same helper, so a round trip cancels any error in it, and at <c>/R</c> 4 and
    /// below the 32-byte padding truncates first — which leaves this rule observable only against a
    /// UTF-8 password longer than 127 bytes at <c>/R</c> 6, and no fixture has one. Known answers
    /// instead: the boundary itself, one byte either side, and a multi-byte character straddling it,
    /// since truncating mid-character is what a naive character-count implementation gets wrong.
    /// </summary>
    [Theory]
    [InlineData(126, 126)]
    [InlineData(127, 127)]
    [InlineData(128, 127)]
    [InlineData(300, 127)]
    public void PasswordBytes_truncatesAt127Bytes(int asciiLength, int expectedLength)
    {
        var bytes = StandardSecurityHandler.PasswordBytes(new string('p', asciiLength));

        Assert.Equal(expectedLength, bytes.Length);
    }

    /// <summary>
    /// The cut is by BYTES, not characters, and it lands mid-character here: 63 two-byte characters
    /// reach 126 bytes, so the 64th contributes only its lead byte. A character-count truncation
    /// would keep all 64 and hash 128 bytes.
    /// </summary>
    [Fact]
    public void PasswordBytes_truncatingMidCharacter_cutsAt127Bytes()
    {
        var bytes = StandardSecurityHandler.PasswordBytes(new string('ä', 64));

        Assert.Equal(127, bytes.Length);
        Assert.Equal(0xC3, bytes[126]);   // the lead byte of the character the cut splits
    }

    /// <summary>
    /// ISO 32000-2 Table 22 reserves bits 1 and 2 of <c>/P</c> and requires them to be 0.
    /// <c>PdfEncryptionSettings.Permissions</c> is an unvalidated public property, so a caller can
    /// hand in a value with them set; the mask is what stops that reaching the file.
    /// </summary>
    [Fact]
    public void PValue_clearsTheReservedLowBits()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "u",
            OwnerPassword = "o",
            Permissions = (PdfPermissions)3 | PdfPermissions.Print,
        });

        Assert.Equal(0, handler.PValue & 0x3);
    }

    /// <summary>
    /// The write side derives <c>/O</c> and <c>/OE</c>; the read side authenticates an owner password
    /// against them. Nothing joined the two, and it shows: three separate mutations of Algorithm 9 —
    /// dropping <c>U</c> as the <c>udata</c> argument to either <c>Hash2B</c> call, and swapping the
    /// validation and key salts inside <c>/O</c> — each survived the entire solution's tests. The
    /// mirror mutations on Algorithm 8 are all caught, because <c>/U</c> and <c>/UE</c> do have a
    /// round trip. This is that round trip for the owner half.
    ///
    /// <para>Algorithm 9 differs from Algorithm 8 in exactly one way — it passes the 48-byte
    /// <c>/U</c> as <c>udata</c> to both hashes (ISO 32000-2 §7.6.4.3.3) — and a writer that omits it
    /// produces a file whose owner password opens nowhere, including here. The corpus cannot catch
    /// that: its fixtures come from another producer, so they exercise the read side against someone
    /// else's <c>/O</c>, never against this library's own.</para>
    ///
    /// <para>Both halves are asserted to reach the SAME file key, which is what makes the test about
    /// Algorithm 9 rather than about the owner path merely returning something.</para>
    /// </summary>
    [Fact]
    public void R6_ownerPassword_authenticatesAgainstTheKeyTheWriterDerived()
    {
        const string userPassword = "user-side-secret";
        const string ownerPassword = "owner-side-secret";

        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = userPassword,
            OwnerPassword = ownerPassword,
            Permissions = PdfPermissions.Print | PdfPermissions.Copy,
        });

        var decryptor = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32,
            o: handler.O, u: handler.U, oe: handler.OE, ue: handler.UE,
            p: handler.PValue, id0: [], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);

        Assert.True(
            decryptor.TryComputeFileKeyFromOwnerPassword(ownerPassword, out var ownerKey),
            "the owner password did not authenticate against the /O this library wrote");
        Assert.True(
            decryptor.TryComputeFileKeyFromUserPassword(userPassword, out var userKey),
            "the user password did not authenticate against the /U this library wrote");

        // /OE and /UE wrap the same file key, so recovering it by either route must agree. A writer
        // that derived /OE from the wrong intermediate key would still "authenticate" and then unwrap
        // to a different key, which is a document that opens and decrypts to noise.
        Assert.Equal(userKey, ownerKey);

        // And the key is the one the document was actually encrypted under.
        var plaintext = "OWNER-ROUND-TRIP-CANARY"u8.ToArray();
        Assert.Equal(
            plaintext,
            decryptor.DecryptStream(ownerKey!, objectNumber: 1, generation: 0, handler.Encrypt(plaintext)));
    }

    /// <summary>
    /// The owner password must not be accepted where it is wrong, and the user password must not
    /// authenticate through the owner path. Without these the test above passes against an
    /// implementation that returns the file key for anything.
    /// </summary>
    [Fact]
    public void R6_ownerPasswordCheck_rejectsTheWrongPassword()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "user-side-secret",
            OwnerPassword = "owner-side-secret",
        });

        var decryptor = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32,
            o: handler.O, u: handler.U, oe: handler.OE, ue: handler.UE,
            p: handler.PValue, id0: [], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);

        Assert.False(decryptor.TryComputeFileKeyFromOwnerPassword("not-the-owner", out _));
        Assert.False(decryptor.TryComputeFileKeyFromOwnerPassword("user-side-secret", out _));
        Assert.False(decryptor.TryComputeFileKeyFromUserPassword("owner-side-secret", out _));
    }

    /// <summary>
    /// Known answers for ISO 32000-2 §7.6.4.3.4 Hash algorithm 2.B, computed outside this repository
    /// from the clause text. Everything else that exercises 2.B round-trips through
    /// <see cref="Hash2B_Test"/>, which is a re-implementation of the same clause and terminates on
    /// the same comparison — so a wrong termination boundary cancels on both sides and every one of
    /// those tests passes. Only a hardcoded answer can see it.
    ///
    /// <para>The first row is the one that matters. §7.6.4.3.4 ends the loop when the round number is
    /// at least 64 AND the last byte of E is at most the round number minus 32; with the loop counted
    /// from zero that is <c>round &gt;= 63 &amp;&amp; e[^1] &lt;= round - 31</c>. This password's last
    /// byte lands EXACTLY on that bound at round 66, so a boundary written one lower runs on to round
    /// 79 and returns a completely different key. Roughly one password in 256 lands there, and none
    /// of the committed fixtures does. The other two rows clear the bound comfortably and pin the
    /// rest of the algorithm — the mod-3 hash selection, the 64 repetitions, the AES key and IV
    /// split — with the <c>/O</c> path's non-empty <c>udata</c> covered by the last.</para>
    /// </summary>
    [Theory]
    [InlineData("kat16", "", "b0be27752762d37b19a8271d69f4bd373a944f6353eb4000cc8f3e62fd8df8b2")]
    [InlineData("kat0", "", "32bb20546044ef0cb9f9d8670c608b85d6d376fa1aefdf290b1b59f99180e204")]
    [InlineData("owner", "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
        + "202122232425262728292a2b2c2d2e2f", "f77bbaa82ba46eca0e3997ad8c91b9fa4a3f83750e13a950f901abef1f69b683")]
    public void Hash2B_matchesKnownAnswersDerivedFromTheClause(string password, string udataHex, string expected)
    {
        byte[] salt = [0, 1, 2, 3, 4, 5, 6, 7];

        var actual = StandardSecurityHandler.Hash2B(
            Encoding.ASCII.GetBytes(password), salt, Convert.FromHexString(udataHex));

        Assert.Equal(expected, Convert.ToHexStringLower(actual));
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
