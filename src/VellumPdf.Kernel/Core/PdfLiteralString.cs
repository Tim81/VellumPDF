// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>
/// PDF literal string object written as (…) with balanced-parenthesis and
/// backslash escaping (ISO 32000-2 §7.3.4.2).
/// </summary>
public sealed class PdfLiteralString : PdfObject
{
    /// <summary>The raw (unescaped) string bytes.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Creates a literal string from the given raw bytes.</summary>
    public PdfLiteralString(ReadOnlyMemory<byte> bytes) => Bytes = bytes;

    /// <summary>Encodes a UTF-16BE string with BOM (used for metadata fields).</summary>
    public static PdfLiteralString FromUnicode(string value)
    {
        var bytes = new byte[2 + value.Length * 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        for (var i = 0; i < value.Length; i++)
        {
            var c = (ushort)value[i];
            bytes[2 + i * 2] = (byte)(c >> 8);
            bytes[2 + i * 2 + 1] = (byte)(c & 0xFF);
        }
        return new PdfLiteralString(bytes);
    }

    /// <inheritdoc />
    public override void WriteTo(PdfWriter writer)
    {
        // When an encryptor is active, encrypt the raw bytes first and then
        // write the escaped result. The (…) delimiters wrap the encrypted payload.
        var payload = writer.Encryptor is { } enc
            ? enc.Encrypt(Bytes.Span)
            : Bytes.Span.ToArray();

        writer.WriteByte((byte)'(');
        foreach (var b in payload.AsSpan())
        {
            switch (b)
            {
                case (byte)'(': writer.WriteByte((byte)'\\'); writer.WriteByte((byte)'('); break;
                case (byte)')': writer.WriteByte((byte)'\\'); writer.WriteByte((byte)')'); break;
                case (byte)'\\': writer.WriteByte((byte)'\\'); writer.WriteByte((byte)'\\'); break;
                case 0x0A: writer.WriteByte((byte)'\\'); writer.WriteByte((byte)'n'); break;
                case 0x0D: writer.WriteByte((byte)'\\'); writer.WriteByte((byte)'r'); break;
                default: writer.WriteByte(b); break;
            }
        }
        writer.WriteByte((byte)')');
    }
}
