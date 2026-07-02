// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Colour;

/// <summary>
/// ISO 19005-2 §6.2.4.2-2 — Overprint mode (OPM) and ICCBased CMYK colour spaces.
/// </summary>
/// <remarks>
/// <para>
/// The rule: when an ICCBased colour space with N=4 (CMYK) is the current fill colour space
/// and fill overprinting is enabled (<c>/op true</c>), OPM shall not be 1; equivalently for
/// stroke. A violation occurs when <c>overprintFlag == true &amp;&amp; OPM != 0</c> at a
/// painting operator.
/// </para>
/// <para>
/// <b>Graphics-state model:</b> This rule implements a minimal per-content-stream
/// graphics-state interpreter with a full q/Q save-restore stack. The following state
/// components are tracked:
/// <list type="bullet">
///   <item><b>OPM</b> — default 0. Set from a <c>/OPM</c> entry in an applied
///   <c>/ExtGState</c> dictionary. Range: 0 or 1 (ISO 32000-1 Table 58).</item>
///   <item><b>strokeOverprint (OP)</b> — default false. Set from <c>/OP</c> in
///   <c>/ExtGState</c>.</item>
///   <item><b>fillOverprint (op)</b> — default false. Set from <c>/op</c> in
///   <c>/ExtGState</c>. When <c>/op</c> is absent, the <c>/OP</c> value also sets fill
///   overprint (ISO 32000-1 §8.4.5). When <c>/op</c> is explicitly present it overrides.</item>
///   <item><b>fillIsIccCmyk</b> — false. Becomes true when a <c>cs</c> operator selects a
///   colour-space resource that resolves to <c>[/ICCBased stream]</c> with stream <c>/N = 4</c>.
///   Reset to false by <c>g</c>, <c>rg</c>, <c>k</c> (device colour operators for fill).</item>
///   <item><b>strokeIsIccCmyk</b> — false. Same semantics for stroke, set by <c>CS</c>,
///   reset by <c>G</c>, <c>RG</c>, <c>K</c>.</item>
/// </list>
/// The full state (all five components) is pushed/popped by <c>q</c>/<c>Q</c>.
/// </para>
/// <para>
/// <b>Violation condition (checked at every painting operator):</b>
/// <code>
/// fillViolation  = fillIsIccCmyk  &amp;&amp; fillOverprint  &amp;&amp; OPM != 0
/// strokeViolation = strokeIsIccCmyk &amp;&amp; strokeOverprint &amp;&amp; OPM != 0
/// </code>
/// Painting operators that are fill-only check fillViolation; stroke-only check strokeViolation;
/// fill-and-stroke operators check both.
/// </para>
/// <para>
/// <b>FP-safety:</b> A finding is emitted only when every condition is positively established
/// from parsed PDF objects. Unresolvable ExtGState entries, undecipherable colour-space
/// entries, and malformed content streams are all treated as "not establishing the violation"
/// (safe skip). The defaults (OPM=0, overprint=false) mean that the common case — any document
/// that does not explicitly enable overprinting — never fires.
/// </para>
/// <para>
/// <b>Scope:</b> Page content streams are scanned with a full graphics-state interpreter that
/// threads the current overprint state (OP/op/OPM, fill/stroke ICCBased-CMYK flags) into every
/// drawn Form XObject via <c>Do</c>. The form inherits the caller's state as its initial GState;
/// the form's own <c>cs</c>/<c>gs</c> operators override that state locally. On return, the
/// caller's state is unchanged (a Form XObject executes as if wrapped in an implicit
/// <c>q</c>/<c>Q</c>). Recursion is bounded to <see cref="MaxFormDepth"/> levels and cycle-
/// guarded by object number. Type 3 CharProcs and annotation /AP /N appearance streams are
/// scanned in ISOLATION with a fresh default GState against their own /Resources (inherited
/// state from the page does not reach these streams; under-detection is FP-safe). Streams with
/// null /Resources are skipped for CharProcs/appearances; for Forms they are still entered with
/// the inherited state (the form's own operators that need resource lookup receive empty tables,
/// which is FP-safe).
/// </para>
/// <para>
/// <b>Oracle probes (veraPDF 1.30.2):</b>
/// <list type="bullet">
///   <item>P1 fill + ICCBased CMYK + op true + OPM 1 → FAIL §6.2.4.2-2</item>
///   <item>P2 fill + ICCBased CMYK + op true + OPM 0 → PASS</item>
///   <item>P3 fill + ICCBased CMYK + op false + OPM 1 → PASS</item>
///   <item>P4 fill + ICCBased CMYK + no gs (defaults) → PASS</item>
///   <item>P5 stroke + ICCBased CMYK + OP true + OPM 1 → FAIL §6.2.4.2-2</item>
///   <item>P6 fill + ICCBased RGB (N=3) + op true + OPM 1 → PASS (only CMYK triggers)</item>
///   <item>P7 fill + DeviceCMYK (k operator) + op true + OPM 1 → PASS for §6.2.4.2-2</item>
///   <item>P8 fill + ICCBased CMYK + /OP only (no /op) + OPM 1 → FAIL (/OP propagates to fill)</item>
///   <item>P9 gs inside q, paint after Q → PASS (state restored)</item>
///   <item>P10 gs before q, paint after Q → FAIL (state from before q persists)</item>
///   <item>P14 /OP true + /op explicitly false + fill → PASS (explicit /op wins)</item>
///   <item>N3-A Form XObject self-contains cs+gs (op/OPM violation) + fill → FAIL §6.2.4.2-2
///   (veraPDF validated 2026-06-23; context: xObject[0]/contentStream[0])</item>
///   <item>N3-B Page sets cs+gs (op/OPM), form only fills (inherited state) → FAIL §6.2.4.2-2
///   (veraPDF validated 2026-06-23; detected via state threading through Do)</item>
///   <item>N3-C Form self-contains cs + op true + OPM 0 + fill → PASS (OPM 0 allowed)</item>
/// </list>
/// </para>
/// <para>
/// Authored from ISO 19005-2:2011, §6.2.4.2 and ISO 32000-1:2008, §8.4.5 (Table 58).
/// Clean-room: derived from the specification text and empirical veraPDF oracle probing,
/// not from any third-party validation profile.
/// </para>
/// </remarks>
internal sealed class OverprintRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.4.2-2";

    public string Clause => "ISO 19005-2:2011, 6.2.4.2";

    private const int MaxFormDepth = 32;

    private static readonly PdfName _colorSpace = new("ColorSpace");
    private static readonly PdfName _extGState = new("ExtGState");
    private static readonly PdfName _op = new("op");
    private static readonly PdfName _OP = new("OP");
    private static readonly PdfName _opm = new("OPM");
    private static readonly PdfName _n = new("N");
    private static readonly PdfName _xObject = new("XObject");
    private static readonly PdfName _subtype = new("Subtype");
    private static readonly PdfName _resources = new("Resources");
    private static readonly PdfName _formSubtype = new("Form");

    public void Evaluate(PreflightContext context)
    {
        foreach (var page in context.EnumeratePages())
        {
            try
            {
                EvaluatePage(context, page);
            }
            catch
            {
                // Malformed page or content — skip rather than propagate.
            }

            try
            {
                EvaluateNonPageStreams(context, page);
            }
            catch
            {
                // Collector or scan failure — skip non-page streams for this page.
            }
        }
    }

    private void EvaluatePage(PreflightContext context, PdfDictionary page)
    {
        if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary pageResources)
            return;

        var content = ContentStreamUsage.GetPageContent(context, page);
        if (content is null || content.Length == 0)
            return;

        var iccCmykNames = BuildIccCmykSet(context, pageResources);
        var extGStates = BuildExtGStateTable(context, pageResources);
        var reported = false;
        var visitedForms = new HashSet<int>();
        InterpretStream(
            context, content, iccCmykNames, extGStates, ref reported,
            new GState(), pageResources, visitedForms, 0);
    }

    /// <summary>
    /// Scans non-page content streams reachable from <paramref name="page"/> via
    /// <see cref="ContentStreamUsage.GetReachableContentStreams"/>. Form XObjects are
    /// skipped here — they are reached with inherited graphics state from the page-level
    /// scan in <see cref="EvaluatePage"/>. Type 3 CharProcs and annotation /AP /N
    /// appearance streams are each scanned in ISOLATION with a fresh default GState resolved
    /// against the stream's OWN <c>/Resources</c> dictionary. Streams with a null
    /// <c>Resources</c> property are skipped (cannot resolve names; under-detection, FP-safe).
    /// </summary>
    private void EvaluateNonPageStreams(PreflightContext context, PdfDictionary page)
    {
        IReadOnlyList<ReachableContentStream> streams;
        try
        {
            streams = ContentStreamUsage.GetReachableContentStreams(context, page);
        }
        catch
        {
            return;
        }

        var reported = false;
        foreach (var cs in streams)
        {
            if (reported)
                break;

            // Form XObjects are handled (with inherited state threading) in EvaluatePage.
            if (cs.Kind == ContentStreamKind.FormXObject)
                continue;

            if (cs.Resources is null)
                continue;

            try
            {
                var iccCmykNames = BuildIccCmykSet(context, cs.Resources);
                var extGStates = BuildExtGStateTable(context, cs.Resources);
                // Pass null xObjectResources: CharProcs and annotation appearances are
                // scanned in isolation without Do-recursion into nested Form XObjects.
                InterpretStream(
                    context, cs.Bytes, iccCmykNames, extGStates, ref reported,
                    new GState(), null, null, 0);
            }
            catch
            {
                // Malformed stream or resources — skip this stream; continue to the next.
            }
        }
    }

    /// <summary>
    /// Runs the graphics-state machine over <paramref name="content"/>. Sets
    /// <paramref name="reported"/> to true and emits a finding on the first violation.
    /// Pass a non-null <paramref name="xObjectResources"/> to enable Form XObject recursion
    /// (state threading through Do); pass null for isolated scan mode.
    /// </summary>
    private void InterpretStream(
        PreflightContext context,
        byte[] content,
        HashSet<string> iccCmykNames,
        Dictionary<string, GsParams> extGStates,
        ref bool reported,
        GState initialState,
        PdfDictionary? xObjectResources,
        HashSet<int>? visitedForms,
        int depth)
    {
        var state = initialState;
        var stack = new Stack<GState>();

        try
        {
            var lexer = new PdfLexer(content);
            string? lastNameVal = null;

            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput)
                    break;

                if (token.Kind == TokenKind.Name)
                {
                    lastNameVal = DecodeName(token.Raw.Span);
                    continue;
                }

                if (token.Kind == TokenKind.Keyword)
                {
                    var op = Encoding.Latin1.GetString(token.Raw.Span);

                    switch (op)
                    {
                        case "q":
                            stack.Push(state);
                            break;

                        case "Q":
                            if (stack.Count > 0)
                                state = stack.Pop();
                            break;

                        case "gs":
                            if (lastNameVal is not null
                                && extGStates.TryGetValue(lastNameVal, out var gsParams))
                            {
                                state.Opm = gsParams.Opm ?? state.Opm;

                                if (gsParams.StrokeOp.HasValue)
                                    state.StrokeOverprint = gsParams.StrokeOp.Value;

                                if (gsParams.FillOp.HasValue)
                                {
                                    state.FillOverprint = gsParams.FillOp.Value;
                                }
                                else if (gsParams.StrokeOp.HasValue)
                                {
                                    // /op absent, /OP present → /OP also sets fill overprint.
                                    state.FillOverprint = gsParams.StrokeOp.Value;
                                }
                            }
                            break;

                        case "cs":
                            if (lastNameVal is not null)
                                state.FillIsIccCmyk = iccCmykNames.Contains(lastNameVal);
                            break;

                        case "CS":
                            if (lastNameVal is not null)
                                state.StrokeIsIccCmyk = iccCmykNames.Contains(lastNameVal);
                            break;

                        case "g":
                        case "rg":
                        case "k":
                            state.FillIsIccCmyk = false;
                            break;

                        case "G":
                        case "RG":
                        case "K":
                            state.StrokeIsIccCmyk = false;
                            break;

                        case "Do":
                            if (!reported
                                && lastNameVal is not null
                                && xObjectResources is not null
                                && visitedForms is not null
                                && depth < MaxFormDepth)
                            {
                                TryRecurseForm(
                                    context, lastNameVal, xObjectResources, state,
                                    visitedForms, depth, ref reported);
                            }
                            break;

                        case "f":
                        case "F":
                        case "f*":
                            if (!reported && CheckFill(state))
                            {
                                ReportFinding(context, state.Opm);
                                reported = true;
                            }
                            break;

                        case "S":
                        case "s":
                            if (!reported && CheckStroke(state))
                            {
                                ReportFinding(context, state.Opm);
                                reported = true;
                            }
                            break;

                        case "B":
                        case "B*":
                        case "b":
                        case "b*":
                            if (!reported && (CheckFill(state) || CheckStroke(state)))
                            {
                                ReportFinding(context, state.Opm);
                                reported = true;
                            }
                            break;

                        case "ID":
                            ContentStreamUsage.SkipInlineImageData(lexer, content);
                            break;
                    }

                    lastNameVal = null;
                    continue;
                }
            }
        }
        catch
        {
            // Malformed content — keep whatever findings were already reported.
        }
    }

    /// <summary>
    /// Resolves the Form XObject named <paramref name="xObjName"/> from
    /// <paramref name="callerResources"/>, then recursively interprets its content stream
    /// with <paramref name="inheritedState"/> as the initial graphics state.
    /// </summary>
    private void TryRecurseForm(
        PreflightContext context,
        string xObjName,
        PdfDictionary callerResources,
        GState inheritedState,
        HashSet<int> visitedForms,
        int depth,
        ref bool reported)
    {
        try
        {
            if (context.Resolve(callerResources.Get(_xObject)) is not PdfDictionary xObjects)
                return;

            var xObjRef = xObjects.Get(new PdfName(xObjName));
            if (xObjRef is not PdfIndirectReference r)
                return;

            // Cycle guard: if this object is already on the call stack, skip.
            if (!visitedForms.Add(r.ObjectNumber))
                return;

            try
            {
                if (context.Resolve(xObjRef) is not PdfDictionary xObjDict)
                    return;
                if (context.Resolve(xObjDict.Get(_subtype)) is not PdfName subtypeName
                    || subtypeName.Value != _formSubtype.Value)
                    return;

                var stream = context.ResolveStream(xObjRef);
                if (stream is null)
                    return;

                var bytes = context.DecodeStream(stream);
                if (bytes is not { Length: > 0 })
                    return;

                // The form's own /Resources resolve its local cs/gs names.
                // If the form has no /Resources, build empty tables — the inherited
                // colour-space and overprint state still applies to painting operators.
                var formResourceDict = context.Resolve(xObjDict.Get(_resources)) as PdfDictionary;
                var formIccCmykNames = formResourceDict is not null
                    ? BuildIccCmykSet(context, formResourceDict)
                    : [];
                var formExtGStates = formResourceDict is not null
                    ? BuildExtGStateTable(context, formResourceDict)
                    : new Dictionary<string, GsParams>(StringComparer.Ordinal);

                // The form's own /XObject dict (for nested Do operators).
                var formXObjectResources = formResourceDict;

                InterpretStream(
                    context, bytes, formIccCmykNames, formExtGStates, ref reported,
                    inheritedState, formXObjectResources, visitedForms, depth + 1);
            }
            finally
            {
                // Remove from cycle guard when unwinding: the same form can be drawn multiple
                // times by different call sites and should be checked each time (only loops
                // are guarded, not re-entrant draws from disjoint sites).
                visitedForms.Remove(r.ObjectNumber);
            }
        }
        catch
        {
            // Unresolvable XObject or decode failure — FP-safe skip.
        }
    }

    private static bool CheckFill(GState s)
        => s.FillIsIccCmyk && s.FillOverprint && s.Opm != 0;

    private static bool CheckStroke(GState s)
        => s.StrokeIsIccCmyk && s.StrokeOverprint && s.Opm != 0;

    private void ReportFinding(PreflightContext context, long opm)
        => context.Report(
            RuleId,
            Clause,
            PreflightSeverity.Error,
            $"Overprint mode (OPM) is set to {opm} instead of 0 when an ICCBased CMYK colour space is used with enabled overprinting.");

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the set of colour-space resource names (e.g. "CS0") that resolve to an
    /// ICCBased array whose embedded stream has <c>/N 4</c>. Entries that cannot be resolved
    /// are simply absent from the set (FP-safe: they won't trigger the ICCBased-CMYK condition).
    /// </summary>
    private static HashSet<string> BuildIccCmykSet(PreflightContext context, PdfDictionary resources)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (context.Resolve(resources.Get(_colorSpace)) is not PdfDictionary colorSpaces)
            return result;

        foreach (var entry in colorSpaces.Entries)
        {
            if (context.Resolve(entry.Value) is not PdfArray csArray || csArray.Count < 2)
                continue;
            if (context.Resolve(csArray[0]) is not PdfName { Value: "ICCBased" })
                continue;
            var stream = context.ResolveStream(csArray[1]);
            if (stream is null)
                continue;
            if (context.Resolve(stream.Dictionary.Get(_n)) is PdfInteger { Value: 4 })
                result.Add(entry.Key.Value);
        }
        return result;
    }

    /// <summary>
    /// Builds a table of ExtGState resource names → parsed overprint parameters.
    /// Entries with unresolvable dictionaries are omitted. Missing keys produce null
    /// (meaning "not set in this gs call").
    /// </summary>
    private static Dictionary<string, GsParams> BuildExtGStateTable(
        PreflightContext context, PdfDictionary resources)
    {
        var result = new Dictionary<string, GsParams>(StringComparer.Ordinal);
        if (context.Resolve(resources.Get(_extGState)) is not PdfDictionary extGStates)
            return result;

        foreach (var entry in extGStates.Entries)
        {
            if (context.Resolve(entry.Value) is not PdfDictionary gs)
                continue;

            var opm = (context.Resolve(gs.Get(_opm)) as PdfInteger)?.Value;

            bool? strokeOp = null;
            if (context.Resolve(gs.Get(_OP)) is PdfBoolean opVal)
                strokeOp = opVal.Value;

            bool? fillOp = null;
            if (context.Resolve(gs.Get(_op)) is PdfBoolean opLower)
                fillOp = opLower.Value;

            result[entry.Key.Value] = new GsParams(opm, strokeOp, fillOp);
        }
        return result;
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'#' && i + 2 < raw.Length && Hex(raw[i + 1]) >= 0 && Hex(raw[i + 2]) >= 0)
            {
                sb.Append((char)((Hex(raw[i + 1]) << 4) | Hex(raw[i + 2])));
                i += 2;
            }
            else
            {
                sb.Append((char)raw[i]);
            }
        }
        return sb.ToString();
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    // ── State structs (value types for fast stack push/pop) ───────────────

    /// <summary>A snapshot of the graphics-state components tracked by this rule.</summary>
    private struct GState
    {
        /// <summary>OPM value (0 or 1). Default 0 per ISO 32000-1 Table 58.</summary>
        public long Opm;

        /// <summary>Stroke overprint flag. Default false.</summary>
        public bool StrokeOverprint;

        /// <summary>Fill overprint flag. Default false.</summary>
        public bool FillOverprint;

        /// <summary>True when the current fill colour space is ICCBased with N=4.</summary>
        public bool FillIsIccCmyk;

        /// <summary>True when the current stroke colour space is ICCBased with N=4.</summary>
        public bool StrokeIsIccCmyk;
    }

    /// <summary>Cached parameters extracted from a resolved <c>/ExtGState</c> dictionary.</summary>
    private readonly record struct GsParams(long? Opm, bool? StrokeOp, bool? FillOp);
}
