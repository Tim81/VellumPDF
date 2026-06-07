// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF hex string object written as &lt;HEXHEX…&gt; (ISO 32000-2 §7.3.4.3).</summary>
public sealed class PdfHexString : PdfObject
{
    public ReadOnlyMemory<byte> Bytes { get; }
    public PdfHexString(ReadOnlyMemory<byte> bytes) => Bytes = bytes;

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteByte((byte)'<');
        foreach (var b in Bytes.Span)
        {
            writer.WriteByte(Nibble(b >> 4));
            writer.WriteByte(Nibble(b & 0xF));
        }
        writer.WriteByte((byte)'>');
    }

    private static byte Nibble(int n) => (byte)(n < 10 ? '0' + n : 'A' + n - 10);
}
