// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Encryption;

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
    /// When non-null, string and stream content is encrypted via this encryptor
    /// before being written. Set to null while writing the /Encrypt dictionary
    /// itself and for trailer /ID (which must not be encrypted).
    /// </summary>
    public IPdfEncryptor? Encryptor { get; set; }

    /// <summary>
    /// Number of bytes written so far. This is always authoritative, even for
    /// non-seekable streams where <c>Stream.Position</c> would throw.
    /// </summary>
    public long Position => _position;

    /// <summary>Creates a writer over the given writable <paramref name="stream"/>.</summary>
    public PdfWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        _stream = stream;
    }

    /// <summary>Writes a single byte and advances the tracked position.</summary>
    public void WriteByte(byte b)
    {
        _stream.WriteByte(b);
        _position++;
    }

    /// <summary>Writes a span of pre-encoded ASCII bytes and advances the tracked position.</summary>
    public void WriteAscii(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        _position += bytes.Length;
    }

    /// <summary>Writes raw bytes verbatim (e.g. stream content) and advances the tracked position.</summary>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
        _position += bytes.Length;
    }

    /// <summary>Encodes an ASCII-safe string and writes it, advancing the tracked position.</summary>
    public void WriteAsciiString(string s)
    {
        // Caller guarantees ASCII-safe content (numbers, names, operators).
        var bytes = Encoding.ASCII.GetBytes(s);
        _stream.Write(bytes);
        _position += bytes.Length;
    }

    /// <summary>Flushes any buffered bytes to the underlying stream.</summary>
    public void Flush() => _stream.Flush();
}
