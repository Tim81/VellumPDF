// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;
using VellumPdf.Core;

namespace VellumPdf.IO;

/// <summary>
/// Builds the classic xref table (§7.5.4) and writes the trailer.
/// Object numbers start at 1; entry 0 is the free-list head (always present).
///
/// Incremental-update seam: a future revision can supply a non-zero prevXrefOffset
/// and an existing object count to chain /Prev in the trailer (§7.5.6).
/// </summary>
public sealed class CrossReferenceBuilder
{
    private readonly List<long> _offsets = []; // index = objectNumber - 1

    public int NextObjectNumber => _offsets.Count + 1;

    /// <summary>Records the byte offset of the next indirect object before it is written.</summary>
    public int ReserveObjectNumber(long byteOffset)
    {
        var n = NextObjectNumber;
        _offsets.Add(byteOffset);
        return n;
    }

    /// <summary>
    /// Writes the xref section + trailer and returns the offset of the xref keyword
    /// (needed for %%EOF startxref).
    /// </summary>
    public long WriteXrefAndTrailer(
        PdfWriter writer,
        PdfIndirectReference catalogRef,
        PdfIndirectReference? infoRef,
        long prevXrefOffset = 0)
    {
        var xrefOffset = writer.Position;
        var count = _offsets.Count + 1; // +1 for the free-list head (object 0)

        writer.WriteAscii("xref\n"u8);
        WriteInt(writer, 0);
        writer.WriteByte((byte)' ');
        WriteInt(writer, count);
        writer.WriteByte((byte)'\n');

        // Object 0: free-list head — always "0000000000 65535 f\r\n"
        writer.WriteAscii("0000000000 65535 f\r\n"u8);

        foreach (var offset in _offsets)
        {
            // Each in-use entry: 10-digit offset  5-digit gen  n  CRLF (20 bytes)
            Write10Digits(writer, offset);
            writer.WriteAscii(" 00000 n\r\n"u8);
        }

        // Trailer dictionary
        writer.WriteAscii("trailer\n"u8);
        var trailer = new PdfDictionary()
            .Set(PdfName.Size, count)
            .Set(PdfName.Root, catalogRef);

        if (infoRef is not null)
            trailer.Set(PdfName.Info, infoRef);

        if (prevXrefOffset > 0)
            trailer.Set(PdfName.Prev, prevXrefOffset);

        trailer.WriteTo(writer);
        writer.WriteAscii("\nstartxref\n"u8);
        WriteInt(writer, xrefOffset);
        writer.WriteAscii("\n%%EOF\n"u8);

        return xrefOffset;
    }

    private static void WriteInt(PdfWriter w, long n)
    {
        Span<byte> buf = stackalloc byte[20];
        Utf8Formatter.TryFormat(n, buf, out var len);
        w.WriteAscii(buf[..len]);
    }

    private static void Write10Digits(PdfWriter w, long n)
    {
        Span<byte> buf = stackalloc byte[10];
        buf.Fill((byte)'0');
        var tmp = n;
        for (var i = 9; i >= 0 && tmp > 0; i--)
        {
            buf[i] = (byte)('0' + tmp % 10);
            tmp /= 10;
        }
        w.WriteAscii(buf);
    }
}
