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

    [Fact]
    public void Save_withPdfAConformanceAndEncrypt_throwsInvalidOperationException()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });

        var ms = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => doc.Save(ms));
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
