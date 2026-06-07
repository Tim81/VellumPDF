// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.IO;

/// <summary>
/// Wraps a <see cref="Stream"/> and tracks the current byte offset so the
/// cross-reference table can record exact object positions.
///
/// The position is tracked with an internal counter so non-seekable streams
/// (e.g. NetworkStream, GZipStream) are fully supported.
/// </summary>
public sealed class PdfWriter
{
    private readonly Stream _stream;
    private long _position;

    /// <summary>
    /// Number of bytes written so far. This is always authoritative, even for
    /// non-seekable streams where <c>Stream.Position</c> would throw.
    /// </summary>
    public long Position => _position;

    public PdfWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        _stream = stream;
    }

    public void WriteByte(byte b)
    {
        _stream.WriteByte(b);
        _position++;
    }

    public void WriteAscii(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        _position += bytes.Length;
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        _position += bytes.Length;
    }

    public void WriteAsciiString(string s)
    {
        // Caller guarantees ASCII-safe content (numbers, names, operators).
        var bytes = Encoding.ASCII.GetBytes(s);
        _stream.Write(bytes);
        _position += bytes.Length;
    }

    public void Flush() => _stream.Flush();
}
