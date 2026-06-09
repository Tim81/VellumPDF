// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;

namespace VellumPdf.Core;

/// <summary>PDF integer numeric object (ISO 32000-2 §7.3.3).</summary>
public sealed class PdfInteger : PdfObject
{
    /// <summary>The integer value.</summary>
    public long Value { get; }

    /// <summary>Creates an integer object with the given value.</summary>
    public PdfInteger(long value) => Value = value;

    /// <inheritdoc />
    public override void WriteTo(PdfWriter writer)
    {
        Span<byte> buf = stackalloc byte[20];
        Utf8Formatter.TryFormat(Value, buf, out var written);
        writer.WriteAscii(buf[..written]);
    }
}
