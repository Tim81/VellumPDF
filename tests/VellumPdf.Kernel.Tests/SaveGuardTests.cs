// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;

namespace VellumPdf.Kernel.Tests;

/// <summary>The checks a <see cref="PdfDocument"/> save makes before it touches the document or the destination, and the order they are made in: "no pages" is reported before the destination and the options.</summary>
public sealed class SaveGuardTests
{
    private static PdfDocument OnePage()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        return doc;
    }

    private static MemoryStream ReadOnlyStream() => new(new byte[8], writable: false);

    /// <summary>
    /// What the save after a refused one wrote must be a PDF, and the same one a document that was never refused
    /// writes: a length above zero holds for any output at all, including pages the refused save left behind.
    /// </summary>
    private static void AssertIsTheFileAFreshOnePageDocumentWrites(MemoryStream written)
    {
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(written.ToArray()), StringComparison.Ordinal);

        using var fresh = OnePage();
        using var expected = new MemoryStream();
        fresh.Save(expected);
        Assert.Equal(expected.Length, written.Length);
    }

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
    public async Task SaveAsync_toANonWritableStream_throwsNotSupportedException_andTheDocumentIsStillSaveable()
    {
        using var doc = OnePage();
        using var readOnly = ReadOnlyStream();

        await Assert.ThrowsAsync<NotSupportedException>(() => doc.SaveAsync(readOnly, TestContext.Current.CancellationToken));

        // Refused up front, so the document was not marked as written: a good stream still works.
        using var ok = new MemoryStream();
        await doc.SaveAsync(ok, TestContext.Current.CancellationToken);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(ok.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_toAClosedStream_throwsObjectDisposedException_andTheDocumentIsStillSaveable()
    {
        using var doc = OnePage();
        var closed = new MemoryStream();
        closed.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => doc.SaveAsync(closed, TestContext.Current.CancellationToken));

        using var ok = new MemoryStream();
        doc.Save(ok);
        AssertIsTheFileAFreshOnePageDocumentWrites(ok);
    }

    /// <summary>A stream that is not writable and not readable but seekable, as the 2.3.3 check classified it.</summary>
    private sealed class SeekOnlyStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task SaveAsync_toAnUnwritableUnreadableSeekableStream_throwsObjectDisposedException()
    {
        using var doc = OnePage();
        using var stream = new SeekOnlyStream();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => doc.SaveAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Save_toANonWritableStream_leavesTheDocumentSaveable()
    {
        using var doc = OnePage();
        using var readOnly = ReadOnlyStream();
        Assert.Throws<ArgumentException>(() => doc.Save(readOnly));

        using var ok = new MemoryStream();
        doc.Save(ok);

        AssertIsTheFileAFreshOnePageDocumentWrites(ok);
    }

    /// <summary>A null destination is checked first and then the token is given to Task.Run, so a token that is already cancelled wins over everything else except the null, as in 2.3.3.</summary>
    [Fact]
    public async Task PdfDocumentSaveAsync_withAnAlreadyCancelledToken_winsOverEveryDefectButTheNull()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var doc = new PdfDocument();   // no pages
        using var readOnly = new MemoryStream([], writable: false);

        await Assert.ThrowsAsync<TaskCanceledException>(() => doc.SaveAsync(readOnly, cts.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => doc.SaveAsync(null!, cts.Token));
    }
}
