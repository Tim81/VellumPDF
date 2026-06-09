// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace VellumPdf.Core;

/// <summary>PDF real (floating-point) numeric object (ISO 32000-2 §7.3.3).</summary>
public sealed class PdfReal : PdfObject
{
    /// <summary>The real value. Never NaN or Infinity.</summary>
    public double Value { get; }

    /// <summary>Creates a real object, rejecting NaN and Infinity which PDF cannot represent.</summary>
    public PdfReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException(
                $"PDF does not support NaN or Infinity as a real number value. Got: {value}", nameof(value));
        Value = value;
    }

    /// <inheritdoc />
    public override void WriteTo(PdfWriter writer)
    {
        // PDF requires a decimal point; use up to 5 decimal places (sufficient for coordinates).
        var s = Value.ToString("0.#####", CultureInfo.InvariantCulture);
        writer.WriteAsciiString(s);
    }
}
