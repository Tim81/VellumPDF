// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;

namespace VellumPdf.Core;

/// <summary>
/// An indirect object wrapper: writes  N 0 obj … endobj  (ISO 32000-2 §7.3.10).
/// Generation is always 0 for fresh documents.
/// </summary>
public sealed class PdfIndirectObject : PdfObject
{
    public int ObjectNumber { get; }
    public PdfObject Value  { get; }

    public PdfIndirectObject(int objectNumber, PdfObject value)
    {
        ObjectNumber = objectNumber;
        Value = value;
    }

    public PdfIndirectReference Reference => new(ObjectNumber);

    public override void WriteTo(PdfWriter writer)
    {
        WriteInt(writer, ObjectNumber);
        writer.WriteAscii(" 0 obj\n"u8);
        Value.WriteTo(writer);
        writer.WriteAscii("\nendobj"u8);
    }

    private static void WriteInt(PdfWriter writer, int n)
    {
        Span<byte> buf = stackalloc byte[12];
        Utf8Formatter.TryFormat(n, buf, out var len);
        writer.WriteAscii(buf[..len]);
    }
}
