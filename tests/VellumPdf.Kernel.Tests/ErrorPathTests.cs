// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Encryption;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Exercises the documented failure paths on <see cref="PdfDocument"/> — the guard
/// combinations, disposal, and null-argument checks (issue #5 error-path gaps).
/// </summary>
public sealed class ErrorPathTests
{
    [Fact]
    public void Save_pdfAConformanceWithEncrypt_throwsInvalidOperationException()
    {
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2b };
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "x" });
        Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
    }

    [Fact]
    public void Save_objectStreamsWithEncrypt_throwsNotSupportedException()
    {
        using var doc = new PdfDocument { UseObjectStreams = true };
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "x" });
        Assert.Throws<NotSupportedException>(() => doc.Save(new MemoryStream()));
    }

    [Fact]
    public void Save_afterDispose_throwsObjectDisposedException()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        doc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => doc.Save(new MemoryStream()));
    }

    [Fact]
    public void Save_nullDestination_throwsArgumentNullException()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        Assert.Throws<ArgumentNullException>(() => doc.Save(null!));
    }

    [Fact]
    public void Encrypt_nullSettings_throwsArgumentNullException()
    {
        using var doc = new PdfDocument();
        Assert.Throws<ArgumentNullException>(() => doc.Encrypt(null!));
    }

    [Fact]
    public void Encrypt_afterDispose_throwsObjectDisposedException()
    {
        var doc = new PdfDocument();
        doc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => doc.Encrypt(new PdfEncryptionSettings()));
    }

    [Fact]
    public void PrepareForSigning_nullOptions_throwsArgumentNullException()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        Assert.Throws<ArgumentNullException>(() => doc.PrepareForSigning(null!));
    }

    [Fact]
    public void PrepareForSigning_whenEncrypted_throwsNotSupportedException()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "x" });
        Assert.Throws<NotSupportedException>(() => doc.PrepareForSigning(new SignaturePlaceholderOptions()));
    }

    // ── Recoverable-precondition retry (issue: _written ordering fix) ─────────

    /// <summary>
    /// Proves that <see cref="PdfDocument.Save"/> is REUSABLE after a recoverable
    /// precondition failure.
    ///
    /// Pre-fix: <c>_written</c> was set BEFORE the precondition checks, so the first
    /// failed <c>Save</c> (no pages) marked the document as "already written", causing
    /// the retry to throw "already written" instead of succeeding.
    ///
    /// Post-fix: <c>_written</c> is set only AFTER all preconditions pass, so a
    /// recoverable failure leaves the document usable.
    /// </summary>
    [Fact]
    public void Save_afterRecoverableNoPagesFailure_succeedsOnRetryWithPage()
    {
        using var doc = new PdfDocument();

        // First Save: no pages — must throw InvalidOperationException.
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.Contains("no pages", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Recover: add a page.
        doc.AddPage();

        // Second Save to a fresh stream — must SUCCEED (not throw "already written").
        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        // A minimal valid PDF starts with %PDF and ends with %%EOF.
        Assert.True(bytes.Length > 0);
        Assert.Equal((byte)'%', bytes[0]);
        var tail = Encoding.Latin1.GetString(bytes[^7..]);
        Assert.Contains("%%EOF", tail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same reusability guarantee for the <c>UseObjectStreams + Encrypt</c>
    /// precondition. The first <c>Save</c> throws <see cref="NotSupportedException"/>;
    /// after removing the incompatible option the second <c>Save</c> succeeds.
    /// </summary>
    [Fact]
    public void Save_afterRecoverableObjectStreamsEncryptFailure_succeedsAfterRemovingConflict()
    {
        using var doc = new PdfDocument { UseObjectStreams = true };
        doc.AddPage();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "x" });

        // First Save: UseObjectStreams + Encrypt is forbidden — must throw.
        Assert.Throws<NotSupportedException>(() => doc.Save(new MemoryStream()));

        // Recover: remove the conflicting option.
        doc.UseObjectStreams = false;

        // Second Save — must SUCCEED.
        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        Assert.True(bytes.Length > 0);
        var tail = Encoding.Latin1.GetString(bytes[^7..]);
        Assert.Contains("%%EOF", tail, StringComparison.Ordinal);
    }
}
