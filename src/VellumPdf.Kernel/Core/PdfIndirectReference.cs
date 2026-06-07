// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;

namespace VellumPdf.Core;

/// <summary>PDF indirect reference: N 0 R (ISO 32000-2 §7.3.10).</summary>
public sealed class PdfIndirectReference : PdfObject, IEquatable<PdfIndirectReference>
{
    public int ObjectNumber { get; }
    public PdfIndirectReference(int objectNumber) => ObjectNumber = objectNumber;

    public override void WriteTo(PdfWriter writer)
    {
        Span<byte> buf = stackalloc byte[12];
        Utf8Formatter.TryFormat(ObjectNumber, buf, out var len);
        writer.WriteAscii(buf[..len]);
        writer.WriteAscii(" 0 R"u8);
    }

    public bool Equals(PdfIndirectReference? other) => other is not null && ObjectNumber == other.ObjectNumber;
    public override bool Equals(object? obj) => obj is PdfIndirectReference r && Equals(r);
    public override int GetHashCode() => ObjectNumber;
}
