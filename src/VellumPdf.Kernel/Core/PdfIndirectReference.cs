// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;

namespace VellumPdf.Core;

/// <summary>PDF indirect reference: N 0 R (ISO 32000-2 §7.3.10).</summary>
public sealed class PdfIndirectReference : PdfObject, IEquatable<PdfIndirectReference>
{
    /// <summary>The referenced object's number (the <c>N</c> in <c>N 0 R</c>).</summary>
    public int ObjectNumber { get; }
    /// <summary>Creates an indirect reference to the object with the given number.</summary>
    public PdfIndirectReference(int objectNumber) => ObjectNumber = objectNumber;

    /// <summary>Writes the reference as <c>N 0 R</c> (generation is always 0).</summary>
    public override void WriteTo(PdfWriter writer)
    {
        Span<byte> buf = stackalloc byte[12];
        Utf8Formatter.TryFormat(ObjectNumber, buf, out var len);
        writer.WriteAscii(buf[..len]);
        writer.WriteAscii(" 0 R"u8);
    }

    /// <summary>Two references are equal when they target the same object number.</summary>
    public bool Equals(PdfIndirectReference? other) => other is not null && ObjectNumber == other.ObjectNumber;
    /// <summary>Determines whether <paramref name="obj"/> is a reference to the same object number.</summary>
    public override bool Equals(object? obj) => obj is PdfIndirectReference r && Equals(r);
    /// <summary>Returns a hash code derived from the object number.</summary>
    public override int GetHashCode() => ObjectNumber;
}
