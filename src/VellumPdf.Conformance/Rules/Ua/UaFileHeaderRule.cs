// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §6.1 (File header). A PDF/UA-1 file shall begin, at byte offset 0, with a header
/// line whose entire content is exactly <c>%PDF-1.n</c> where <c>n</c> is a single digit 0–7,
/// followed immediately by an end-of-line sequence (CR, LF, or CR+LF).
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 6.1 (CosDocument predicate: <c>/^%PDF-1\.[0-7]$/.test(header)</c>)
/// and ISO 32000-1:2008, 7.5.2. Clean-room: derived from the specification text and empirically
/// validated against veraPDF 1.30.2 (test id 6.1-1), not from any third-party validation profile.
///
/// Intentional differences from the PDF/A-2 <c>FileHeaderRule</c>:
/// <list type="bullet">
///   <item>The trailing-chars check enforces the <c>$</c> anchoring — any character between the
///         version digit and the EOL (e.g. a trailing space) is a violation.</item>
///   <item>No binary-marker check is included — ISO 14289-1 does not require one (§6.1 contains
///         only the <c>6.1-1</c> test; the binary-marker predicate is absent from the UA-1 profile).</item>
/// </list>
/// </remarks>
internal sealed class UaFileHeaderRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:6.1-1";

    public string Clause => "ISO 14289-1:2014, 6.1";

    // "%PDF-1." — the fixed prefix; the byte that immediately follows must be a digit 0–7,
    // and the byte after that must be CR, LF, or the file must end there (highly unlikely).
    private static readonly byte[] _prefix = "%PDF-1."u8.ToArray();

    public void Evaluate(PreflightContext context)
    {
        if (!HasValidHeader(context.FileBytes.Span))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "The file header line shall be exactly \"%PDF-1.n\" (0 ≤ n ≤ 7) with no "
                + "trailing characters before the end-of-line.");
        }
    }

    private static bool HasValidHeader(ReadOnlySpan<byte> bytes)
    {
        // The file must start with "%PDF-1." followed by a digit 0–7.
        if (bytes.Length < _prefix.Length + 1)
            return false;
        if (!bytes[.._prefix.Length].SequenceEqual(_prefix))
            return false;

        var digit = bytes[_prefix.Length];
        if (digit is < (byte)'0' or > (byte)'7')
            return false;

        // The character immediately after the version digit must be CR or LF (the line ends there).
        // Anything else — a space, another digit, a letter — is a trailing character that violates the "$".
        var afterDigit = _prefix.Length + 1;
        if (afterDigit >= bytes.Length)
            return false; // File ends after the digit with no EOL — malformed but we flag it.

        var next = bytes[afterDigit];
        return next is (byte)'\r' or (byte)'\n';
    }
}
