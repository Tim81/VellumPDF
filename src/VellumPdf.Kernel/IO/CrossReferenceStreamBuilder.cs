// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;
using System.IO.Compression;
using VellumPdf.Core;

namespace VellumPdf.IO;

/// <summary>
/// Builds and writes the cross-reference stream (ISO 32000-2 §7.5.8).
///
/// Each entry is a fixed-width binary row with three fields:
///   W = [1 4 2]  (type=1 byte, field2=4 bytes, field3=2 bytes)
///
/// Entry types:
///   0  free object:         f2 = next-free obj num,  f3 = generation (65535 for head)
///   1  uncompressed object: f2 = byte offset,        f3 = generation (0)
///   2  in object stream:    f2 = ObjStm obj number,  f3 = index in ObjStm
///
/// The entry for object 0 is always type-0 (free-list head): 0 0 65535.
/// W = [1 4 2]: type=1 byte, field2=4 bytes (max ~4 GB offset), field3=2 bytes.
/// </summary>
internal sealed class CrossReferenceStreamBuilder
{
    // W widths: [1 4 2]
    private const int W1 = 1;
    private const int W2 = 4;
    private const int W3 = 2;
    private const int RowSize = W1 + W2 + W3; // 7

    private readonly struct XrefRow
    {
        public readonly byte Type;
        public readonly long Field2;
        public readonly int Field3;

        public XrefRow(byte type, long field2, int field3)
        {
            Type = type;
            Field2 = field2;
            Field3 = field3;
        }
    }

    // Indexed by object number; slot 0 = object 0 (pre-filled with free-list head).
    private readonly List<XrefRow> _rows = [new XrefRow(0, 0, 65535)];

    /// <summary>Sets the xref entry for the given object number (1-based).</summary>
    public void SetUncompressed(int objectNumber, long byteOffset)
    {
        EnsureSlot(objectNumber);
        _rows[objectNumber] = new XrefRow(1, byteOffset, 0);
    }

    /// <summary>Sets the xref entry for a type-2 (in object stream) object.</summary>
    public void SetInObjectStream(int objectNumber, int objStmObjectNumber, int indexInObjStm)
    {
        EnsureSlot(objectNumber);
        _rows[objectNumber] = new XrefRow(2, objStmObjectNumber, indexInObjStm);
    }

    /// <summary>Total number of entries including object 0 (= /Size value).</summary>
    public int Size => _rows.Count;

    private void EnsureSlot(int objectNumber)
    {
        while (_rows.Count <= objectNumber)
            _rows.Add(new XrefRow(0, 0, 0)); // placeholder
    }

    /// <summary>
    /// Writes the complete XRef stream indirect object to <paramref name="writer"/>,
    /// including the "N 0 obj" wrapper, the stream dictionary, "stream"/"endstream", and "endobj".
    /// Follows with "startxref\nN\n%%EOF\n".
    /// Returns the byte offset of the XRef stream object (for startxref).
    ///
    /// The XRef stream's own type-1 xref entry is included in the table (pointing to
    /// <paramref name="xrefObjNumber"/> at the offset where this method starts writing).
    /// </summary>
    public long WriteXRefStream(
        PdfWriter writer,
        int xrefObjNumber,
        PdfIndirectReference catalogRef,
        PdfIndirectReference? infoRef,
        ReadOnlySpan<byte> documentId)
    {
        var xrefOffset = writer.Position;

        // Record the XRef stream's own type-1 entry so /Size covers it.
        SetUncompressed(xrefObjNumber, xrefOffset);

        // Encode the rows into binary
        var decoded = EncodeRows();

        // FlateDecode compress (ZLib, RFC 1950)
        var compMs = new MemoryStream();
        using (var z = new ZLibStream(compMs, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(decoded);
        var compressed = compMs.ToArray();

        // Build the XRef stream dictionary (which IS the trailer dict)
        var dict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("XRef"))
            .Set(PdfName.Size, Size)
            .Set(new PdfName("W"), new PdfArray([
                new PdfInteger(W1),
                new PdfInteger(W2),
                new PdfInteger(W3)
            ]))
            .Set(PdfName.Root, catalogRef)
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(PdfName.Length, compressed.Length);

        if (infoRef is not null)
            dict.Set(PdfName.Info, infoRef);

        if (!documentId.IsEmpty && documentId.Length == 16)
        {
            var hexId = new PdfHexString(documentId.ToArray());
            var hexId2 = new PdfHexString(documentId.ToArray());
            dict.Set(PdfName.ID, new PdfArray([hexId, hexId2]));
        }

        // Write "N 0 obj\n<dict>\nstream\n<compressed>\nendstream\nendobj\n"
        WriteInt(writer, xrefObjNumber);
        writer.WriteAscii(" 0 obj\n"u8);
        dict.WriteTo(writer);
        writer.WriteAscii("\nstream\n"u8);
        writer.WriteRaw(compressed);
        writer.WriteAscii("\nendstream\nendobj\n"u8);

        // startxref + %%EOF
        writer.WriteAscii("startxref\n"u8);
        WriteInt(writer, xrefOffset);
        writer.WriteAscii("\n%%EOF\n"u8);

        return xrefOffset;
    }

    private byte[] EncodeRows()
    {
        var buf = new byte[_rows.Count * RowSize];
        var pos = 0;
        foreach (var row in _rows)
        {
            // Field 1: type (1 byte)
            buf[pos] = row.Type;
            pos += W1;

            // Field 2: 4 bytes big-endian
            var f2 = row.Field2;
            buf[pos] = (byte)((f2 >> 24) & 0xFF);
            buf[pos + 1] = (byte)((f2 >> 16) & 0xFF);
            buf[pos + 2] = (byte)((f2 >> 8) & 0xFF);
            buf[pos + 3] = (byte)(f2 & 0xFF);
            pos += W2;

            // Field 3: 2 bytes big-endian
            var f3 = row.Field3;
            buf[pos] = (byte)((f3 >> 8) & 0xFF);
            buf[pos + 1] = (byte)(f3 & 0xFF);
            pos += W3;
        }
        return buf;
    }

    private static void WriteInt(PdfWriter w, long n)
    {
        Span<byte> buf = stackalloc byte[20];
        Utf8Formatter.TryFormat(n, buf, out var len);
        w.WriteAscii(buf[..len]);
    }
}
