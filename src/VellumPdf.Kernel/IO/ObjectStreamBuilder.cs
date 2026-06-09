// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;
using System.IO.Compression;
using VellumPdf.Core;

namespace VellumPdf.IO;

/// <summary>
/// Packs non-stream indirect objects into a single PDF object stream
/// (/Type /ObjStm, ISO 32000-2 §7.5.7).
///
/// Body layout:
///   Header: N pairs of "objNum offset " (ASCII, relative to /First byte).
///   Body: concatenated object bodies without "N 0 obj"/"endobj" wrappers.
///
/// The resulting stream is itself a normal indirect object that gets a type-1
/// xref entry. All objects packed inside get type-2 xref entries.
/// </summary>
internal sealed class ObjectStreamBuilder
{
    private readonly List<(int ObjNum, PdfObject Value)> _entries = [];

    /// <summary>Adds a non-stream object to be packed into this object stream.</summary>
    public void Add(int objectNumber, PdfObject value) => _entries.Add((objectNumber, value));

    public int Count => _entries.Count;

    /// <summary>
    /// Serialises the object stream body, compresses it with FlateDecode, and returns
    /// the compressed bytes along with the /First offset (byte offset from start of
    /// decoded body to the first object body, i.e. the length of the header section).
    /// </summary>
    public (byte[] CompressedBody, int First, int N) Build()
    {
        // Build the decoded body in two passes:
        //   Pass 1: render each object body to a MemoryStream, record offsets.
        //   Pass 2: assemble header + bodies, then compress.

        var objectBodies = new List<byte[]>(_entries.Count);

        foreach (var (_, value) in _entries)
        {
            var ms = new MemoryStream();
            var w = new PdfWriter(ms);
            value.WriteTo(w);
            w.Flush();
            objectBodies.Add(ms.ToArray());
        }

        // Build the ASCII header: "objNum offset objNum offset ..."
        // Offsets are relative to the first byte after the header (i.e. after /First).
        // We need to know all offsets before writing the header, so compute them first.
        var bodyOffsets = new int[_entries.Count];
        var cursor = 0;
        for (var i = 0; i < _entries.Count; i++)
        {
            bodyOffsets[i] = cursor;
            // Each object body is followed by a space separator (except the last);
            // we use a newline between bodies for readability and to act as a delimiter.
            cursor += objectBodies[i].Length + 1; // +1 for '\n' separator
        }

        // Build header string: "n1 o1 n2 o2 ... " (each pair space-separated)
        var headerMs = new MemoryStream();
        for (var i = 0; i < _entries.Count; i++)
        {
            AppendInt(headerMs, _entries[i].ObjNum);
            headerMs.WriteByte((byte)' ');
            AppendInt(headerMs, bodyOffsets[i]);
            headerMs.WriteByte((byte)' ');
        }

        var headerBytes = headerMs.ToArray();
        var first = headerBytes.Length; // offset from start of decoded body to first object

        // Assemble the full decoded body
        var decodedMs = new MemoryStream();
        decodedMs.Write(headerBytes);
        for (var i = 0; i < _entries.Count; i++)
        {
            decodedMs.Write(objectBodies[i]);
            decodedMs.WriteByte((byte)'\n');
        }

        var decoded = decodedMs.ToArray();

        // FlateDecode compress (ZLib, RFC 1950)
        var compMs = new MemoryStream();
        using (var z = new ZLibStream(compMs, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(decoded);

        return (compMs.ToArray(), first, _entries.Count);
    }

    private static void AppendInt(MemoryStream ms, int n)
    {
        Span<byte> buf = stackalloc byte[12];
        Utf8Formatter.TryFormat(n, buf, out var len);
        ms.Write(buf[..len]);
    }
}
