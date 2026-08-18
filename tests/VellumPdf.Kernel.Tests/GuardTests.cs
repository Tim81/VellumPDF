// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Forms;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Lock-in tests for the defensive guards on <see cref="PdfDocument"/>.
/// Covers: Dispose/ObjectDisposedException guards on Save and Encrypt;
/// the PDF/A + Encrypt mutual-exclusion guard on Save; and
/// ArgumentNullException / ArgumentException null-argument guards.
/// </summary>
public sealed class GuardTests
{
    // ── ObjectDisposedException guards ──────────────────────────────────────

    [Fact]
    public void Save_afterDispose_throwsObjectDisposedException()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        doc.Dispose();

        var ms = new MemoryStream();
        Assert.Throws<ObjectDisposedException>(() => doc.Save(ms));
    }

    [Fact]
    public void Encrypt_afterDispose_throwsObjectDisposedException()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        doc.Dispose();

        var settings = new PdfEncryptionSettings { UserPassword = "pw" };
        Assert.Throws<ObjectDisposedException>(() => doc.Encrypt(settings));
    }

    // ── PDF/A + Encrypt mutual-exclusion guard ───────────────────────────────
    // (UseObjectStreams + Encrypt is already covered by ObjectStreamTests)

    [Theory]
    [InlineData(PdfConformance.PdfA2b)]
    [InlineData(PdfConformance.PdfA2u)]
    [InlineData(PdfConformance.PdfA2a)]
    public void Save_withPdfAConformanceAndEncrypt_throwsInvalidOperationException(PdfConformance conformance)
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Conformance = conformance;
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });

        var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));
        Assert.Contains("PDF/A", ex.Message, StringComparison.Ordinal);
    }

    // ── PDF/UA-1 + Encrypt: distinct from the PDF/A rule (#188) ──────────────
    // PDF/UA-1 (ISO 14289-1) does not prohibit encryption; it requires that content
    // remain extractable for assistive technology, so the guard is permission-shaped
    // rather than a flat "no encryption" rule.

    [Fact]
    public void Save_withPdfUA1ConformanceAndEncrypt_savesSuccessfully()
    {
        // PdfEncryptionSettings.Permissions defaults to All, which includes Extract,
        // so the default case must not be rejected.
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfUA1, Tagged = true, Language = "en-US" };
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });

        var ms = new MemoryStream();
        var ex = Record.Exception(() => doc.Save(ms));
        Assert.Null(ex);
    }

    [Fact]
    public void Save_withPdfUA1ConformanceAndEncryptMissingExtractPermission_throwsInvalidOperationException()
    {
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfUA1, Tagged = true, Language = "en-US" };
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings
        {
            UserPassword = "pw",
            Permissions = PdfPermissions.All & ~PdfPermissions.Extract,
        });

        var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));
        Assert.Contains("PDF/UA-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Extract", ex.Message, StringComparison.Ordinal);
    }

    // ── ArgumentNullException guards on Save and Encrypt ─────────────────────

    [Fact]
    public void Save_nullStream_throwsArgumentNullException()
    {
        using var doc = new PdfDocument();
        doc.AddPage();

        Assert.Throws<ArgumentNullException>(() => doc.Save(null!));
    }

    [Fact]
    public void Encrypt_nullSettings_throwsArgumentNullException()
    {
        using var doc = new PdfDocument();
        doc.AddPage();

        Assert.Throws<ArgumentNullException>(() => doc.Encrypt(null!));
    }

    // ── AcroForm null-argument guards ────────────────────────────────────────

    [Fact]
    public void AddTextField_nullPage_throwsArgumentNullException()
    {
        using var doc = new PdfDocument();
        var rect = new PdfRectangle(0, 0, 100, 20);

        Assert.Throws<ArgumentNullException>(() => doc.AddTextField(null!, "field", rect));
    }

    [Fact]
    public void AddTextField_nullName_throwsArgumentNullException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var rect = new PdfRectangle(0, 0, 100, 20);

        Assert.Throws<ArgumentNullException>(() => doc.AddTextField(page, null!, rect));
    }

    [Fact]
    public void AddTextField_emptyName_throwsArgumentException()
    {
        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentException for empty string.
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var rect = new PdfRectangle(0, 0, 100, 20);

        Assert.Throws<ArgumentException>(() => doc.AddTextField(page, "", rect));
    }

    [Fact]
    public void AddPushButton_nullPage_throwsArgumentNullException()
    {
        using var doc = new PdfDocument();
        var rect = new PdfRectangle(0, 0, 100, 20);

        Assert.Throws<ArgumentNullException>(() => doc.AddPushButton(null!, "btn", rect, "Click"));
    }

    [Fact]
    public void AddPushButton_nullName_throwsArgumentNullException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var rect = new PdfRectangle(0, 0, 100, 20);

        Assert.Throws<ArgumentNullException>(() => doc.AddPushButton(page, null!, rect, "Click"));
    }

    [Fact]
    public void AddPushButton_emptyName_throwsArgumentException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var rect = new PdfRectangle(0, 0, 100, 20);

        Assert.Throws<ArgumentException>(() => doc.AddPushButton(page, "", rect, "Click"));
    }
}
