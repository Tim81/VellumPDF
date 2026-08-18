// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;

namespace VellumPdf.Core;

/// <summary>PDF indirect reference: N G R (ISO 32000-2 §7.3.10).</summary>
public sealed class PdfIndirectReference : PdfObject, IEquatable<PdfIndirectReference>
{
    /// <summary>The referenced object's number (the <c>N</c> in <c>N G R</c>).</summary>
    public int ObjectNumber { get; }

    /// <summary>The referenced object's generation (the <c>G</c> in <c>N G R</c>).</summary>
    public int Generation { get; }

    /// <summary>Creates an indirect reference to the object with the given number, at generation 0.</summary>
    public PdfIndirectReference(int objectNumber) => ObjectNumber = objectNumber;

    /// <summary>Creates an indirect reference to the object with the given number and generation.</summary>
    public PdfIndirectReference(int objectNumber, int generation)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    /// <summary>Writes the reference as <c>N G R</c>.</summary>
    public override void WriteTo(PdfWriter writer)
    {
        Span<byte> buf = stackalloc byte[12];
        Utf8Formatter.TryFormat(ObjectNumber, buf, out var len);
        writer.WriteAscii(buf[..len]);
        writer.WriteAscii(" "u8);
        Utf8Formatter.TryFormat(Generation, buf, out len);
        writer.WriteAscii(buf[..len]);
        writer.WriteAscii(" R"u8);
    }

    /// <summary>Two references are equal when they target the same object number and generation.</summary>
    public bool Equals(PdfIndirectReference? other) =>
        other is not null && ObjectNumber == other.ObjectNumber && Generation == other.Generation;
    /// <summary>Determines whether <paramref name="obj"/> is a reference to the same object number and generation.</summary>
    public override bool Equals(object? obj) => obj is PdfIndirectReference r && Equals(r);
    /// <summary>Returns a hash code derived from the object number and generation.</summary>
    public override int GetHashCode() => HashCode.Combine(ObjectNumber, Generation);
}
