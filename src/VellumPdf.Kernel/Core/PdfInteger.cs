// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;

namespace VellumPdf.Core;

public sealed class PdfInteger : PdfObject
{
    public long Value { get; }
    public PdfInteger(long value) => Value = value;

    public override void WriteTo(PdfWriter writer)
    {
        Span<byte> buf = stackalloc byte[20];
        Utf8Formatter.TryFormat(Value, buf, out var written);
        writer.WriteAscii(buf[..written]);
    }
}
