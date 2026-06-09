// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;

namespace VellumPdf.Core;

/// <summary>
/// An indirect object wrapper: writes  N 0 obj … endobj  (ISO 32000-2 §7.3.10).
/// Generation is always 0 for fresh documents.
/// </summary>
public sealed class PdfIndirectObject : PdfObject
{
    /// <summary>The object number assigned to this indirect object.</summary>
    public int ObjectNumber { get; }
    /// <summary>The wrapped object value.</summary>
    public PdfObject Value { get; }

    /// <summary>Creates an indirect object with the given <paramref name="objectNumber"/> wrapping <paramref name="value"/>.</summary>
    public PdfIndirectObject(int objectNumber, PdfObject value)
    {
        ObjectNumber = objectNumber;
        Value = value;
    }

    /// <summary>An indirect reference (N 0 R) pointing at this object.</summary>
    public PdfIndirectReference Reference => new(ObjectNumber);

    /// <summary>Writes the serialised PDF representation to <paramref name="writer"/>.</summary>
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
