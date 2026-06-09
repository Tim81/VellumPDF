// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Document;

/// <summary>
/// Kernel-internal helpers for constructing PDF signature placeholder structures.
/// No cryptography — all CMS computation lives in <c>VellumPdf.Signing</c>.
/// </summary>
internal static class PdfSignatureHelper
{
    // Fixed width of each ByteRange field (decimal digits). A value of 10 supports
    // files up to 9,999,999,999 bytes (~9.3 GB) — ample for any realistic PDF.
    internal const int ByteRangeFieldWidth = 10;

    // The placeholder text written for /ByteRange (four fixed-width fields).
    // Format: [NNNNNNNNNN NNNNNNNNNN NNNNNNNNNN NNNNNNNNNN]
    internal static readonly byte[] ByteRangePlaceholder =
        Encoding.ASCII.GetBytes(BuildByteRangePlaceholderString());

    // Sentinel used to find the /Contents hex string in the raw bytes.
    internal static readonly byte[] ContentsKey = "/Contents <"u8.ToArray();

    private static string BuildByteRangePlaceholderString()
    {
        var val = new string('9', ByteRangeFieldWidth);
        return $"[{val} {val} {val} {val}]";
    }

    /// <summary>
    /// Returns the /ByteRange placeholder string (four fixed-width '9' fields).
    /// </summary>
    internal static string GetByteRangePlaceholderString() => BuildByteRangePlaceholderString();

    /// <summary>
    /// Returns the placeholder /Contents hex string: a hex string of
    /// <paramref name="estimatedSizeBytes"/> * 2 zero chars, wrapped in angle brackets.
    /// </summary>
    internal static string GetContentsPlaceholder(int estimatedSizeBytes)
        => "<" + new string('0', estimatedSizeBytes * 2) + ">";

    /// <summary>
    /// Builds the PDF date string in the format D:YYYYMMDDHHmmSS+00'00'.
    /// </summary>
    internal static string FormatPdfDate(DateTimeOffset dt)
    {
        var u = dt.ToUniversalTime();
        return $"D:{u.Year:D4}{u.Month:D2}{u.Day:D2}{u.Hour:D2}{u.Minute:D2}{u.Second:D2}+00'00'";
    }
}
