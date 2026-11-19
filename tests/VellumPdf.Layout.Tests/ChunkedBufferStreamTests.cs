// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Testing;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The buffer a save to a path builds the PDF in. 2.3.3 wrote that PDF straight to the file, so a
/// buffer that is one array would add a 2 GB limit it never had; this one is a list of chunks. A
/// small chunk size stands in for a large PDF: what is checked is that the copy crosses chunk
/// boundaries and gives the bytes back exactly.
/// </summary>
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

    /// <summary>
    /// 2.3.3 passed the token to the copy into the file, so a cancellation there throws. An empty buffer
    /// is included, because it has no chunk to look at the token at.
    /// </summary>
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buffer.CopyContentsToAsync(copy, cts.Token));

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

    /// <summary>
    /// A path save through a buffer of 7-byte chunks, which a PDF of a few kilobytes crosses hundreds of
    /// times, writes a PDF that is whole: it ends in the end-of-file marker and holds the page.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveToAPath_throughTinyChunks_writesAWholePdf(bool async)
    {
        var path = Path.Combine(Path.GetTempPath(), "chunked-" + Guid.NewGuid().ToString("N") + ".pdf");
        var before = Document.SaveBufferChunkSize;
        Document.SaveBufferChunkSize = 7;
        try
        {
            using var doc = new Document();
            doc.Add(new Paragraph("Hello chunks", new TextStyle { FontSize = 12 }));
            if (async)
                await doc.SaveAsync(path, TestContext.Current.CancellationToken);
            else
                doc.Save(path);

            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            Assert.True(bytes.Length > 7 * 50);
            Assert.StartsWith("%PDF-", text, StringComparison.Ordinal);
            Assert.EndsWith("%%EOF", text.TrimEnd(), StringComparison.Ordinal);
            Assert.Contains("Hello chunks", PdfTestUtil.DecompressAllFlatStreams(bytes), StringComparison.Ordinal);
        }
        finally
        {
            Document.SaveBufferChunkSize = before;
            File.Delete(path);
        }
    }
}
