// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Conformance.Rules.Fonts; // PredefinedCMaps
using VellumPdf.Core;
using VellumPdf.Reader; // ParsedStream

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.3.3 (CMap embedding and consistency). Three related checks:
/// <list type="bullet">
///   <item><strong>7.21.3.3-1 (PDCMap)</strong> — a composite font's <c>/Encoding</c> shall either
///   be one of the predefined CMaps from ISO 32000-1 Table 118, be <c>Identity-H</c> /
///   <c>Identity-V</c>, or be an embedded CMap stream. A non-predefined name that is not a stream
///   reference is non-conformant.</item>
///   <item><strong>7.21.3.3-2 (CMapFile)</strong> — for an embedded CMap stream, the integer
///   <c>/WMode</c> entry in the stream dictionary must equal the <c>/WMode N def</c> declaration
///   in the CMap program. Absent <c>/WMode</c> defaults to 0 on both sides.</item>
///   <item><strong>7.21.3.3-3 (PDReferencedCMap)</strong> — if the embedded CMap program contains
///   a <c>usecmap</c> invocation (<c>/SomeName usecmap</c>), the referenced name shall be one of
///   the predefined CMaps from ISO 32000-1 Table 118.</item>
/// </list>
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.21.3.3 and cross-validated against veraPDF 1.30.2 (clause
/// 7.21.3.3 testNumbers 1, 2, 3). The PDF/UA-1 predicates are identical to PDF/A-2
/// §6.2.11.3.3-1 / §6.2.11.3.3-2 / §6.2.11.3.3-3:
/// <list type="bullet">
///   <item>7.21.3.3-1 mirrors the <c>FontStructureRule.CheckCMapEncoding</c> check.</item>
///   <item>7.21.3.3-2 mirrors the <c>CMapContentRule</c> WMode check.</item>
///   <item>7.21.3.3-3 mirrors the <c>CMapContentRule</c> usecmap check.</item>
/// </list>
/// <para>
/// Only fonts actually selected via a <c>Tf</c> operator are evaluated (usage-scoped, matching
/// veraPDF). The primary <c>RuleId</c> is 7.21.3.3-1; 7.21.3.3-2 and 7.21.3.3-3 are reported
/// under their own rule ids from the same evaluation pass.
/// </para>
/// <para>
/// Defensive operation: any CMap parse failure stops the scan and produces no finding; a malformed
/// CMap program never causes a spurious finding.
/// </para>
/// </remarks>
internal sealed class UaCMapRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.3.3-1";

    public string Clause => "ISO 14289-1:2014, 7.21.3.3";

    private const string RuleId1 = "ISO14289-1:7.21.3.3-1";
    private const string RuleId2 = "ISO14289-1:7.21.3.3-2";
    private const string RuleId3 = "ISO14289-1:7.21.3.3-3";

    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _wMode = new("WMode");

    // The predefined CMap names of ISO 32000-1 Table 118 — single shared copy (see PredefinedCMaps).
    private static readonly IReadOnlySet<string> _predefinedCMaps = PredefinedCMaps.Names;

    public void Evaluate(PreflightContext context)
    {
        // Deduplicated by CMap object number: one finding per CMap stream per rule.
        var reported2 = new HashSet<int>();
        var reported3 = new HashSet<int>();

        foreach (var font in context.EnumerateUsedFonts())
        {
            if (context.Resolve(font.Get(PdfName.Subtype)) is not PdfName { Value: "Type0" })
                continue;

            var rawEncoding = font.Get(_encoding);
            var encoding = context.Resolve(rawEncoding);

            // ── §7.21.3.3-1: /Encoding must be a predefined name or an embedded CMap stream. ──────
            if (encoding is PdfName nameVal)
            {
                if (!_predefinedCMaps.Contains(nameVal.Value))
                {
                    context.Report(
                        RuleId1,
                        Clause,
                        PreflightSeverity.Error,
                        $"A composite font's /Encoding names the CMap /{nameVal.Value}, which is neither "
                        + "one of the predefined CMaps nor an embedded CMap stream (§7.21.3.3).");
                }
                // Predefined name (including Identity-H/V): §7.21.3.3-2/-3 have no embedded program
                // to check — they do not apply to predefined-name encodings.
                continue;
            }

            // /Encoding is not a name — must resolve to an embedded CMap stream.
            if (rawEncoding is not PdfIndirectReference cmapRef)
                continue;
            if (context.ResolveStream(cmapRef) is not { } cmapStream)
                continue; // not a stream reference — cannot check -2/-3 without the stream

            // ── §7.21.3.3-2 and §7.21.3.3-3: only check each CMap stream once. ──────────────────
            if (!reported2.Contains(cmapRef.ObjectNumber) || !reported3.Contains(cmapRef.ObjectNumber))
                CheckEmbeddedCMap(context, cmapRef.ObjectNumber, cmapStream, reported2, reported3);
        }
    }

    private void CheckEmbeddedCMap(
        PreflightContext context,
        int cmapObjNum,
        ParsedStream cmapStream,
        HashSet<int> reported2,
        HashSet<int> reported3)
    {
        if (context.DecodeStream(cmapStream) is not { } cmapBytes)
            return; // decode failure — defensive skip

        var parsed = ParseCMapProgram(cmapBytes);
        if (parsed is null)
            return; // malformed — defensive skip

        // ── §7.21.3.3-2: stream-dictionary /WMode must equal the program's /WMode N def. ─────────
        if (reported2.Add(cmapObjNum))
        {
            var dictWMode = (context.Resolve(cmapStream.Dictionary.Get(_wMode)) as PdfInteger)?.Value ?? 0L;
            var progWMode = parsed.ProgramWMode;
            if (progWMode != (int)dictWMode)
            {
                context.Report(
                    RuleId2,
                    Clause,
                    PreflightSeverity.Error,
                    $"WMode entry (value {progWMode}) in the embedded CMap program and in the CMap "
                    + $"dictionary (value {dictWMode}) are not identical (§7.21.3.3).");
            }
        }

        // ── §7.21.3.3-3: usecmap-referenced names must be predefined. ────────────────────────────
        if (reported3.Add(cmapObjNum))
        {
            foreach (var referencedName in parsed.UseCMapNames)
            {
                if (!_predefinedCMaps.Contains(referencedName))
                {
                    context.Report(
                        RuleId3,
                        Clause,
                        PreflightSeverity.Error,
                        $"A CMap references another non-standard CMap /{referencedName} via usecmap "
                        + "(§7.21.3.3).");
                    break; // one finding per CMap stream
                }
            }
        }
    }

    // ── CMap program parser ────────────────────────────────────────────────────────────────────────
    // Extracted from CMapContentRule — parses /WMode N def and /Name usecmap patterns.

    private static ParsedCMapProgram? ParseCMapProgram(byte[] bytes)
    {
        var useCMapNames = new List<string>();

        try
        {
            var mem = new ReadOnlyMemory<byte>(bytes);

            // Pass 1: collect /Name usecmap patterns.
            {
                var lexer = new PdfLexer(mem);
                string? lastNameSeen = null;

                while (!lexer.AtEnd)
                {
                    var token = lexer.NextToken();
                    if (token.Kind == TokenKind.EndOfInput)
                        break;

                    switch (token.Kind)
                    {
                        case TokenKind.Name:
                            lastNameSeen = DecodeName(token.Raw.Span);
                            break;

                        case TokenKind.Keyword:
                            {
                                var kw = Encoding.Latin1.GetString(token.Raw.Span);
                                if (kw == "usecmap" && lastNameSeen is not null)
                                    useCMapNames.Add(lastNameSeen);
                                lastNameSeen = null;
                                break;
                            }

                        default:
                            lastNameSeen = null;
                            break;
                    }
                }
            }

            var programWMode = ExtractProgramWMode(mem);

            return new ParsedCMapProgram(programWMode, useCMapNames);
        }
        catch
        {
            return null; // parse failure — degrade to no-op
        }
    }

    private static int ExtractProgramWMode(ReadOnlyMemory<byte> mem)
    {
        try
        {
            var lexer = new PdfLexer(mem);
            var state = 0;
            var candidate = 0;

            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput)
                    break;

                switch (state)
                {
                    case 0:
                        if (token.Kind == TokenKind.Name && DecodeName(token.Raw.Span) == "WMode")
                            state = 1;
                        break;

                    case 1:
                        if (token.Kind == TokenKind.Integer)
                        {
                            var v = ParseInt(token.Raw.Span);
                            if (v >= 0) { candidate = v; state = 2; }
                            else state = 0;
                        }
                        else if (token.Kind == TokenKind.Name && DecodeName(token.Raw.Span) == "WMode")
                            state = 1;
                        else
                            state = 0;
                        break;

                    case 2:
                        if (token.Kind == TokenKind.Keyword
                            && Encoding.Latin1.GetString(token.Raw.Span) == "def")
                            return candidate;
                        else if (token.Kind == TokenKind.Name && DecodeName(token.Raw.Span) == "WMode")
                            state = 1;
                        else
                            state = 0;
                        break;
                }
            }
        }
        catch { /* malformed CMap program — fall through to the WMode-0 default (FP-safe). */ }

        return 0; // absent /WMode defaults to 0.
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(raw.Length);
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

    private static int ParseInt(ReadOnlySpan<byte> raw)
    {
        if (!int.TryParse(Encoding.Latin1.GetString(raw), out var v))
            return -1;
        return v < 0 ? -1 : v;
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    // ── Data types ─────────────────────────────────────────────────────────────────────────────────

    private sealed class ParsedCMapProgram(int programWMode, IReadOnlyList<string> useCMapNames)
    {
        public int ProgramWMode { get; } = programWMode;
        public IReadOnlyList<string> UseCMapNames { get; } = useCMapNames;
    }
}
