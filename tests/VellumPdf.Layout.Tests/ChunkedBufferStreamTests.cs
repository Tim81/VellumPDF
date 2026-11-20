// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Testing;

namespace VellumPdf.Layout.Tests;

/// <summary>The buffer a save to a path builds the PDF in: a list of chunks, with a small chunk size standing in for a large PDF, so the copy crosses chunk boundaries and gives the bytes back exactly.</summary>
public sealed class ChunkedBufferStreamTests
{
    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7)).ToArray();

    [Fact]
    public void InternalsVisibleTo_publicKey_matchesActualSigningKey()
    {
        StrongNamePublicKeyAssertion.AssertGrantedByAll(Assembly.GetExecutingAssembly(), "VellumPdf.Layout");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    public void CopyContentsTo_givesBackExactlyWhatWasWritten_acrossChunks(int length)
    {
        var data = Pattern(length);
        var buffer = new ChunkedBufferStream(chunkSize: 8);

        // Written in uneven pieces, so a write starts and ends part-way through a chunk.
        var offset = 0;
        foreach (var piece in new[] { 3, 1, 9, 2, 100 })
        {
            var take = Math.Min(piece, length - offset);
            buffer.Write(data, offset, take);
            offset += take;
        }

        using var copy = new MemoryStream();
        buffer.CopyContentsTo(copy);

        Assert.Equal(length, buffer.Length);
        Assert.Equal(data, copy.ToArray());
        Assert.Equal((length + 7) / 8, buffer.ChunkCount);
    }

    [Fact]
    public async Task CopyContentsToAsync_givesBackExactlyWhatWasWritten_acrossChunks()
    {
        var data = Pattern(1000);
        var buffer = new ChunkedBufferStream(chunkSize: 64);
        buffer.Write(data);

        using var copy = new MemoryStream();
        await buffer.CopyContentsToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(data, copy.ToArray());
        Assert.True(buffer.ChunkCount > 1);
    }

    /// <summary>The token is passed to the copy into the file, so a cancellation there throws, as in 2.3.3; an empty buffer is included, since it has no chunk to look at the token at.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task CopyContentsToAsync_withACancelledToken_throwsBeforeCopyingAnything(int length)
    {
        var buffer = new ChunkedBufferStream(chunkSize: 64);
        buffer.Write(Pattern(length));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var copy = new MemoryStream();

        await Assert.ThrowsAsync<TaskCanceledException>(() => buffer.CopyContentsToAsync(copy, cts.Token));

        Assert.Equal(0, copy.Length);
    }

    [Fact]
    public void Position_isTheLength_andReadingIsRefused()
    {
        var buffer = new ChunkedBufferStream(chunkSize: 4);
        buffer.Write(Pattern(10));

        Assert.Equal(10, buffer.Position);
        Assert.False(buffer.CanRead);
        Assert.False(buffer.CanSeek);
        Assert.Throws<NotSupportedException>(() => buffer.Read(new byte[1], 0, 1));
    }

    /// <summary>A path save through a buffer of 7-byte chunks writes a PDF that is whole; the chunk count is asserted, so a save that ignored the size would fail.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveToAPath_throughTinyChunks_writesAWholePdf(bool async)
    {
        var path = Path.Combine(Path.GetTempPath(), "chunked-" + Guid.NewGuid().ToString("N") + ".pdf");
        var chunks = 0;
        try
        {
            using var doc = new Document { SaveBufferChunkSize = 7, BeforeSaveCopy = b => chunks = b.ChunkCount };
            doc.Add(new Paragraph("Hello chunks", new TextStyle { FontSize = 12 }));
            if (async)
                await doc.SaveAsync(path, TestContext.Current.CancellationToken);
            else
                doc.Save(path);

            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            Assert.Equal((bytes.Length + 6) / 7, chunks);
            Assert.True(chunks > 50);
            Assert.StartsWith("%PDF-", text, StringComparison.Ordinal);
            Assert.EndsWith("%%EOF", text.TrimEnd(), StringComparison.Ordinal);
            Assert.Contains("Hello chunks", PdfTestUtil.DecompressAllFlatStreams(bytes), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The first chunk is small and each next one doubles, up to the chunk size, so a small PDF does not allocate a 1 MiB chunk on the large object heap (threshold 85,000 bytes).</summary>
    [Fact]
    public void Chunks_startSmall_andDoubleUpToTheChunkSize()
    {
        var small = new ChunkedBufferStream();
        small.Write(Pattern(1500));
        Assert.Equal(1, small.ChunkCount);
        Assert.Equal(ChunkedBufferStream.FirstChunkSize, small.LargestChunkAllocated);

        var grown = new ChunkedBufferStream();
        var data = Pattern(40_000);
        grown.Write(data);
        // 4096 + 8192 + 16384 = 28672 < 40000 <= 28672 + 32768
        Assert.Equal(4, grown.ChunkCount);
        Assert.Equal(32768, grown.LargestChunkAllocated);

        var capped = new ChunkedBufferStream(chunkSize: 10_000);
        capped.Write(Pattern(25_000));
        Assert.Equal(10_000, capped.LargestChunkAllocated);

        using var copy = new MemoryStream();
        grown.CopyContentsTo(copy);
        Assert.Equal(data, copy.ToArray());
    }

    /// <summary>A cancellation that arrives while the last chunk is being written is not reported; one with a chunk still to write is, as a TaskCanceledException, and the chunks after it are not written.</summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(2, true)]
    public async Task CopyContentsToAsync_reportsACancellationOnlyWhileAChunkIsStillToWrite(int cancelOnWrite, bool throws)
    {
        var buffer = new ChunkedBufferStream(chunkSize: 8);
        buffer.Write(Pattern(24));                    // three chunks of 8
        using var cts = new CancellationTokenSource();
        using var destination = new CancelOnNthWrite(cancelOnWrite, cts);

        if (throws)
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() => buffer.CopyContentsToAsync(destination, cts.Token));
            Assert.Equal(16, destination.Written);
        }
        else
        {
            await buffer.CopyContentsToAsync(destination, cts.Token);
            Assert.Equal(24, destination.Written);
        }
    }

    private sealed class CancelOnNthWrite(int n, CancellationTokenSource cts) : Stream
    {
        private int _writes;
        public long Written { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written += buffer.Length;
            if (++_writes == n) cts.Cancel();
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
