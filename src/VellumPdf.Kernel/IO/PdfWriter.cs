// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.IO;

/// <summary>
/// Wraps a <see cref="Stream"/> and tracks the current byte offset so the
/// cross-reference table can record exact object positions.
/// </summary>
public sealed class PdfWriter
{
    private readonly Stream _stream;

    public long Position => _stream.Position;

    public PdfWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        _stream = stream;
    }

    public void WriteByte(byte b) => _stream.WriteByte(b);

    public void WriteAscii(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public void WriteRaw(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

    public void WriteAsciiString(string s)
    {
        // Caller guarantees ASCII-safe content (numbers, names, operators).
        var bytes = Encoding.ASCII.GetBytes(s);
        _stream.Write(bytes);
    }

    public void Flush() => _stream.Flush();
}
