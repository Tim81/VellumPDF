// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.3 (File trailer). The file trailer dictionary shall contain a <c>/ID</c>
/// entry whose value is an array of exactly two byte strings, and no data shall follow the last
/// end-of-file marker (<c>%%EOF</c>) except an optional single end-of-line marker.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.3 and ISO 32000-1:2008, 7.5.5 / 14.4. Clean-room: derived
/// from the specification text, not from any third-party validation profile.
/// <para>
/// §6.1.3 also forbids the <c>/Encrypt</c> key. That case is enforced earlier, at the reader
/// layer (<see cref="Reader.PdfDocumentReader"/> rejects encrypted documents), so an encrypted
/// file never reaches rule evaluation; it is therefore not duplicated here.
/// </para>
/// </remarks>
internal sealed class FileTrailerRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.3-id-present";

    public string Clause => "ISO 19005-2:2011, 6.1.3";

    public void Evaluate(PreflightContext context)
    {
        var id = context.Resolve(context.Trailer.Get(PdfName.ID));

        if (id is not PdfArray array
            || array.Count != 2
            || !IsByteString(context.Resolve(array[0]))
            || !IsByteString(context.Resolve(array[1])))
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "The file trailer dictionary shall contain a /ID entry that is an array of two byte strings.");
        }

        CheckTrailingData(context);
    }

    private static readonly byte[] _eofMarker = "%%EOF"u8.ToArray();

    // §6.1.3: no data shall follow the last %%EOF except at most one EOL marker (CR, LF, or CRLF).
    private void CheckTrailingData(PreflightContext context)
    {
        var bytes = context.FileBytes.Span;
        var eof = LastIndexOf(bytes, _eofMarker);
        if (eof < 0)
            return; // a missing %%EOF is a different (structural) concern, not handled here.

        var trailing = bytes.Length - (eof + _eofMarker.Length);
        // Tolerate at most a single EOL: "\r", "\n", or "\r\n".
        var allowed = trailing == 0
            || (trailing == 1 && IsEol(bytes[^1]))
            || (trailing == 2 && bytes[^2] == (byte)'\r' && bytes[^1] == (byte)'\n');
        if (!allowed)
            context.Report(
                "ISO19005-2:6.1.3-trailing-data",
                Clause,
                PreflightSeverity.Error,
                $"{trailing} bytes follow the final %%EOF marker; PDF/A-2 permits at most a single "
                + "end-of-line marker after it.");
    }

    private static bool IsEol(byte b) => b is (byte)'\r' or (byte)'\n';

    private static int LastIndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    private static bool IsByteString(PdfObject? value) => value is PdfLiteralString or PdfHexString;
}
