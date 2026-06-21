// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.2 (File header). A PDF/A-2 file shall begin, at byte offset 0, with a
/// header of the form <c>%PDF-1.n</c> where <c>n</c> is a single digit 0–7, and the header
/// line shall be immediately followed by a comment line containing at least four bytes whose
/// values are 128 or greater (the binary marker).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.2 and ISO 32000-1:2008, 7.5.2. Clean-room: derived from
/// the specification text, not from any third-party validation profile. PDF/A-2 is defined
/// against PDF 1.7, so a <c>%PDF-2.0</c> header is not valid PDF/A-2.
/// </remarks>
internal sealed class FileHeaderRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.2-file-header";

    public string Clause => "ISO 19005-2:2011, 6.1.2";

    // "%PDF-1." — the fixed prefix; the byte that follows must be a digit 0–7.
    private static readonly byte[] _prefix = "%PDF-1."u8.ToArray();

    public void Evaluate(PreflightContext context)
    {
        var bytes = context.FileBytes.Span;

        if (!HasValidHeader(bytes))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "The file header shall be \"%PDF-1.n\" (0 ≤ n ≤ 7) beginning at byte offset 0.");
            // Without a recognisable header line the marker position is undefined, so stop here.
            return;
        }

        if (!HasBinaryMarker(bytes))
        {
            context.Report(
                "ISO19005-2:6.1.2-binary-marker",
                Clause,
                PreflightSeverity.Error,
                "The header line shall be followed by a comment line containing at least four "
                + "bytes whose values are 128 or greater.");
        }
    }

    private static bool HasValidHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < _prefix.Length + 1)
            return false;
        if (!bytes[.._prefix.Length].SequenceEqual(_prefix))
            return false;
        var digit = bytes[_prefix.Length];
        return digit is >= (byte)'0' and <= (byte)'7';
    }

    private static bool HasBinaryMarker(ReadOnlySpan<byte> bytes)
    {
        var i = 0;

        // Advance past the header line.
        while (i < bytes.Length && bytes[i] is not (byte)'\n' and not (byte)'\r')
            i++;

        // Consume the end-of-line sequence (\r, \n, or \r\n).
        if (i < bytes.Length && bytes[i] == (byte)'\r') i++;
        if (i < bytes.Length && bytes[i] == (byte)'\n') i++;

        // The next line must be a comment.
        if (i >= bytes.Length || bytes[i] != (byte)'%')
            return false;
        i++;

        // Count high bytes (>= 128) up to the next end-of-line.
        var highBytes = 0;
        while (i < bytes.Length && bytes[i] is not (byte)'\n' and not (byte)'\r')
        {
            if (bytes[i] >= 0x80)
                highBytes++;
            i++;
        }

        return highBytes >= 4;
    }
}
