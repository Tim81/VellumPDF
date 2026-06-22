// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.2.11.3.3 tests 2 and 3: embedded CMap WMode consistency and usecmap
/// predefined-name constraint. Both rules are evaluated by parsing the embedded CMap program once.
/// </summary>
/// <remarks>
/// <para>
/// <strong>§6.2.11.3.3-2 (WMode consistency):</strong>
/// For an embedded CMap stream, the integer <c>/WMode</c> entry in the CMap stream dictionary shall
/// be identical to the <c>/WMode</c> value declared in the CMap program (<c>/WMode N def</c>).
/// Absent <c>/WMode</c> defaults to 0 on both sides. A mismatch is reported naming both values.
/// </para>
/// <para>
/// <strong>§6.2.11.3.3-3 (usecmap predefined-name):</strong>
/// If the embedded CMap program contains a <c>usecmap</c> invocation
/// (<c>/SomeName usecmap</c>), the referenced name shall be one of the predefined CMaps from
/// ISO 32000-1:2008, 9.7.5.2, Table 118. Any non-predefined referenced name is reported.
/// </para>
/// <para>
/// <strong>Scope:</strong> only composite (Type 0) fonts with an embedded CMap stream (not
/// Identity-H/V or a predefined named CMap) that are actually selected via a <c>Tf</c> operator
/// in page content are evaluated. This matches veraPDF, which evaluates these rules on the
/// <c>CMapFile</c> / <c>PDReferencedCMap</c> objects reachable from rendered text operators.
/// Unused fonts present in <c>/Resources</c> are not checked. Predefined named CMaps (a
/// name-object <c>/Encoding</c>) have no embedded program, so neither rule applies to them —
/// these are legitimately <em>Implemented</em> (not Partial) because the check is not deferred;
/// predefined CMaps simply have no embedded program to check.
/// </para>
/// <para>
/// <strong>Defensive operation:</strong> any CMap parse failure or lexer exception stops the scan
/// and produces no finding; a malformed CMap program never causes a spurious finding.
/// </para>
/// <para>
/// Clean-room: derived from ISO 19005-2:2011 §6.2.11.3.3 and cross-validated against
/// veraPDF 1.30.2 via oracle probes (STEP-0):
/// <list type="bullet">
///   <item>WMode dict=1/prog=0 and dict=0/prog=1 → FAIL on <c>CMapFile</c> object (confirmed).</item>
///   <item>WMode both=1, both absent (=0), dict=0/prog=absent (=0) → PASS (confirmed).</item>
///   <item>WMode mismatch on an unused font → PASS (confirms scoping to used fonts).</item>
///   <item>§6.2.11.3.3-3 verified via the veraPDF profile XML (object <c>PDReferencedCMap</c>,
///   test checks <c>CMapName</c> against Table 118 list); usecmap oracle fixtures are impractical
///   (veraPDF requires a fully resolved <c>PDReferencedCMap</c> structure; in-process tests
///   cover the rule directly).</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class CMapContentRule : IConformanceRule
{
    // This rule covers two test numbers; the primary RuleId is the first one.
    public string RuleId => "ISO19005-2:6.2.11.3.3-2";

    public string Clause => "ISO 19005-2:2011, 6.2.11.3.3";

    private const string RuleId2 = "ISO19005-2:6.2.11.3.3-2";
    private const string RuleId3 = "ISO19005-2:6.2.11.3.3-3";
    private const string ClauseRef = "ISO 19005-2:2011, 6.2.11.3.3";

    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _wMode = new("WMode");

    // ISO 32000-1 Table 118: predefined CMap names a composite font's /Encoding may reference.
    // This set must stay in sync with FontStructureRule._predefinedCMaps and
    // CidRangeRule._predefinedCMaps.
    private static readonly HashSet<string> _predefinedCMaps = new(StringComparer.Ordinal)
    {
        "Identity-H", "Identity-V",
        "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V",
        "GBKp-EUC-H", "GBKp-EUC-V", "GBK2K-H", "GBK2K-V", "UniGB-UCS2-H", "UniGB-UCS2-V",
        "UniGB-UTF16-H", "UniGB-UTF16-V",
        "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V", "ETen-B5-H", "ETen-B5-V",
        "ETenms-B5-H", "ETenms-B5-V", "CNS-EUC-H", "CNS-EUC-V", "UniCNS-UCS2-H", "UniCNS-UCS2-V",
        "UniCNS-UTF16-H", "UniCNS-UTF16-V",
        "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H",
        "Add-RKSJ-H", "Add-RKSJ-V", "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V",
        "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V",
        "UniJIS-UTF16-H", "UniJIS-UTF16-V",
        "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V",
        "KSCpc-EUC-H", "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V",
    };

    public void Evaluate(PreflightContext context)
    {
        // Keyed by CMap stream object number so each CMap is reported at most once per rule.
        var reported2 = new HashSet<int>();
        var reported3 = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fontsDict)
                continue;

            // Collect embedded CMaps for Type0 fonts in this page's resources.
            var embeddedCMaps = new Dictionary<string, EmbeddedCMapInfo>(StringComparer.Ordinal);
            foreach (var entry in fontsDict.Entries)
            {
                if (TryGetEmbeddedCMap(context, entry.Value) is { } info)
                    embeddedCMaps[entry.Key.Value] = info;
            }

            if (embeddedCMaps.Count == 0)
                continue;

            // Walk the content stream to find fonts actually selected via Tf.
            var content = ContentStreamUsage.GetPageContent(context, page);
            if (content is null)
                continue;

            ScanContent(context, content, embeddedCMaps, reported2, reported3);
        }
    }

    // Resolves a font reference to an EmbeddedCMapInfo when it is a Type0 font with an embedded
    // CMap stream (non-Identity, non-predefined-name /Encoding).
    private static EmbeddedCMapInfo? TryGetEmbeddedCMap(PreflightContext context, PdfObject? fontRef)
    {
        if (context.Resolve(fontRef) is not PdfDictionary font)
            return null;
        if (context.Resolve(font.Get(PdfName.Subtype)) is not PdfName { Value: "Type0" })
            return null;

        var rawEncoding = font.Get(_encoding);
        var encoding = context.Resolve(rawEncoding);

        // Identity-H / Identity-V: no embedded program; skip.
        if (encoding is PdfName { Value: "Identity-H" or "Identity-V" })
            return null;

        // Any other predefined name (not a stream reference): skip.
        if (encoding is PdfName)
            return null;

        // /Encoding must be an indirect reference to a stream.
        if (rawEncoding is not PdfIndirectReference cmapRef)
            return null;
        if (context.ResolveStream(cmapRef) is not { } cmapStream)
            return null;

        // Read /WMode from the stream dictionary; absent defaults to 0.
        var dictWMode = (context.Resolve(cmapStream.Dictionary.Get(_wMode)) as PdfInteger)?.Value ?? 0L;

        if (context.DecodeStream(cmapStream) is not { } cmapBytes)
            return null;

        // Parse the CMap program for /WMode N def and /Name usecmap.
        var parsed = ParseCMapProgram(cmapBytes);
        if (parsed is null)
            return null; // malformed — defensive skip

        return new EmbeddedCMapInfo(cmapRef.ObjectNumber, (int)dictWMode, parsed);
    }

    // Walks the content stream, tracking the font resource name via the Tf operator, and evaluates
    // each embedded CMap's WMode and usecmap constraints on first selection.
    private static void ScanContent(
        PreflightContext context,
        byte[] content,
        Dictionary<string, EmbeddedCMapInfo> embeddedCMaps,
        HashSet<int> reported2,
        HashSet<int> reported3)
    {
        try
        {
            var lexer = new PdfLexer(content);
            string? lastName = null;

            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput)
                    break;

                switch (token.Kind)
                {
                    case TokenKind.Name:
                        lastName = DecodeName(token.Raw.Span);
                        break;

                    case TokenKind.Keyword:
                        {
                            var op = Encoding.Latin1.GetString(token.Raw.Span);
                            if (op == "Tf" && lastName is not null
                                && embeddedCMaps.TryGetValue(lastName, out var info))
                            {
                                CheckCMap(context, info, reported2, reported3);
                            }

                            lastName = null;
                            break;
                        }

                    default:
                        // Numerics and other operands do not affect font-name tracking.
                        break;
                }
            }
        }
        catch
        {
            // Malformed content stream — stop scanning; keep any findings already emitted.
        }
    }

    // Runs both clause checks for a single embedded CMap the first time it is selected via Tf.
    private static void CheckCMap(
        PreflightContext context,
        EmbeddedCMapInfo info,
        HashSet<int> reported2,
        HashSet<int> reported3)
    {
        // §6.2.11.3.3-2: program WMode must equal the stream-dictionary WMode.
        if (reported2.Add(info.CmapObjectNumber))
        {
            var progWMode = info.Parsed.ProgramWMode;
            if (progWMode != info.DictWMode)
            {
                context.Report(
                    RuleId2,
                    ClauseRef,
                    PreflightSeverity.Error,
                    $"WMode entry (value {progWMode}) in the embedded CMap and in the CMap "
                    + $"dictionary (value {info.DictWMode}) are not identical.");
            }
        }

        // §6.2.11.3.3-3: every usecmap-referenced name must be in the Table 118 predefined set.
        if (reported3.Add(info.CmapObjectNumber))
        {
            foreach (var referencedName in info.Parsed.UseCMapNames)
            {
                if (!_predefinedCMaps.Contains(referencedName))
                {
                    context.Report(
                        RuleId3,
                        ClauseRef,
                        PreflightSeverity.Error,
                        $"A CMap references another non-standard CMap {referencedName}.");
                    break; // one finding per CMap stream (the error message names the offender)
                }
            }
        }
    }

    // ── CMap program parser ────────────────────────────────────────────────────────────────────────

    // Parses the CMap PostScript program bytes for:
    //   - /WMode N def  — the program's declared WMode (absent → 0)
    //   - /Name usecmap — each name immediately preceding a `usecmap` keyword
    //
    // The lexer emits PostScript names as TokenKind.Name, integers as TokenKind.Integer, and
    // keywords (operators) as TokenKind.Keyword. `true`/`false`/`null` also arrive as Keyword
    // (PDF lexer convention); the parser ignores them.
    //
    // Returns null on any parse exception — defensive, never a spurious finding.
    private static ParsedCMapProgram? ParseCMapProgram(byte[] bytes)
    {
        var useCMapNames = new List<string>();

        try
        {
            var mem = new ReadOnlyMemory<byte>(bytes);

            // Pass 1: collect /Name usecmap patterns (single scan).
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

            // Pass 2: extract /WMode N def using a three-state machine.
            var programWMode = ExtractProgramWMode(mem);

            return new ParsedCMapProgram(programWMode, useCMapNames);
        }
        catch
        {
            return null; // Parse failure — degrade to no-op.
        }
    }

    // Scans the CMap program for the token sequence /WMode <integer> def and returns the declared
    // WMode value, or 0 (the default per PDF spec) when the declaration is absent.
    private static int ExtractProgramWMode(ReadOnlyMemory<byte> mem)
    {
        try
        {
            var lexer = new PdfLexer(mem);

            // State: 0=idle, 1=seen /WMode, 2=seen /WMode <integer>
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
                            if (v >= 0)
                            {
                                candidate = v;
                                state = 2;
                            }
                            else
                            {
                                state = 0; // malformed integer — reset
                            }
                        }
                        else if (token.Kind == TokenKind.Name && DecodeName(token.Raw.Span) == "WMode")
                        {
                            // Another /WMode token (e.g. inside a comment or dictionary) — restart.
                            state = 1;
                        }
                        else
                        {
                            state = 0;
                        }
                        break;

                    case 2:
                        if (token.Kind == TokenKind.Keyword
                            && Encoding.Latin1.GetString(token.Raw.Span) == "def")
                        {
                            return candidate; // confirmed /WMode N def
                        }
                        else if (token.Kind == TokenKind.Name && DecodeName(token.Raw.Span) == "WMode")
                        {
                            state = 1; // restart from a new /WMode
                        }
                        else
                        {
                            state = 0;
                        }
                        break;
                }
            }
        }
        catch
        {
            // Malformed program — return default.
        }

        return 0; // absent /WMode defaults to 0.
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        // raw includes the leading '/' which we skip.
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

    // The relevant extracted content from a CMap program: its declared WMode and any usecmap names.
    private sealed class ParsedCMapProgram(int programWMode, IReadOnlyList<string> useCMapNames)
    {
        // The /WMode N def value from the program; 0 when the declaration is absent.
        public int ProgramWMode { get; } = programWMode;

        // Names referenced via /Name usecmap in the program (in order of appearance).
        public IReadOnlyList<string> UseCMapNames { get; } = useCMapNames;
    }

    // Associates a CMap stream object number with its dictionary /WMode and parsed program.
    private sealed class EmbeddedCMapInfo(int cmapObjectNumber, int dictWMode, ParsedCMapProgram parsed)
    {
        public int CmapObjectNumber { get; } = cmapObjectNumber;

        // /WMode from the stream dictionary; 0 when absent.
        public int DictWMode { get; } = dictWMode;

        public ParsedCMapProgram Parsed { get; } = parsed;
    }
}
