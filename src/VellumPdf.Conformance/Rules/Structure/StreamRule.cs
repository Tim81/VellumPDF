// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.7.1 (Streams). No stream in a conforming file may be external: its dictionary
/// shall not contain the <c>/F</c>, <c>/FFilter</c>, or <c>/FDecodeParms</c> keys, which designate
/// stream data stored outside the file.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.7.1. Clean-room: derived from the specification text, not
/// from any third-party validation profile. Applies to <em>every</em> stream object in the file
/// (walked through the indirect-object number space), not only those reachable from the rendered
/// content — §6.1.7.1 is a file-structure constraint, and veraPDF likewise flags the external-stream
/// keys on any parsed stream (cross-checked).
/// <para>
/// Deferred: the LZWDecode-filter prohibition (§6.1.7.2) — veraPDF only flags it on streams it
/// actually instantiates a filter for (reachable/decoded streams), so a faithful check needs
/// used-stream scoping rather than this all-streams walk. The <c>/Length</c>-matches-body
/// (§6.1.7.1-1) and stream-keyword EOL (§6.1.7.1-2) checks need byte-offset parsing; inline-image
/// filters (§6.1.10) are a separate content-stream concern.
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
        }
    }
}
