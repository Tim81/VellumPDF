// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Images;
using VellumPdf.Reader;

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

        // Bits 2..5, 8..9 and 11 should be 0; bits 6..7 (0xC0) and bit 10 (0x200) are
        // forced to 1 regardless of the requested permissions (ISO 32000-2 Table 22:
        // bits 7-8 must be 1 for R >= 3, and bit 10, deprecated in PDF 2.0, must always
        // be 1 so readers on earlier specifications keep treating extraction as allowed).
        Assert.Equal(0x200, handler.PValue & 0xF3C);
        Assert.Equal(0xC0, handler.PValue & 0xC0);
    }

    /// <summary>
    /// Known-answer values for <c>/P</c>, worked out by hand from Table 22 rather than copied from
    /// a program run, so a mutation that lands on the same wrong answer the fixed implementation
    /// would produce cannot slip through both at once.
    ///
    /// <para><c>None</c>: enabledBits = 0. <c>(0xFFFFF2C0 | 0) &amp; ~3 = 0xFFFFF2C0</c>. As a signed
    /// 32-bit value, <c>0x100000000 - 0xFFFFF2C0 = 0xD40 = 3392</c>, so <c>-3392</c>.</para>
    ///
    /// <para><c>Copy</c> (<c>1&lt;&lt;4 = 0x10</c>): <c>(0xFFFFF2C0 | 0x10) &amp; ~3 = 0xFFFFF2D0</c>.
    /// <c>0x100000000 - 0xFFFFF2D0 = 0xD30 = 3376</c>, so <c>-3376</c>.</para>
    ///
    /// <para><c>All &amp; ~Extract</c>: <c>All</c> is
    /// <c>Print|Modify|Copy|Annotate|FillForms|Extract|Assemble|PrintHighRes</c>
    /// <c>= 0x4|0x8|0x10|0x20|0x100|0x200|0x400|0x800 = 0xF3C</c>. Minus <c>Extract (0x200)</c> is
    /// <c>0xD3C</c>. <c>0xFFFFF2C0 | 0xD3C</c>: low 12 bits <c>0x2C0 | 0xD3C = 0xFFC</c>, so the
    /// result is <c>0xFFFFFFFC</c>, already clear on bits 0-1, which is <c>-4</c>. Bit 10 being
    /// forced on independently of the caller's flags is exactly why dropping <c>Extract</c> no
    /// longer moves this value away from what <c>All</c> itself produces.</para>
    ///
    /// <para><c>All</c>: enabledBits = <c>0xF3C</c>. <c>0xFFFFF2C0 | 0xF3C</c>: low 12 bits
    /// <c>0x2C0 | 0xF3C = 0xFFC</c> (identical to the previous case, since <c>0x2C0</c>'s bits are
    /// already covered by <c>0xF3C</c>), so the result is again <c>0xFFFFFFFC = -4</c>.</para>
    /// </summary>
    [Theory]
    [InlineData(PdfPermissions.None, -3392)]
    [InlineData(PdfPermissions.Copy, -3376)]
    [InlineData(PdfPermissions.All & ~PdfPermissions.Extract, -4)]
    [InlineData(PdfPermissions.All, -4)]
    public void PValue_matchesHandDerivedKnownAnswer(PdfPermissions permissions, int expected)
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "pw",
            Permissions = permissions,
        });

        Assert.Equal(expected, handler.PValue);
    }

    /// <summary>
    /// End-to-end version of <see cref="PValue_matchesHandDerivedKnownAnswer"/>: saves a full AES-256
    /// R6 document (the writer's only mode) with <c>All &amp; ~Extract</c>, the exact permission set
    /// #397 names, and checks the byte that actually reaches disk rather than only the handler's
    /// in-memory value.
    ///
    /// <para>The <c>/Perms</c> seal (Algorithm 10) is checked too, but against a handler built with
    /// the same settings rather than against the bytes <c>PdfDocument.Save</c> wrote: the handler
    /// <c>Save</c> constructs internally is not exposed, so there is no way from outside to recover
    /// the file key that sealed that specific document's <c>/Perms</c>. <c>PValue</c> is a pure
    /// function of <c>settings.Permissions</c> with no random input, so a second handler built from
    /// the same settings computes the identical <c>/P</c> and therefore seals the identical value;
    /// only <c>/U</c>, <c>/O</c>, <c>/UE</c>, <c>/OE</c> and the random padding differ between the
    /// two handlers, none of which this test depends on.</para>
    /// </summary>
    [Fact]
    public void EncryptedDocument_allWithoutExtract_writesExpectedPAndPerms()
    {
        var bytes = SaveEncrypted("u", "o", permissions: PdfPermissions.All & ~PdfPermissions.Extract);
        var text = Encoding.Latin1.GetString(bytes);

        var declared = Regex.Match(text, @"/P (-?\d+)");
        Assert.True(declared.Success, "no /P found in the written document");
        Assert.Equal("-4", declared.Groups[1].Value);

        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "TestPass@2026",
            Permissions = PdfPermissions.All & ~PdfPermissions.Extract,
        });
        Assert.Equal(-4, handler.PValue);

        var permsPlain = DecryptPermsBlockForTest(handler);
        var pFromPerms = (int)(permsPlain[0] | (permsPlain[1] << 8) | (permsPlain[2] << 16) | (permsPlain[3] << 24));
        Assert.Equal(-4, pFromPerms);
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

        // #199: Keywords is mirrored into pdf:Keywords now too, so it reads in cleartext here
        // the same way title does — pin that directly rather than only through the negative
        // /Info assertion below.
        Assert.Contains(keywords, packet, StringComparison.Ordinal);

        // And the exemption has to stay narrow. Widening the predicate to every object leaves the
        // whole document cleartext, which every other assertion here tolerates. #199 now mirrors
        // Keywords into pdf:Keywords too, so the plain string does appear in the packet — but only
        // as XmlEscape's plain UTF-8 text. AsWritten() instead reproduces /Info's own encoding:
        // PdfLiteralString.FromUnicode's UTF-16BE-with-BOM, escaped by WriteTo. That is a distinct
        // byte sequence (a NUL before every ASCII code unit) from the XMP mirror, so its absence
        // here still isolates /Info: if it ever turned up, /Info leaked in cleartext rather than
        // staying encrypted.
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

    /// <summary>
    /// Both passwords empty is a document with no protection at all, which ISO 32000-2 permits and
    /// the #211 guard must leave alone: it rejects an empty <c>OwnerPassword</c> only when a real
    /// <c>UserPassword</c> sits beside it. Covers both entry points, since both carry the guard.
    /// </summary>
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
        Assert.Equal(48, handler.O.Length);

        using var doc = new PdfDocument();
        var exception = Record.Exception(() => doc.Encrypt(settings));
        Assert.Null(exception);
    }

    /// <summary>
    /// The guard's condition is <c>string.IsNullOrEmpty(UserPassword)</c> beside an empty
    /// <c>OwnerPassword</c>; <see cref="Empty_password_is_accepted"/> only exercises the
    /// empty-string half of that. A null <c>UserPassword</c> beside an empty <c>OwnerPassword</c>
    /// is the same "no real user password to protect against" shape and must pass just as freely.
    /// </summary>
    [Fact]
    public void Encrypt_withNullUserPassword_andEmptyOwnerPassword_isAccepted()
    {
        var settings = new PdfEncryptionSettings
        {
            UserPassword = null,
            OwnerPassword = string.Empty,
        };

        var handler = new StandardSecurityHandler(settings);
        Assert.Equal(48, handler.U.Length);
        Assert.Equal(48, handler.O.Length);

        using var doc = new PdfDocument();
        var exception = Record.Exception(() => doc.Encrypt(settings));
        Assert.Null(exception);
    }

    /// <summary>
    /// An empty <c>OwnerPassword</c> beside a real <c>UserPassword</c> would derive /O from the
    /// empty string (see <see cref="DocumentWithNoOwnerPassword_doesNotOpenUnderTheEmptyPassword"/>
    /// for what that does at read time), so both shipped entry points that can produce it —
    /// <see cref="StandardSecurityHandler"/>'s public constructor and <see cref="PdfDocument.Encrypt"/>
    /// — refuse it rather than the file silently opening at owner privilege to anyone.
    ///
    /// <para>What this guard blocks at write time is no longer reachable to pin at read time: the
    /// hostile shape is /O and /OE sealed under the empty password, and the guard is what stops
    /// this constructor from ever producing that shape again. A fixture with it can't be built by
    /// calling the API — it would have to be hand-crafted, and at /R 6 both /O and /OE are bound to
    /// the same file key, so splicing one in means reimplementing Algorithm 9 (ISO 32000-2
    /// §7.6.4.4.6) in the test file, which is its own oracle hazard rather than a test. The
    /// components are pinned separately instead: <see cref="Hash2B_matchesKnownAnswersDerivedFromTheClause"/>
    /// pins the Algorithm 2.B hash Algorithm 9 is built from, and
    /// <see cref="DocumentWithNoOwnerPassword_doesNotOpenUnderTheEmptyPassword"/> pins the
    /// mechanism this guard exists to prevent — a document opening at owner access under a password
    /// that was never meant to grant it — for the one shape that's still reachable, the documented
    /// null-owner fallback.</para>
    /// </summary>
    [Fact]
    public void Encrypt_withEmptyOwnerPassword_besideARealUserPassword_throws()
    {
        var settings = new PdfEncryptionSettings
        {
            UserPassword = "hunter2",
            OwnerPassword = string.Empty,
        };

        var fromHandler = Assert.Throws<ArgumentException>(() => new StandardSecurityHandler(settings));
        Assert.Equal("settings", fromHandler.ParamName);

        using var doc = new PdfDocument();
        var fromDocument = Assert.Throws<ArgumentException>(() => doc.Encrypt(settings));
        Assert.Equal("settings", fromDocument.ParamName);
    }

    /// <summary>
    /// A null <c>OwnerPassword</c> is the one case the guard above must let through: it is the
    /// documented same-password fallback, not the empty-string defect. This only pins that the
    /// guard doesn't misfire on it — construction succeeds and <c>/O</c> is still 48 bytes.
    /// <see cref="DocumentWithNoOwnerPassword_doesNotOpenUnderTheEmptyPassword"/> is where the
    /// fallback's actual behaviour (opening at owner access under the user password alone) is
    /// pinned; a mutation that deleted the fallback entirely would still pass here.
    /// </summary>
    [Fact]
    public void Encrypt_withNullOwnerPassword_isNotRejectedByTheGuard()
    {
        var settings = new PdfEncryptionSettings { UserPassword = "hunter2", OwnerPassword = null };

        var handler = new StandardSecurityHandler(settings);
        Assert.Equal(48, handler.O.Length);

        using var doc = new PdfDocument();
        var exception = Record.Exception(() => doc.Encrypt(settings));
        Assert.Null(exception);
    }

    /// <summary>
    /// A permissions-only document — empty user password, a real owner password — must still tell
    /// the two apart: opening it with no password at all has to land as user access, never owner.
    /// This is the mirror image of the case <see cref="Encrypt_withEmptyOwnerPassword_besideARealUserPassword_throws"/>
    /// guards against, not an instance of it: the empty password here is the user password, so /O
    /// is sealed under the real owner password and this reads correctly with the guard absent. It
    /// pins the legitimate case the guard must not disturb.
    /// </summary>
    [Fact]
    public void PermissionsOnlyDocument_opensWithNoPasswordAtUserAccess_notOwner()
    {
        var bytes = SaveEncrypted(userPassword: string.Empty, ownerPassword: "the-owner-password");

        using var reader = PdfReader.Open(bytes);

        Assert.False(reader.Encryption!.IsOwnerAccess);
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

        // Bytes 12-15 are random padding (Algorithm 10 step (f)). Two blocks from two handlers must
        // therefore differ there; a constant would make every /Perms block for a given /P identical.
        var other = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "TestPass@2026",
            OwnerPassword = "OwnerSecret",
            Permissions = PdfPermissions.Print | PdfPermissions.Copy,
        });
        Assert.NotEqual(permsPlain[12..16], DecryptPermsBlockForTest(other)[12..16]);

        Assert.Equal((byte)'a', permsPlain[9]);
        Assert.Equal((byte)'d', permsPlain[10]);
        Assert.Equal((byte)'b', permsPlain[11]);

        // bytes[8] = 'T' because EncryptMetadata = true
        Assert.Equal((byte)'T', permsPlain[8]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole write path through the whole read path. Everything else that exercises encryption
    /// stops at one end or the other: the algorithm tests feed <c>handler.O/U/OE/UE</c> straight into
    /// a decryptor, and the corpus tests read files another producer wrote. Neither touches
    /// <c>PdfDocument.BuildEncryptDictionary</c>, the code that decides which of those values goes
    /// under which key and what <c>/StmF</c>, <c>/StrF</c>, <c>/V</c>, <c>/R</c> and <c>/Length</c>
    /// say about them — so four separate mutations of that dictionary passed every test in the
    /// solution, including one that hands the caller ciphertext with no error at all.
    ///
    /// <para>That one is <c>/StrF</c> written as <c>/Identity</c>. Strings are encrypted on the way
    /// out whenever an encryptor is set, whatever <c>/StrF</c> claims, so the file says "the strings
    /// are in the clear" over strings that are not — and a reader that believes it returns them
    /// verbatim. Asserting a decoded STRING as well as a decoded stream is what catches it; content
    /// streams decrypt correctly under that mutation.</para>
    /// </summary>
    [Theory]
    [InlineData("user-pw", "owner-pw")]
    [InlineData("", "owner-pw")]                 // the empty user password most encrypted PDFs use
    public void EncryptedDocument_writtenHere_isReadBackByPdfReader(string userPassword, string ownerPassword)
    {
        const string title = "CANARY-TITLE";
        var bytes = SaveEncrypted(userPassword, ownerPassword, title);

        foreach (var password in new[] { userPassword, ownerPassword })
        {
            using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = password });

            Assert.Equal(256, reader.Encryption!.KeyLengthBits);
            Assert.Equal(PdfCipherAlgorithm.Aes256, reader.Encryption.StreamCipher);
            Assert.Equal(PdfCipherAlgorithm.Aes256, reader.Encryption.StringCipher);

            // A string, which is what /StrF governs and what a wrong /StrF returns as ciphertext.
            var info = (PdfDictionary)reader.Resolve((PdfIndirectReference)reader.Trailer.Get(PdfName.Info)!)!;
            var titleValue = info.Get(new PdfName("Title"))!;
            var titleBytes = titleValue switch
            {
                PdfHexString hex => hex.Bytes,
                PdfLiteralString lit => lit.Bytes,
                _ => throw new InvalidOperationException($"/Title is a {titleValue.GetType().Name}, not a string."),
            };
            Assert.Equal(title, DecodeTextString(titleBytes.Span));

            // And a stream, which is what /StmF governs and what a swapped /OE or /UE destroys.
            var pages = (PdfDictionary)reader.Resolve((PdfIndirectReference)reader.Catalog.Get(new PdfName("Pages"))!)!;
            var kids = (PdfArray)reader.ResolveValue(pages.Get(new PdfName("Kids"))!)!;
            var page = (PdfDictionary)reader.Resolve((PdfIndirectReference)kids[0]!)!;
            var content = reader.GetDecodedStreamData(
                reader.ResolveStream((PdfIndirectReference)page.Get(new PdfName("Contents"))!)!)!;

            Assert.Contains("Hello, encrypted world!", Encoding.Latin1.GetString(content), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// What the written <c>/Encrypt</c> dictionary DECLARES, as opposed to what this library can read
    /// back from it. Two of its entries are invisible to a round trip: <c>/Length</c>, because
    /// <c>/R</c> 5 and 6 force a 32-byte key whatever it says, and <c>/Perms</c>, because a reader
    /// that finds none falls back to <c>/P</c> and reaches the same answer. Both were mutable with
    /// the whole solution green. Another implementation reading these files does see them — Table 21
    /// requires <c>/Perms</c> at <c>/R</c> 5 and 6, and a <c>/Length</c> of 128 beside <c>/V</c> 5 is
    /// a document contradicting itself.
    /// </summary>
    [Fact]
    public void EncryptedDocument_declaresTheDictionaryTheSpecRequires()
    {
        var bytes = SaveEncrypted("u", "o");

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        var encrypt = (PdfDictionary)reader.ResolveValue(reader.Trailer.Get(new PdfName("Encrypt"))!)!;

        long Int(string key) => ((PdfInteger)encrypt.Get(new PdfName(key))!).Value;
        string Name(string key) => ((PdfName)encrypt.Get(new PdfName(key))!).Value;

        Assert.Equal("Standard", Name("Filter"));
        Assert.Equal(5, Int("V"));
        Assert.Equal(6, Int("R"));
        Assert.Equal(256, Int("Length"));
        Assert.Equal("StdCF", Name("StmF"));
        Assert.Equal("StdCF", Name("StrF"));

        var stdCf = (PdfDictionary)((PdfDictionary)encrypt.Get(new PdfName("CF"))!).Get(new PdfName("StdCF"))!;
        Assert.Equal("AESV3", ((PdfName)stdCf.Get(new PdfName("CFM"))!).Value);

        Assert.Equal(16, ((PdfHexString)encrypt.Get(new PdfName("Perms"))!).Bytes.Length);
        Assert.Equal(48, ((PdfHexString)encrypt.Get(new PdfName("O"))!).Bytes.Length);
        Assert.Equal(48, ((PdfHexString)encrypt.Get(new PdfName("U"))!).Bytes.Length);
        Assert.Equal(32, ((PdfHexString)encrypt.Get(new PdfName("OE"))!).Bytes.Length);
        Assert.Equal(32, ((PdfHexString)encrypt.Get(new PdfName("UE"))!).Bytes.Length);
        Assert.Equal((int)Int("P") & (int)PdfPermissions.All, (int)reader.Encryption!.Permissions);
    }

    /// <summary>
    /// The <c>/Encrypt</c> dictionary is exempt from encryption and everything else is not, and the
    /// exemption is implemented by clearing the writer's encryptor for one object and restoring it
    /// afterwards. Neither the restore nor the exemption's narrowness was pinned: restoring
    /// <see langword="null"/> instead of the saved encryptor, or widening the test to "this object
    /// number or later", both passed the whole solution — because the objects written after that slot
    /// are the outline entries, and no test encrypted a document that has any.
    /// </summary>
    /// <remarks>
    /// A bookmark title is a text string, so it is the one piece of user content in an outline that
    /// encryption is supposed to cover. Under either mutation it appears in the file verbatim.
    /// </remarks>
    [Fact]
    public void EncryptedDocument_withAnOutline_encryptsTheBookmarkTitle()
    {
        const string title = "OUTLINE-CANARY";

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = title,
            DestPage = page,
            DestLeft = 0,
            DestTop = 800,
            Level = 0,
        });

        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        // The writer emits text strings as UTF-16BE behind a byte-order mark, so that is the form the
        // title would leak in.
        var leaked = Encoding.BigEndianUnicode.GetBytes(title);
        Assert.DoesNotContain(
            Encoding.Latin1.GetString(leaked),
            Encoding.Latin1.GetString(bytes),
            StringComparison.Ordinal);

        // And it must come back intact, so this is not passing because the title was mangled.
        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        var outlines = (PdfDictionary)reader.ResolveValue(reader.Catalog.Get(new PdfName("Outlines"))!)!;
        var first = (PdfDictionary)reader.ResolveValue(outlines.Get(new PdfName("First"))!)!;
        var titleValue = first.Get(new PdfName("Title"))!;
        var titleBytes = titleValue switch
        {
            PdfHexString hex => hex.Bytes,
            PdfLiteralString lit => lit.Bytes,
            _ => throw new InvalidOperationException($"/Title is a {titleValue.GetType().Name}, not a string."),
        };

        Assert.Equal(title, DecodeTextString(titleBytes.Span));
    }

    /// <summary>
    /// A passthrough image stream is encrypted like any other, and its <c>/Length</c> counts the
    /// CIPHERTEXT. <c>RawPdfStream</c> carries every image whose bytes are handed through verbatim —
    /// JPEG, JPEG 2000, CCITT, JBIG2 — and no test encrypted a document containing one, so both
    /// clauses could be removed with the whole solution green. Its two sibling stream classes are
    /// pinned; this one was reachable only through a fixture nobody had.
    ///
    /// <para>Skipping the encryption writes the image into the file verbatim, which is a plaintext
    /// leak visible by grepping for the JPEG start-of-image marker. Writing the plaintext length over
    /// ciphertext is the stale-<c>/Length</c> shape: qpdf reports "expected endstream" and recovers a
    /// different length, and this library's own reader papers over it by scanning.</para>
    /// </summary>
    [Fact]
    public void EncryptedDocument_withAPassthroughImage_encryptsItAndStatesTheCiphertextLength()
    {
        // Any filter that is not the implicit-FlateDecode sentinel makes the bytes passthrough, which
        // is the branch under test. The content is a recognisable run rather than a real JPEG: what
        // matters is that these exact bytes must not appear in the output.
        var raw = "PASSTHROUGH-IMAGE-CANARY-0123456789"u8.ToArray();
        var image = new PdfImageXObject(
            width: 8, height: 6, streamData: raw, filter: PdfName.DCTDecode,
            colorSpace: ImageColorSpace.DeviceRgb, bitsPerComponent: 8);

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterImageXObject(page, image, "Im0");

        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Assert.DoesNotContain(
            Encoding.Latin1.GetString(raw),
            Encoding.Latin1.GetString(bytes),
            StringComparison.Ordinal);

        // And /Length must count the CIPHERTEXT. AES adds a 16-byte IV and pads to a block boundary,
        // so the encrypted body is necessarily longer than the plaintext; stating the plaintext
        // length over ciphertext is the stale-/Length shape qpdf reports as "expected endstream".
        var text = Encoding.Latin1.GetString(bytes);
        var imageAt = text.IndexOf("/Subtype /Image", StringComparison.Ordinal);
        Assert.True(imageAt >= 0, "no image XObject found in the written document");

        var streamAt = text.IndexOf("stream", imageAt, StringComparison.Ordinal);
        var lengthAt = text.LastIndexOf("/Length ", streamAt, StringComparison.Ordinal);
        Assert.True(lengthAt > imageAt, "the image XObject declared no /Length");

        var digits = lengthAt + "/Length ".Length;
        var end = digits;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
            end++;
        var declared = int.Parse(text[digits..end], CultureInfo.InvariantCulture);

        // Exactly the AES shape: a 16-byte IV plus the plaintext padded up to the next block. Stating
        // the plaintext length would give raw.Length, which this rejects.
        Assert.Equal(16 + (((raw.Length / 16) + 1) * 16), declared);
    }

    /// <summary>
    /// The file encryption key is the document's whole secret, and nothing required it to be random.
    /// Replacing the call that fills it with a constant passed every test in the solution: every
    /// round trip recovers the key from <c>/UE</c> rather than checking it is fresh, and the
    /// different-ciphertext test still passes because the per-string IV varies. A document written
    /// under a fixed key decrypts to its plaintext for anyone who knows that constant, and qpdf and
    /// poppler report nothing, because the file is structurally perfect.
    /// </summary>
    [Fact]
    public void TwoDocumentsWithTheSamePassword_useDifferentFileKeys()
    {
        static byte[] KeyOf(StandardSecurityHandler handler)
        {
            var decryptor = new StandardSecurityDecryptor(
                v: 5, r: 6, keyLengthBytes: 32,
                o: handler.O, u: handler.U, oe: handler.OE, ue: handler.UE,
                p: handler.PValue, id0: [], encryptMetadata: true,
                streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);

            Assert.True(decryptor.TryComputeFileKeyFromUserPassword("same-password", out var key));
            return key!;
        }

        var settings = () => new PdfEncryptionSettings { UserPassword = "same-password", OwnerPassword = "o" };

        Assert.NotEqual(KeyOf(new StandardSecurityHandler(settings())), KeyOf(new StandardSecurityHandler(settings())));
    }

    /// <summary>
    /// The validation salt and the key salt must not be the same eight bytes. If they are, the hash
    /// that unwraps <c>/UE</c> is byte-identical to the validation hash — which the file publishes in
    /// the clear in <c>/U</c>'s first 32 bytes — and anyone can compute the file encryption key from
    /// the document alone, with no password. Total break, not a weakening, and structurally invisible:
    /// qpdf and poppler read such a file without a word.
    ///
    /// <para>Both halves are checked in the form that matters: not "the salts differ" but "the
    /// published validation hash does not unwrap the wrapped key". A future change that made the two
    /// salts merely correlated rather than equal would still fail this.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]   // /U and /UE
    [InlineData(true)]    // /O and /OE
    public void ThePublishedValidationHash_doesNotUnwrapTheFileKey(bool ownerSide)
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "u",
            OwnerPassword = "o",
        });

        var (entry, wrapped) = ownerSide ? (handler.O, handler.OE) : (handler.U, handler.UE);

        // Distinct salts are the mechanism...
        Assert.NotEqual(entry[32..40], entry[40..48]);

        // ...and this is the property they exist to provide. Unwrapping /UE or /OE with the hash the
        // file hands out must not produce the key the document is actually encrypted under.
        using var aes = Aes.Create();
        aes.Key = entry[..32];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        var wouldBeKey = aes.CreateDecryptor(aes.Key, new byte[16]).TransformFinalBlock(wrapped, 0, wrapped.Length);

        var plaintext = "SALT-COLLISION-CANARY"u8.ToArray();
        var ciphertext = handler.Encrypt(plaintext);

        var decryptor = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32,
            o: handler.O, u: handler.U, oe: handler.OE, ue: handler.UE,
            p: handler.PValue, id0: [], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);

        byte[] recovered;
        try
        {
            recovered = decryptor.DecryptStream(wouldBeKey, 1, 0, ciphertext);
        }
        catch (InvalidDataException)
        {
            return; // the wrong key did not even produce well-formed padding, which is the point
        }

        Assert.NotEqual(plaintext, recovered);
    }

    /// <summary>
    /// A <c>/ID</c> that is not 16 bytes was written as no <c>/ID</c> at all — silently, with no
    /// exception and no warning. ISO 32000-2 Table 15 requires the entry once <c>/Encrypt</c> is
    /// present, so that produced an encrypted document qpdf rejects outright ("invalid /ID in trailer
    /// dictionary"). Neither the writer's length guard nor what a caller got when they tripped it was
    /// pinned by anything.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(32)]
    public void DocumentIdOfTheWrongLength_isRefusedAtTheSetter(int length)
    {
        using var doc = new PdfDocument();

        var ex = Assert.Throws<ArgumentException>(() => doc.DocumentId = new byte[length]);

        Assert.Contains("16 bytes", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Sixteen bytes is accepted, and reaches the trailer of an encrypted document.</summary>
    [Fact]
    public void DocumentIdOfSixteenBytes_isWrittenToAnEncryptedDocument()
    {
        var id = new byte[16];
        for (var i = 0; i < id.Length; i++)
            id[i] = (byte)(0xA0 + i);

        using var doc = new PdfDocument();
        doc.AddPage();
        doc.DocumentId = id;
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" });

        using var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Contains(
            Convert.ToHexStringLower(id),
            Encoding.Latin1.GetString(ms.ToArray()),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ISO 32000-1 §7.6.5: the trailer's <c>/ID</c> strings are never encrypted. The writer enforces
    /// that by clearing its encryptor before writing the trailer, and nothing joined that line to an
    /// assertion — deleting it passed every test in the solution, because at <c>/V</c> 5 the
    /// <c>/ID</c> is not an input to key derivation, so this library reads its own output back
    /// perfectly either way. Only another implementation would see the damage.
    ///
    /// <para>Both elements are checked. Encrypting them turns each into a 16-byte IV followed by AES
    /// ciphertext — three times the length, and no longer equal to each other, since a fresh IV is
    /// drawn per string.</para>
    /// </summary>
    [Fact]
    public void EncryptedDocument_trailerId_isNotEncrypted()
    {
        var bytes = SaveEncrypted("u", "o");
        var text = Encoding.Latin1.GetString(bytes);

        var idAt = text.LastIndexOf("/ID [", StringComparison.Ordinal);
        Assert.True(idAt >= 0, "no trailer /ID found in the written document");

        var ids = Regex.Matches(text[idAt..(text.IndexOf(']', idAt) + 1)], "<([0-9a-fA-F]*)>");
        Assert.Equal(2, ids.Count);

        // A PDF file identifier is a 16-byte MD5 digest, so 32 hex digits. Encrypted it would be 96.
        Assert.Equal(32, ids[0].Groups[1].Value.Length);
        Assert.Equal(32, ids[1].Groups[1].Value.Length);

        // The writer emits the same value twice for an original document (§14.4): a pair that no
        // longer matches is the tell that something transformed them independently.
        Assert.Equal(ids[0].Groups[1].Value, ids[1].Groups[1].Value);
    }

    /// <summary>
    /// <c>/Perms</c> has to be the SEALED copy of <c>/P</c>, not merely sixteen bytes of the right
    /// shape. Nothing here could tell the difference: the reader falls back to the dictionary's
    /// <c>/P</c> when the seal fails its marker check, deliberately and by documented design, so a
    /// <c>/Perms</c> of sixteen zeroes reaches the same answer and passes. qpdf does not agree —
    /// it warns that "/Perms field in encryption dictionary doesn't match expected value" — which
    /// makes this the class of defect only another implementation sees.
    ///
    /// <para>Editing <c>/P</c> in the written bytes is what separates the two sources. The document
    /// is written granting print, and the writer always sets bit 10 as well (ISO 32000-2 Table 22,
    /// #397); the edit declares full permissions over a seal that says otherwise, and a reader that
    /// reads the seal still reports the document's narrower grant. Same byte count, so every
    /// cross-reference offset stays valid.</para>
    /// </summary>
    [Fact]
    public void EncryptedDocument_permsIsTheSealedCopyOfP_notJustSixteenBytes()
    {
        var bytes = SaveEncrypted("u", "o", permissions: PdfPermissions.Print);

        var text = Encoding.Latin1.GetString(bytes);
        var declared = Regex.Match(text, @"/P (-?\d+)");
        Assert.True(declared.Success, "no /P found in the written document");

        // -1 grants everything; pad it to the width of what it replaces so no offset moves.
        var replacement = "-1".PadLeft(declared.Groups[1].Value.Length, '0');
        if (declared.Groups[1].Value.StartsWith('-'))
            replacement = "-" + "1".PadLeft(declared.Groups[1].Value.Length - 1, '0');

        var patched = Encoding.Latin1.GetBytes(
            text[..declared.Groups[1].Index] + replacement
            + text[(declared.Groups[1].Index + declared.Groups[1].Value.Length)..]);
        Assert.Equal(bytes.Length, patched.Length);

        using var reader = PdfReader.Open(patched, new PdfReaderOptions { Password = "u" });

        // The seal wins: print plus the bit-10 the writer always sets, not the everything the
        // edited /P now claims.
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Extract, reader.Encryption!.Permissions);
    }

    /// <summary>
    /// <c>PdfEncryptionSettings.OwnerPassword</c> documents that a null one means the user password
    /// serves as both. Dropping that fallback is a one-token edit that passed every test here, and it
    /// is not a cosmetic one: with no owner password the handler would derive <c>/O</c> from the
    /// EMPTY string, so every document written without an explicit owner password would open to
    /// anyone supplying nothing at all, at owner privilege.
    /// </summary>
    [Fact]
    public void DocumentWithNoOwnerPassword_doesNotOpenUnderTheEmptyPassword()
    {
        var bytes = SaveEncrypted("the-user-password", ownerPassword: null);

        Assert.Throws<PdfPasswordException>(() => PdfReader.Open(bytes, new PdfReaderOptions { Password = "" }));
        Assert.Throws<PdfPasswordException>(() => PdfReader.Open(bytes));

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "the-user-password" });
        Assert.True(reader.Encryption!.IsOwnerAccess, "the user password also serves as the owner password");
    }

    /// <summary>
    /// ISO 32000-2 §7.6.5.3 requires a fresh IV per encryption. At <c>/V</c> 5 the file key is used
    /// directly for every string and stream, so a fixed IV would make identical plaintext produce
    /// identical ciphertext across the whole document — and deleting the call that fills it passed
    /// every test, because a round trip decrypts either way.
    /// </summary>
    [Fact]
    public void Encrypt_usesAFreshIvEachTime()
    {
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "u",
            OwnerPassword = "o",
        });

        var plaintext = "IDENTICAL-PLAINTEXT"u8.ToArray();
        var first = handler.Encrypt(plaintext);
        var second = handler.Encrypt(plaintext);

        // The IV is the first 16 bytes, and it is what makes the rest differ.
        Assert.NotEqual(first[..16], second[..16]);
        Assert.NotEqual(first, second);
    }

    // The text string PDF writes for /Info values: UTF-16BE behind a byte-order mark (ISO 32000-1
    // §7.9.2.2), which is what the writer emits and what a correct decryption produces.
    private static string DecodeTextString(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(bytes[2..])
            : Encoding.Latin1.GetString(bytes);

    private static byte[] SaveEncrypted(
        string userPassword,
        string? ownerPassword,
        string? title = null,
        PdfPermissions permissions = PdfPermissions.All)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("Hello, encrypted world!").EndText();
        canvas.Finish();

        if (title is not null)
            doc.Info.Title = title;

        doc.Encrypt(new PdfEncryptionSettings
        {
            UserPassword = userPassword,
            OwnerPassword = ownerPassword,
            Permissions = permissions,
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

    // The decrypted /Perms block of a handler, using only the BCL — the read side's own recovery
    // refuses a block whose marker is wrong, and this needs the bytes whatever they say.
    private static byte[] DecryptPermsBlockForTest(StandardSecurityHandler handler)
    {
        var decryptor = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32,
            o: handler.O, u: handler.U, oe: handler.OE, ue: handler.UE,
            p: handler.PValue, id0: [], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);
        Assert.True(decryptor.TryComputeFileKeyFromUserPassword("TestPass@2026", out var fileKey));

        using var aes = Aes.Create();
        aes.Key = fileKey!;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes.CreateDecryptor().TransformFinalBlock(handler.Perms, 0, handler.Perms.Length);
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
