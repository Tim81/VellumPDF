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

    // Unique sentinel written as a PDF comment on the line immediately before the
    // /Contents hex value in the signature dictionary. The value is a fixed opaque
    // token that cannot appear in any user-supplied metadata (Reason, Location, …).
    // PdfCmsSigner searches for this sentinel then the following '<' to locate the
    // /Contents placeholder unambiguously — adversarial metadata cannot match it.
    internal const string ContentsSentinel = "%VELLUM_SIG_CONTENTS_3F8A2B1C4D5E6F70";

    // Search key used by PdfCmsSigner to locate the /Contents hex string:
    // the sentinel comment line followed immediately by the opening '<'.
    internal static readonly byte[] ContentsKey =
        Encoding.ASCII.GetBytes(ContentsSentinel + "\n<");

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
    /// The value begins with <see cref="ContentsSentinel"/> on its own line so that
    /// <c>VellumPdf.Signing.PdfCmsSigner</c> can locate the hex string unambiguously
    /// even when user-supplied metadata (Reason/Location/…) contains the text
    /// <c>/Contents &lt;</c>.  The sentinel is a valid PDF comment between the
    /// <c>/Contents</c> key token and its hex-string value — PDF allows whitespace and
    /// comments between tokens inside a dictionary.
    /// </summary>
    internal static string GetContentsPlaceholder(int estimatedSizeBytes)
        => ContentsSentinel + "\n<" + new string('0', estimatedSizeBytes * 2) + ">";

    /// <summary>
    /// Builds the PDF date string in the format D:YYYYMMDDHHmmSS+00'00'.
    /// </summary>
    internal static string FormatPdfDate(DateTimeOffset dt)
    {
        var u = dt.ToUniversalTime();
        return $"D:{u.Year:D4}{u.Month:D2}{u.Day:D2}{u.Hour:D2}{u.Minute:D2}{u.Second:D2}+00'00'";
    }
}
