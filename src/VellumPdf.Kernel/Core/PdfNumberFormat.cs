// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace VellumPdf.Core;

/// <summary>
/// The one place a <see cref="double"/> becomes the text of a PDF number token, shared by
/// <see cref="PdfReal"/>, the content-stream operands of the canvas and the numbers of the
/// AcroForm appearance streams and <c>/DA</c> strings, so the three cannot drift apart.
/// </summary>
internal static class PdfNumberFormat
{
    /// <summary>
    /// Formats <paramref name="value"/> with <paramref name="format"/> (a custom numeric format
    /// string with a fraction part). The rule reads the written text, not the double: when the text
    /// has no decimal point and is not a 32-bit integer, <c>.0</c> is appended, so the token is a
    /// real. ISO 32000-2 7.3.3 allows an integer wherever a real is expected, but ISO 32000-1 Table
    /// C.1 gives 2,147,483,647 as the largest integer (ISO 32000-2 Table C.1 says only that integers
    /// "can often be expressed within 32 bits") and ISO 19005-2 6.1.13 holds a PDF/A file to that range. A whole value of
    /// 2^63 or more written as an integer token is also one a reader cannot convert to a 64-bit
    /// integer. It does not refuse NaN or an infinity; each caller does that its own way.
    /// </summary>
    internal static string Format(double value, string format)
    {
        var s = value.ToString(format, CultureInfo.InvariantCulture);
        if (s.Contains('.'))
            return s;
        return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            ? s
            : s + ".0";
    }

    /// <summary>Formats a real with at most five decimal places (<see cref="PdfReal"/>, canvas operands).</summary>
    internal static string FormatReal(double value) => Format(value, "0.#####");

    /// <summary>Formats a number with at most three decimal places (AcroForm appearance streams).</summary>
    internal static string FormatAppearance(double value) => Format(value, "0.###");
}
