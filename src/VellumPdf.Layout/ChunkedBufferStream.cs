// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout;

/// <summary>
/// An append-only buffer that holds what is written to it in fixed-size chunks, so it has no single
/// array and no 2 GB ceiling. <c>Document.Save(string)</c> builds the PDF in one of these before it
/// empties the file at the path (#508).
/// </summary>
/// <remarks>
/// A <see cref="MemoryStream"/> holds its bytes in one array, which cannot pass about 2 GB, and doubles
/// it as it grows. 2.3.3 wrote a path save straight to the file and had no such limit, so the buffer
/// must not add one. The cost is one more copy of the finished PDF in memory, in chunks, until it is
/// copied to the file.
/// </remarks>
internal sealed class ChunkedBufferStream : Stream
{
    /// <summary>The size of a chunk when none is given: 1 MiB.</summary>
    internal const int DefaultChunkSize = 1 << 20;

    private readonly List<byte[]> _chunks = [];
    private readonly int _chunkSize;
    private long _length;

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

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var used = (int)(_length % _chunkSize);
            if (used == 0)
                _chunks.Add(new byte[_chunkSize]);

            var take = Math.Min(buffer.Length, _chunkSize - used);
            buffer[..take].CopyTo(_chunks[^1].AsSpan(used));
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
            var take = (int)Math.Min(remaining, _chunkSize);
            destination.Write(chunk, 0, take);
            remaining -= take;
        }
    }

    /// <summary>
    /// Copies everything written so far to <paramref name="destination"/>, chunk by chunk, looking at
    /// <paramref name="cancellationToken"/> before each one.
    /// </summary>
    internal async Task CopyContentsToAsync(Stream destination, CancellationToken cancellationToken)
    {
        var remaining = _length;
        foreach (var chunk in _chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = (int)Math.Min(remaining, _chunkSize);
            await destination.WriteAsync(chunk.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            remaining -= take;
        }

        // An empty buffer has no chunk to look at the token at, and 2.3.3's copy looked at it anyway.
        cancellationToken.ThrowIfCancellationRequested();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
