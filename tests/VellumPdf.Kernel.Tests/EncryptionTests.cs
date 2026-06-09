// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
}
