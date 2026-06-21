// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.4 (Cross-reference table). In a file that uses a classic cross-reference table,
/// the <c>xref</c> keyword and the first cross-reference subsection header shall be separated by a
/// single EOL marker (§6.1.4-2).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.4 and ISO 32000-1:2008, 7.5.4. Clean-room: derived from the
/// specification text, not from any third-party validation profile. This is a byte-level check: the
/// cross-reference table is located through the file's final <c>startxref</c> offset (not by scanning
/// for the word "xref", which could occur inside a stream), so the check inspects exactly one place
/// and cannot misfire on stream content. A file whose cross-reference data is a cross-reference
/// <em>stream</em> (the offset points at an indirect object, not the <c>xref</c> keyword) has no
/// <c>xref</c> keyword and is therefore outside this clause.
/// </remarks>
internal sealed class CrossReferenceRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.4-xref-eol";

    public string Clause => "ISO 19005-2:2011, 6.1.4";

    private static readonly byte[] _xref = "xref"u8.ToArray();
    private static readonly byte[] _startxref = "startxref"u8.ToArray();

    public void Evaluate(PreflightContext context)
    {
        var bytes = context.FileBytes.Span;
        var xrefOffset = LastStartxrefTarget(bytes);
        if (xrefOffset < 0 || xrefOffset + _xref.Length > bytes.Length)
            return;

        // Only a classic cross-reference table begins with the literal "xref"; a cross-reference
        // stream begins with "N G obj" and is outside §6.1.4.
        for (var i = 0; i < _xref.Length; i++)
            if (bytes[xrefOffset + i] != _xref[i])
                return;

        // §6.1.4-2: exactly one EOL marker (CR, LF, or CRLF) shall separate "xref" from the first
        // subsection header (which begins with a decimal digit — the first object number).
        var pos = xrefOffset + _xref.Length;
        if (pos < bytes.Length && bytes[pos] == (byte)'\r')
        {
            pos++;
            if (pos < bytes.Length && bytes[pos] == (byte)'\n')
                pos++;
        }
        else if (pos < bytes.Length && bytes[pos] == (byte)'\n')
        {
            pos++;
        }
        else
        {
            Report(context);
            return;
        }

        if (pos >= bytes.Length || bytes[pos] is < (byte)'0' or > (byte)'9')
            Report(context);
    }

    private void Report(PreflightContext context)
        => context.Report(
            RuleId, Clause, PreflightSeverity.Error,
            "The xref keyword and the first cross-reference subsection header shall be separated by a "
            + "single EOL marker (§6.1.4).");

    // The byte offset named by the file's final startxref, or −1 when none is found. startxref always
    // sits just before %%EOF at the end of the file, so the last occurrence is the active one.
    private static int LastStartxrefTarget(ReadOnlySpan<byte> bytes)
    {
        var at = bytes.LastIndexOf(_startxref);
        if (at < 0)
            return -1;
        var p = at + _startxref.Length;
        while (p < bytes.Length && bytes[p] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0)
            p++;
        long value = 0;
        var any = false;
        while (p < bytes.Length && bytes[p] is >= (byte)'0' and <= (byte)'9')
        {
            value = value * 10 + (bytes[p] - (byte)'0');
            p++;
            any = true;
            if (value > bytes.Length)
                return -1;
        }
        return any ? (int)value : -1;
    }
}
