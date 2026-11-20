// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>PDF real (floating-point) numeric object (ISO 32000-2 §7.3.3).</summary>
/// <remarks>
/// The value is written rounded to at most five decimal places. When the rounded text has no
/// decimal point and lies inside −2,147,483,648 to 2,147,483,647 it is an integer token, which
/// 7.3.3 allows wherever a real is expected (<c>3</c> for 3.0). Otherwise it is a real token
/// (<c>2147483648.0</c>), so no integer token outside the 32-bit range of ISO 32000-2 Table C.1
/// is written. The rule reads the rounded text: 2147483647.000001 is written as
/// <c>2147483647</c> and 2147483648.000001 as <c>2147483648.0</c>.
/// </remarks>
public sealed class PdfReal : PdfObject
{
    /// <summary>The real value. Never NaN or Infinity.</summary>
    public double Value { get; }

    /// <summary>Creates a real object, rejecting NaN and Infinity which PDF cannot represent.</summary>
    /// <param name="value">The value. It must be finite.</param>
    /// <remarks>
    /// <b>A NaN or infinite value is refused.</b> The constructor throws
    /// <see cref="ArgumentException"/> at the call, because PDF has no token for either.
    /// <para><b>Do not pass</b> a magnitude above 3.403 × 10^38. It is written, as a real token,
    /// but it lies outside the real range of ISO 19005-2 6.1.13, so a PDF/A file that holds it
    /// fails validation.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="value"/> is NaN, positive infinity or
    /// negative infinity.</exception>
    public PdfReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException(
                $"PDF does not support NaN or Infinity as a real number value. Got: {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}", nameof(value));
        Value = value;
    }

    /// <inheritdoc />
    public override void WriteTo(PdfWriter writer)
    {
        // Up to 5 decimal places (sufficient for coordinates). A whole value written without a
        // decimal point is an integer token, which 7.3.3 allows for a real; PdfNumberFormat keeps
        // it inside the 32-bit range and gives anything outside it ".0".
        writer.WriteAsciiString(PdfNumberFormat.FormatReal(Value));
    }
}
