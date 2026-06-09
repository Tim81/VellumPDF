// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF hex string object written as &lt;HEXHEX…&gt; (ISO 32000-2 §7.3.4.3).</summary>
public sealed class PdfHexString : PdfObject
{
    /// <summary>The raw string bytes (serialised as hexadecimal).</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Creates a hex string from the given raw bytes.</summary>
    public PdfHexString(ReadOnlyMemory<byte> bytes) => Bytes = bytes;

    /// <inheritdoc />
    public override void WriteTo(PdfWriter writer)
    {
        // When an encryptor is active, encrypt the raw bytes first.
        var payload = writer.Encryptor is { } enc
            ? enc.Encrypt(Bytes.Span)
            : Bytes.Span.ToArray();

        writer.WriteByte((byte)'<');
        foreach (var b in payload.AsSpan())
        {
            writer.WriteByte(Nibble(b >> 4));
            writer.WriteByte(Nibble(b & 0xF));
        }
        writer.WriteByte((byte)'>');
    }

    private static byte Nibble(int n) => (byte)(n < 10 ? '0' + n : 'A' + n - 10);
}
