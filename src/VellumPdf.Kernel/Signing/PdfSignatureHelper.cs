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
    /// Returns the /Contents placeholder value emitted into the raw signature dictionary.
    /// The value is a plain hex-string token (<c>&lt;000…0&gt;</c>) with no intervening
    /// comment or whitespace.  <c>VellumPdf.Signing.PdfCmsSigner</c> locates it by
    /// anchoring on the (unique, in-place-overwritten) <see cref="ByteRangePlaceholder"/>:
    /// the only bytes between the end of the ByteRange placeholder and the opening
    /// <c>&lt;</c> of the /Contents hex string are the fixed sequence <c>]\n/Contents </c>,
    /// which contains no caller-controlled data, so adversarial metadata cannot match it.
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
