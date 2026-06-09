// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
}
