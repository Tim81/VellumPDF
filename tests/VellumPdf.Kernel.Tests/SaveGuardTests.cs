// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #530: the checks a <see cref="PdfDocument"/> save makes before it touches the document or the
/// destination, and the order they are made in. "No pages" has always been reported before the
/// destination and the options.
/// </summary>
public sealed class SaveGuardTests
{
    private static PdfDocument OnePage()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        return doc;
    }

    private static MemoryStream ReadOnlyStream() => new(new byte[8], writable: false);

    // ── "No pages" first ─────────────────────────────────────────────────────

    [Fact]
    public void Save_aDocumentWithNoPages_reportsNoPagesBeforeAnUnwritableStream()
    {
        using var doc = new PdfDocument();
        using var readOnly = ReadOnlyStream();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(readOnly));

        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_aDocumentWithNoPages_reportsNoPagesBeforeAnUnwritableStream()
    {
        using var doc = new PdfDocument();
        using var readOnly = ReadOnlyStream();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveAsync(readOnly, TestContext.Current.CancellationToken));

        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_aDocumentWithNoPages_reportsNoPagesBeforeIncompatibleOptions()
    {
        using var doc = new PdfDocument { UseObjectStreams = true };
        doc.Encrypt(new VellumPdf.Encryption.PdfEncryptionSettings { UserPassword = "pw" });
        using var ms = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
    }

    // ── A destination that cannot be written to is refused before anything is marked ──

    [Fact]
    public void Save_toAReadOnlyStream_throwsArgumentExceptionNamingDestination()
    {
        using var doc = OnePage();
        using var readOnly = ReadOnlyStream();

        var ex = Assert.Throws<ArgumentException>(() => doc.Save(readOnly));

        Assert.Equal("destination", ex.ParamName);
    }

    [Fact]
    public async Task SaveAsync_toANonWritableStream_throwsArgumentExceptionNamingDestination_andTheDocumentIsStillSaveable()
    {
        using var doc = OnePage();
        using var readOnly = ReadOnlyStream();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => doc.SaveAsync(readOnly, TestContext.Current.CancellationToken));
        Assert.Equal("destination", ex.ParamName);

        // Refused up front, so the document was not marked as written: a good stream still works.
        using var ok = new MemoryStream();
        await doc.SaveAsync(ok, TestContext.Current.CancellationToken);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(ok.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_toAClosedStream_throwsArgumentException_andTheDocumentIsStillSaveable()
    {
        using var doc = OnePage();
        var closed = new MemoryStream();
        closed.Dispose();

        await Assert.ThrowsAsync<ArgumentException>(() => doc.SaveAsync(closed, TestContext.Current.CancellationToken));

        using var ok = new MemoryStream();
        doc.Save(ok);
        Assert.True(ok.Length > 0);
    }

    [Fact]
    public void Save_toANonWritableStream_leavesTheDocumentSaveable()
    {
        using var doc = OnePage();
        using var readOnly = ReadOnlyStream();
        Assert.Throws<ArgumentException>(() => doc.Save(readOnly));

        using var ok = new MemoryStream();
        doc.Save(ok);

        Assert.True(ok.Length > 0);
    }
}
