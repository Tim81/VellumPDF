// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace VellumPdf.Core;

public sealed class PdfReal : PdfObject
{
    public double Value { get; }
    public PdfReal(double value) => Value = value;

    public override void WriteTo(PdfWriter writer)
    {
        // PDF requires a decimal point; use up to 5 decimal places (sufficient for coordinates).
        var s = Value.ToString("0.#####", CultureInfo.InvariantCulture);
        writer.WriteAsciiString(s);
    }
}
