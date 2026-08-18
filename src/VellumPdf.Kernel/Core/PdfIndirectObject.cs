// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;

namespace VellumPdf.Core;

/// <summary>
/// An indirect object wrapper: writes  N G obj … endobj  (ISO 32000-2 §7.3.10).
/// Generation is 0 unless constructed otherwise — every object a fresh document mints is
/// generation 0; a nonzero generation only matters when re-emitting an object that already
/// exists in a document being incrementally updated (see <see cref="Generation"/>).
/// </summary>
public sealed class PdfIndirectObject : PdfObject
{
    /// <summary>The object number assigned to this indirect object.</summary>
    public int ObjectNumber { get; }

    /// <summary>The generation this object is written at (the <c>G</c> in <c>N G obj</c>).</summary>
    public int Generation { get; }

    /// <summary>The wrapped object value.</summary>
    public PdfObject Value { get; }

    /// <summary>
    /// Creates an indirect object with the given <paramref name="objectNumber"/>, at generation 0,
    /// wrapping <paramref name="value"/>.
    /// </summary>
    public PdfIndirectObject(int objectNumber, PdfObject value)
    {
        ObjectNumber = objectNumber;
        Value = value;
    }

    /// <summary>
    /// Creates an indirect object with the given <paramref name="objectNumber"/> and
    /// <paramref name="generation"/>, wrapping <paramref name="value"/>. Re-emitting an object that
    /// already exists in a document — an incremental update's job — must keep that object's
    /// existing generation; only a freed number being reused for a different object advances it
    /// (ISO 32000-2 §7.5.4).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generation"/> is negative.</exception>
    public PdfIndirectObject(int objectNumber, int generation, PdfObject value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ObjectNumber = objectNumber;
        Generation = generation;
        Value = value;
    }

    /// <summary>An indirect reference (N G R) pointing at this object.</summary>
    public PdfIndirectReference Reference => new(ObjectNumber, Generation);

    /// <summary>Writes the serialised PDF representation to <paramref name="writer"/>.</summary>
    public override void WriteTo(PdfWriter writer)
    {
        WriteInt(writer, ObjectNumber);
        writer.WriteAscii(" "u8);
        WriteInt(writer, Generation);
        writer.WriteAscii(" obj\n"u8);
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
