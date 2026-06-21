// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.7.1 (Streams). No stream in a conforming file may be external: its dictionary
/// shall not contain the <c>/F</c>, <c>/FFilter</c>, or <c>/FDecodeParms</c> keys, which designate
/// stream data stored outside the file.
/// Also implements §6.1.7.2-1: every stream filter shall be one of the eight permitted filter names
/// (ASCIIHexDecode, ASCII85Decode, FlateDecode, RunLengthDecode, CCITTFaxDecode, JBIG2Decode,
/// DCTDecode, JPXDecode); the <c>/Crypt</c> filter is allowed only when its corresponding
/// <c>/DecodeParms</c> dictionary's <c>/Name</c> entry is the name <c>Identity</c>. LZWDecode and
/// any other non-listed filter name are violations.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.7.1 and 6.1.7.2. Clean-room: derived from the specification
/// text, not from any third-party validation profile. Applies to <em>every</em> stream object in
/// the file (walked through the indirect-object number space), not only those reachable from the
/// rendered content — §6.1.7.1 and §6.1.7.2 are file-structure constraints, and veraPDF likewise
/// flags both the external-stream keys and forbidden filter names on any parsed stream (cross-checked).
/// <para>
/// Deferred: the <c>/Length</c>-matches-body (§6.1.7.1-1) and stream-keyword EOL (§6.1.7.1-2)
/// checks need byte-offset parsing; inline-image filters (§6.1.10) are a separate content-stream
/// concern.
/// </para>
/// </remarks>
internal sealed class StreamRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.7.1-external-stream";

    public string Clause => "ISO 19005-2:2011, 6.1.7.1";

    private static readonly PdfName _f = new("F");
    private static readonly PdfName _fFilter = new("FFilter");
    private static readonly PdfName _fDecodeParms = new("FDecodeParms");

    public void Evaluate(PreflightContext context)
    {
        foreach (var stream in context.EnumerateStreams())
        {
            var dict = stream.Dictionary;

            // §6.1.7.1-3: a stream dictionary shall not contain the F, FFilter, or FDecodeParms keys
            // (these designate an external stream whose data lives outside the file).
            if (dict.Get(_f) is not null || dict.Get(_fFilter) is not null || dict.Get(_fDecodeParms) is not null)
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    "A stream dictionary contains an /F, /FFilter, or /FDecodeParms key (an external "
                    + "stream), which is not permitted in PDF/A-2.");

            // §6.1.7.1-1: the /Length value shall equal the number of bytes of the stream body. The
            // reader locates the real body by scanning to 'endstream' when /Length is wrong, so a
            // declared /Length that differs from the actual body length is a stale or incorrect value.
            if (context.Resolve(dict.Get(PdfName.Length)) is PdfInteger declared
                && declared.Value != stream.RawBody.Length)
                context.Report(
                    "ISO19005-2:6.1.7.1-length",
                    "ISO 19005-2:2011, 6.1.7.1",
                    PreflightSeverity.Error,
                    $"A stream's /Length ({declared.Value}) does not match its actual body length "
                    + $"({stream.RawBody.Length}).");

            CheckStreamKeywordEol(context, stream);
            CheckStreamFilters(context, stream);
        }
    }

    // ── §6.1.7.2-1: stream filter allowlist ─────────────────────────────────────────────────────
    // The eight filter names permitted in PDF/A-2 streams (ISO 19005-2:2011, 6.1.7.2).
    // LZWDecode is explicitly excluded. Crypt is handled separately with its /Name/Identity check.
    private static readonly HashSet<string> _allowedFilters = new(StringComparer.Ordinal)
    {
        "ASCIIHexDecode",
        "ASCII85Decode",
        "FlateDecode",
        "RunLengthDecode",
        "CCITTFaxDecode",
        "JBIG2Decode",
        "DCTDecode",
        "JPXDecode",
    };

    private static readonly PdfName _filterName = new("Filter");
    private static readonly PdfName _decodeParms = new("DecodeParms");
    private static readonly PdfName _crypt = new("Crypt");

    // §6.1.7.2-1: every filter name on the stream shall be in the allowlist; the /Crypt filter is
    // permitted only when the corresponding /DecodeParms dictionary carries /Name /Identity.
    // A stream with no /Filter is fine. Reports at most one violation per stream.
    private static void CheckStreamFilters(PreflightContext context, ParsedStream stream)
    {
        var dict = stream.Dictionary;

        // Resolve the /Filter value — may be absent, a single PdfName, or a PdfArray of PdfName.
        var filterValue = context.Resolve(dict.Get(_filterName));
        if (filterValue is null)
            return; // No filter — nothing to check.

        // Resolve the /DecodeParms value (absent, a single dict, or an array parallel to /Filter).
        var decodeParms = context.Resolve(dict.Get(_decodeParms));

        // Normalise /Filter to an enumerable of (filterName, correspondingParms) pairs.
        if (filterValue is PdfName singleName)
        {
            CheckOneFilter(context, singleName, decodeParms);
        }
        else if (filterValue is PdfArray filterArray)
        {
            // /DecodeParms is an array of the same length as /Filter; entries may be null (PdfNull).
            var parmsArray = decodeParms as PdfArray;
            for (var i = 0; i < filterArray.Count; i++)
            {
                if (context.Resolve(filterArray[i]) is not PdfName filterName)
                    continue; // Skip unresolvable entries (malformed — other rules will catch it).
                PdfObject? parms = null;
                if (parmsArray is not null && i < parmsArray.Count)
                    parms = context.Resolve(parmsArray[i]);
                if (CheckOneFilter(context, filterName, parms))
                    return; // Report at most once per stream; stop on first violation.
            }
        }
    }

    // Checks a single filter name against the allowlist. Returns true when a violation was reported.
    private static bool CheckOneFilter(PreflightContext context, PdfName filterName, PdfObject? parms)
    {
        if (_allowedFilters.Contains(filterName.Value))
            return false; // Explicitly allowed — no issue.

        if (filterName.Equals(_crypt))
        {
            // Crypt is permitted only when the corresponding /DecodeParms dict has /Name /Identity.
            if (parms is PdfDictionary parmsDict
                && context.Resolve(parmsDict.Get(PdfName.Name)) is PdfName { Value: "Identity" })
                return false; // Crypt with /Name /Identity — conformant.

            context.Report(
                "ISO19005-2:6.1.7.2-1-filter",
                "ISO 19005-2:2011, 6.1.7.2",
                PreflightSeverity.Error,
                "A stream uses the /Crypt filter without an Identity /Name in its decode parameters, "
                + "which is not permitted in PDF/A-2.");
            return true;
        }

        // Any other filter name (including LZWDecode) is forbidden.
        context.Report(
            "ISO19005-2:6.1.7.2-1-filter",
            "ISO 19005-2:2011, 6.1.7.2",
            PreflightSeverity.Error,
            $"A stream uses the /{filterName.Value} filter, which is not permitted in PDF/A-2.");
        return true;
    }

    private static readonly byte[] _streamLf = "stream\n"u8.ToArray();
    private static readonly byte[] _streamCrLf = "stream\r\n"u8.ToArray();
    private static readonly byte[] _endstream = "endstream"u8.ToArray();

    // §6.1.7.1-2: the 'stream' keyword shall be followed by a CRLF or a single LF, and the 'endstream'
    // keyword shall be preceded by an EOL marker. The body's file offset (ParsedStream.BodyOffset)
    // pins both keywords exactly, so the bytes around them are inspected without scanning — the check
    // cannot misfire on 'stream'/'endstream' byte sequences inside a binary body.
    private static void CheckStreamKeywordEol(PreflightContext context, ParsedStream stream)
    {
        var bytes = context.FileBytes.Span;
        var bodyOffset = stream.BodyOffset;
        if (bodyOffset <= 0 || bodyOffset > bytes.Length)
            return; // no recorded file position (e.g. an object-stream member).

        var streamEolOk =
            EndsWith(bytes, bodyOffset, _streamLf) || EndsWith(bytes, bodyOffset, _streamCrLf);

        var bodyEnd = bodyOffset + stream.RawBody.Length;
        var endstreamEolOk = EndstreamPrecededByEol(bytes, bodyEnd);

        if (!streamEolOk || !endstreamEolOk)
            context.Report(
                "ISO19005-2:6.1.7.1-stream-eol",
                "ISO 19005-2:2011, 6.1.7.1",
                PreflightSeverity.Error,
                "The 'stream' keyword shall be followed by a single EOL (CRLF or LF) and the 'endstream' "
                + "keyword shall be preceded by an EOL marker (§6.1.7.1).");
    }

    // True when the bytes ending at exclusive index 'end' equal 'suffix'.
    private static bool EndsWith(ReadOnlySpan<byte> bytes, int end, byte[] suffix)
        => end >= suffix.Length && bytes.Slice(end - suffix.Length, suffix.Length).SequenceEqual(suffix);

    // True when 'endstream' begins within a few bytes of 'bodyEnd' and is immediately preceded by an
    // EOL marker. Returns true (no false positive) when 'endstream' is not found in the small window.
    private static bool EndstreamPrecededByEol(ReadOnlySpan<byte> bytes, int bodyEnd)
    {
        for (var gap = 0; gap <= 2; gap++)
        {
            var at = bodyEnd + gap;
            if (at >= 0 && at + _endstream.Length <= bytes.Length
                && bytes.Slice(at, _endstream.Length).SequenceEqual(_endstream))
                return at > 0 && bytes[at - 1] is (byte)'\n' or (byte)'\r';
        }
        return true;
    }
}
