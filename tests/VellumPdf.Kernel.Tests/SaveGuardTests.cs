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

    /// <summary>A stream that reports <c>CanWrite</c> false, with the other capabilities as given, and the <c>Write</c> given.</summary>
    private sealed class UnwritableStream(bool canRead, bool canSeek, Action<byte[], int, int> write) : Stream
    {
        public readonly MemoryStream Received = new();
        public override bool CanRead => canRead;
        public override bool CanSeek => canSeek;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count)
        {
            write(buffer, offset, count);
            Received.Write(buffer, offset, count);
        }
    }

    private static PdfDocument LinearizedOnePage()
    {
        var doc = OnePage();
        doc.Linearize = true;
        return doc;
    }

    private static void AssertWrittenAndRefusedAgain(PdfDocument doc)
    {
        var again = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.Contains("already been written", again.Message, StringComparison.Ordinal);
    }

    /// <summary>A linearized save serialises in memory and gives the destination one real Write, so the stream's own exception propagates, after serialisation: the document counts as written.</summary>
    [Fact]
    public void Save_linearized_toAClosedMemoryStream_throwsObjectDisposedException_andTheDocumentCountsAsWritten()
    {
        using var doc = LinearizedOnePage();
        var closed = new MemoryStream();
        closed.Dispose();

        Assert.Throws<ObjectDisposedException>(() => doc.Save(closed));

        AssertWrittenAndRefusedAgain(doc);
    }

    [Fact]
    public void Save_linearized_toAReadOnlyMemoryStream_throwsNotSupportedException_andTheDocumentCountsAsWritten()
    {
        using var doc = LinearizedOnePage();
        using var readOnly = ReadOnlyStream();

        Assert.Throws<NotSupportedException>(() => doc.Save(readOnly));

        AssertWrittenAndRefusedAgain(doc);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Save_linearized_toAnUnwritableStreamWhoseWriteThrows_throwsThatException(bool canRead, bool canSeek)
    {
        using var doc = LinearizedOnePage();
        using var refusing = new UnwritableStream(canRead, canSeek, (_, _, _) => throw new IOException("custom write refused"));

        var ex = Assert.Throws<IOException>(() => doc.Save(refusing));

        Assert.Equal("custom write refused", ex.Message);
        AssertWrittenAndRefusedAgain(doc);
    }

    /// <summary>A write of no bytes is not a probe: a stream that returns early from one and refuses a real write gives the real write's exception.</summary>
    [Fact]
    public void Save_linearized_toAStreamThatAcceptsAnEmptyWriteOnly_throwsTheRealWritesException()
    {
        using var doc = LinearizedOnePage();
        using var stream = new UnwritableStream(true, false, (_, _, count) =>
        {
            if (count != 0) throw new NotSupportedException("real write refused");
        });

        Assert.Throws<NotSupportedException>(() => doc.Save(stream));
    }

    /// <summary>A stream that reports <c>CanWrite</c> false but stores what it is given receives the whole PDF.</summary>
    [Fact]
    public void Save_linearized_toAStreamThatReportsUnwritableButStoresTheBytes_writesThePdf()
    {
        using var doc = LinearizedOnePage();
        using var storing = new UnwritableStream(true, false, (_, _, _) => { });
        using var expectedDoc = LinearizedOnePage();
        using var expected = new MemoryStream();
        expectedDoc.Save(expected);

        doc.Save(storing);

        var pdf = Encoding.Latin1.GetString(storing.Received.ToArray());
        Assert.StartsWith("%PDF-", pdf, StringComparison.Ordinal);
        Assert.Contains("%%EOF", pdf, StringComparison.Ordinal);
        Assert.Equal(expected.Length, storing.Received.Length);
    }

    /// <summary>Without <see cref="PdfDocument.Linearize"/> the destination is checked up front, so the document is left unwritten.</summary>
    [Fact]
    public void Save_notLinearized_toAnUnwritableStreamWhoseWriteThrows_throwsArgumentException_andTheDocumentIsStillSaveable()
    {
        using var doc = OnePage();
        using var refusing = new UnwritableStream(false, true, (_, _, _) => throw new IOException("custom write refused"));

        Assert.Throws<ArgumentException>(() => doc.Save(refusing));

        using var ok = new MemoryStream();
        doc.Save(ok);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(ok.ToArray()), StringComparison.Ordinal);
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
