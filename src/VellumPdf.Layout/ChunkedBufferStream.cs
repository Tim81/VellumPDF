// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout;

/// <summary>
/// An append-only buffer that holds what is written to it in chunks, so it has no single array and
/// no 2 GB ceiling. <c>Document.Save(string)</c> builds the PDF in one of these before it empties
/// the file at the path (#508).
/// </summary>
/// <remarks>
/// A <see cref="MemoryStream"/> holds its bytes in one array, which cannot pass about 2 GB, and doubles
/// it as it grows. 2.3.3 wrote a path save straight to the file and had no such limit, so the buffer
/// must not add one. The cost is one more copy of the finished PDF in memory, in chunks, until it is
/// copied to the file. The first chunk is small and each next one twice the size of the last, up to
/// the chunk size, so a PDF of a few kilobytes does not allocate a whole chunk of 1 MiB, which would
/// land on the large object heap (the threshold is 85,000 bytes).
/// </remarks>
internal sealed class ChunkedBufferStream : Stream
{
    /// <summary>The largest a chunk grows to when no size is given: 1 MiB.</summary>
    internal const int DefaultChunkSize = 1 << 20;

    /// <summary>The size of the first chunk, when the chunk size allows it.</summary>
    internal const int FirstChunkSize = 4096;

    private readonly List<byte[]> _chunks = [];
    private readonly int _chunkSize;
    private long _length;
    private int _usedInLast;

    internal ChunkedBufferStream(int chunkSize = DefaultChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        _chunkSize = chunkSize;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    /// <summary>The number of chunks allocated, for the tests.</summary>
    internal int ChunkCount => _chunks.Count;

    /// <summary>The size of the largest chunk allocated, for the tests.</summary>
    internal int LargestChunkAllocated => _chunks.Count == 0 ? 0 : _chunks.Max(c => c.Length);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            if (_chunks.Count == 0 || _usedInLast == _chunks[^1].Length)
            {
                var size = _chunks.Count == 0 ? Math.Min(FirstChunkSize, _chunkSize) : (int)Math.Min((long)_chunks[^1].Length * 2, _chunkSize);
                _chunks.Add(new byte[size]);
                _usedInLast = 0;
            }

            var last = _chunks[^1];
            var take = Math.Min(buffer.Length, last.Length - _usedInLast);
            buffer[..take].CopyTo(last.AsSpan(_usedInLast));
            _usedInLast += take;
            _length += take;
            buffer = buffer[take..];
        }
    }

    /// <summary>Copies everything written so far to <paramref name="destination"/>, chunk by chunk.</summary>
    internal void CopyContentsTo(Stream destination)
    {
        var remaining = _length;
        foreach (var chunk in _chunks)
        {
            var take = (int)Math.Min(remaining, chunk.Length);
            destination.Write(chunk, 0, take);
            remaining -= take;
        }
    }

    /// <summary>
    /// Throws <see cref="TaskCanceledException"/> when <paramref name="cancellationToken"/> is
    /// cancelled. 2.3.3's asynchronous path save threw that type at every point where it looked at the
    /// token, because it handed the token to <c>Task.Run</c> and to the stream copy, which fail their
    /// task; a plain <see cref="OperationCanceledException"/> would slip past a
    /// <c>catch (TaskCanceledException)</c> written against it.
    /// </summary>
    internal static void ThrowIfCanceled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new TaskCanceledException(null, null, cancellationToken);
    }

    /// <summary>
    /// Copies everything written so far to <paramref name="destination"/>, chunk by chunk, looking at
    /// <paramref name="cancellationToken"/> before each one. A cancellation seen after the last chunk
    /// was written is not reported: the file is whole by then.
    /// </summary>
    internal async Task CopyContentsToAsync(Stream destination, CancellationToken cancellationToken)
    {
        var remaining = _length;
        foreach (var chunk in _chunks)
        {
            ThrowIfCanceled(cancellationToken);
            var take = (int)Math.Min(remaining, chunk.Length);
            await destination.WriteAsync(chunk.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            remaining -= take;
        }

        // An empty buffer has no chunk to look at the token at, and 2.3.3's copy looked at it anyway.
        if (_chunks.Count == 0)
            ThrowIfCanceled(cancellationToken);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
