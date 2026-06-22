// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.1.13 test 10. A conforming PDF/A-2 file shall not contain a CID value greater
/// than 65,535. This rule checks composite (Type 0) fonts that use an embedded CMap stream (not
/// Identity-H, Identity-V, or a predefined named CMap) by parsing the CMap's
/// <c>begincidrange</c> and <c>begincidchar</c> sections and looking up the CID produced for each
/// character code used in page content. If any resolved CID exceeds 65,535, a finding is emitted
/// naming the offending CID value.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.13 and cross-validated against veraPDF 1.30.2, which
/// evaluates this rule on the <c>CMapFile</c> object and gates it on the actual CIDs produced
/// from text-show operators (<c>Tj</c>, <c>TJ</c>, <c>'</c>, <c>"</c>) rendered through the
/// Type 0 font — only character codes actually used in content are checked, not the abstract
/// maximum of all declared cidrange entries.
/// Clean-room: derived from the specification text and empirical veraPDF oracle probing, not from
/// any third-party validation profile.
/// <para>
/// <strong>Scope:</strong>
/// <list type="bullet">
///   <item>Identity-H / Identity-V: always conformant — a 2-byte character code can produce at
///   most CID 65535 by definition. These fonts are never checked.</item>
///   <item>Predefined named CMaps (e.g. <c>/UniGB-UCS2-H</c>): the character-collection table for
///   the predefined CMap is not embedded in this library, so the rule is deferred — no finding
///   is generated. This matches the §6.2.11.3.1-1 predefined-CMap deferral.</item>
///   <item>Embedded CMap streams: the CMap program is parsed for its <c>begincodespacerange</c>,
///   <c>begincidrange</c>, and <c>begincidchar</c> sections. Text-show operands are split into
///   character codes using the declared codespace (so codes are decoded the way veraPDF decodes
///   them, not by a fixed-width guess), and the CID resolved for each code is checked against the
///   65,535 limit.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Defensive operation:</strong> on any CMap parse failure or lexer error the scan stops
/// and no finding is emitted; a malformed CMap never causes a spurious finding.
/// </para>
/// </remarks>
internal sealed class CidRangeRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.13-10";

    public string Clause => "ISO 19005-2:2011, 6.1.13";

    private const int MaxCid = 65535;

    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _descendantFonts = new("DescendantFonts");

    // The predefined CMaps listed in ISO 32000-1 Table 118. An /Encoding that names one of these
    // (excluding Identity-H/V, which are handled separately) is deferred — the character
    // collection table is not available in this library.
    // The predefined CMap names of ISO 32000-1 Table 118 — single shared copy (see PredefinedCMaps).
    private static readonly IReadOnlySet<string> _predefinedCMaps = PredefinedCMaps.Names;

    public void Evaluate(PreflightContext context)
    {
        // Keyed by the CMap stream's object number so each CMap is flagged at most once.
        var reported = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fontsDict)
                continue;

            // Collect the embedded CMaps for every Type0 font in this page's resources.
            var embeddedCMaps = new Dictionary<string, EmbeddedCMap>(StringComparer.Ordinal);
            foreach (var entry in fontsDict.Entries)
                if (TryGetEmbeddedCMap(context, entry.Value) is { } cmap)
                    embeddedCMaps[entry.Key.Value] = cmap;

            if (embeddedCMaps.Count == 0)
                continue;

            var content = ContentStreamUsage.GetPageContent(context, page);
            if (content is null)
                continue;

            ScanContent(context, content, embeddedCMaps, reported);
        }
    }

    // Resolves a font dictionary reference to an EmbeddedCMap descriptor when it is a Type0 font
    // with an embedded CMap stream (non-Identity, non-predefined-name /Encoding).
    private static EmbeddedCMap? TryGetEmbeddedCMap(PreflightContext context, PdfObject? fontRef)
    {
        if (context.Resolve(fontRef) is not PdfDictionary font)
            return null;
        if (context.Resolve(font.Get(PdfName.Subtype)) is not PdfName { Value: "Type0" })
            return null;

        var rawEncoding = font.Get(_encoding);
        var encoding = context.Resolve(rawEncoding);

        // Identity-H / Identity-V: structurally bounded at 65535 — never fail.
        if (encoding is PdfName { Value: "Identity-H" or "Identity-V" })
            return null;

        // Any other predefined name: deferred (no character-collection table available).
        if (encoding is PdfName)
            return null;

        // /Encoding must be an indirect reference to a stream.
        if (rawEncoding is not PdfIndirectReference cmapRef)
            return null;
        if (context.ResolveStream(cmapRef) is not { } cmapStream)
            return null;
        if (context.DecodeStream(cmapStream) is not { } cmapBytes)
            return null;

        // Parse the CMap program to extract cidrange and cidchar mappings.
        var parsedCMap = ParseCMap(cmapBytes);
        if (parsedCMap is null)
            return null; // malformed CMap — skip defensively

        return new EmbeddedCMap(cmapRef.ObjectNumber, parsedCMap);
    }

    // Walks the content stream, tracking the current font via Tf, and for each text-show operator
    // resolves the character codes to CIDs using the embedded CMap.
    private void ScanContent(
        PreflightContext context,
        byte[] content,
        Dictionary<string, EmbeddedCMap> embeddedCMaps,
        HashSet<int> reported)
    {
        EmbeddedCMap? current = null;

        try
        {
            var lexer = new PdfLexer(content);
            string? lastName = null;
            var pending = new List<byte[]>();

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

                    case TokenKind.LiteralString:
                    case TokenKind.HexString:
                        pending.Add(DecodeString(token.Raw.Span, token.Kind == TokenKind.HexString));
                        break;

                    case TokenKind.Keyword:
                        var op = Encoding.Latin1.GetString(token.Raw.Span);
                        if (op == "Tf")
                        {
                            current = lastName is not null && embeddedCMaps.TryGetValue(lastName, out var f)
                                ? f
                                : null;
                        }
                        else if (op is "Tj" or "TJ" or "'" or "\"")
                        {
                            CheckCids(context, current, pending, reported);
                        }
                        pending.Clear();
                        lastName = null;
                        break;

                    default:
                        // Numerics and array delimiters are operands; keep pending strings.
                        break;
                }
            }
        }
        catch
        {
            // Malformed content — stop scanning; keep findings collected so far.
        }
    }

    // Checks all CIDs produced from the pending strings against the 65,535 limit.
    private void CheckCids(
        PreflightContext context,
        EmbeddedCMap? current,
        List<byte[]> strings,
        HashSet<int> reported)
    {
        if (current is null)
            return;

        foreach (var bytes in strings)
        {
            // Split the string into the SAME character codes veraPDF decodes, using the CMap's
            // declared codespace — not a fixed-width guess. This is what keeps the rule
            // false-positive-safe: a code is only ever looked up if it is a valid codespace code,
            // so two single-byte codes are never accidentally combined into a spurious wide code
            // that happens to map past 65,535.
            foreach (var code in current.Cmap.DecodeCodes(bytes))
            {
                if (current.Cmap.TryLookupCid(code, out var cid)
                    && cid > MaxCid
                    && reported.Add(current.CmapObjectNumber))
                {
                    Report(context, cid);
                }
            }
        }
    }

    private void Report(PreflightContext context, int cid)
        => context.Report(
            RuleId,
            Clause,
            PreflightSeverity.Error,
            $"A CID value ({cid}) in an embedded CMap exceeds 65,535, which §6.1.13 prohibits.");

    // ── CMap program parser ────────────────────────────────────────────────────────────────────────

    // Parses the CMap PostScript program bytes into a lookup structure. Returns null on malformed
    // input (defensive: no finding on parse failure).
    private static ParsedCMap? ParseCMap(byte[] bytes)
    {
        var cidRanges = new List<CidRange>();
        var cidChars = new List<CidChar>();
        var codespaces = new List<Codespace>();

        try
        {
            var mem = new ReadOnlyMemory<byte>(bytes);
            var lexer = new PdfLexer(mem);

            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput)
                    break;
                if (token.Kind != TokenKind.Keyword)
                    continue;

                var kw = Encoding.Latin1.GetString(token.Raw.Span);

                if (kw == "begincodespacerange")
                {
                    ParseCodespaces(lexer, codespaces);
                }
                else if (kw == "begincidrange")
                {
                    ParseCidRanges(lexer, cidRanges);
                }
                else if (kw == "begincidchar")
                {
                    ParseCidChars(lexer, cidChars);
                }
            }
        }
        catch
        {
            // Parse failure — degrade to no-op rather than a spurious finding.
            return null;
        }

        return new ParsedCMap(cidRanges, cidChars, codespaces);
    }

    // Reads `<lo> <hi>` codespace pairs until `endcodespacerange`. Each pair's hex-string byte
    // length defines a code length, and the per-byte [lo, hi] bounds define which byte sequences
    // are valid codes of that length. This is what lets a CMap-encoded string be split into the
    // SAME character codes veraPDF decodes — rather than a fixed-width guess.
    private static void ParseCodespaces(PdfLexer lexer, List<Codespace> codespaces)
    {
        while (!lexer.AtEnd)
        {
            var tok = lexer.NextToken();
            if (tok.Kind == TokenKind.EndOfInput)
                return;

            if (tok.Kind == TokenKind.Keyword
                && Encoding.Latin1.GetString(tok.Raw.Span) == "endcodespacerange")
                return;

            if (tok.Kind != TokenKind.HexString)
                return; // malformed — stop

            var lo = HexStringToBytes(tok.Raw.Span);

            var t2 = lexer.NextToken();
            if (t2.Kind != TokenKind.HexString)
                return;
            var hi = HexStringToBytes(t2.Raw.Span);

            // A well-formed codespace entry has equal-length, non-empty bounds.
            if (lo.Length > 0 && lo.Length == hi.Length)
                codespaces.Add(new Codespace(lo, hi));
        }
    }

    // Reads `<srcLo> <srcHi> dstCidStart` triples until `endcidrange`.
    private static void ParseCidRanges(PdfLexer lexer, List<CidRange> ranges)
    {
        while (!lexer.AtEnd)
        {
            // Skip to the next non-whitespace token.
            var tok = lexer.NextToken();
            if (tok.Kind == TokenKind.EndOfInput)
                return;

            // Hit `endcidrange` — done.
            if (tok.Kind == TokenKind.Keyword
                && Encoding.Latin1.GetString(tok.Raw.Span) == "endcidrange")
                return;

            // Expect: <srcLo> <srcHi> dstCidStart
            if (tok.Kind != TokenKind.HexString)
                return; // malformed — stop

            var srcLo = HexStringToInt(tok.Raw.Span);

            var t2 = lexer.NextToken();
            if (t2.Kind != TokenKind.HexString)
                return;
            var srcHi = HexStringToInt(t2.Raw.Span);

            var t3 = lexer.NextToken();
            if (t3.Kind != TokenKind.Integer)
                return;
            var dstStart = ParseInt(t3.Raw.Span);

            if (srcLo >= 0 && srcHi >= 0 && dstStart >= 0)
                ranges.Add(new CidRange(srcLo, srcHi, dstStart));
        }
    }

    // Reads `<src> dstCid` pairs until `endcidchar`.
    private static void ParseCidChars(PdfLexer lexer, List<CidChar> chars)
    {
        while (!lexer.AtEnd)
        {
            var tok = lexer.NextToken();
            if (tok.Kind == TokenKind.EndOfInput)
                return;

            if (tok.Kind == TokenKind.Keyword
                && Encoding.Latin1.GetString(tok.Raw.Span) == "endcidchar")
                return;

            if (tok.Kind != TokenKind.HexString)
                return; // malformed — stop

            var src = HexStringToInt(tok.Raw.Span);

            var t2 = lexer.NextToken();
            if (t2.Kind != TokenKind.Integer)
                return;
            var dst = ParseInt(t2.Raw.Span);

            if (src >= 0 && dst >= 0)
                chars.Add(new CidChar(src, dst));
        }
    }

    // Interprets a hex-string token's bytes as a big-endian unsigned integer. Returns -1 on error.
    // The raw token includes the delimiting '<' and '>' characters.
    private static int HexStringToInt(ReadOnlySpan<byte> raw)
    {
        var result = 0;
        for (var i = 1; i < raw.Length && raw[i] != (byte)'>'; i++)
        {
            var v = Hex(raw[i]);
            if (v < 0)
                continue;
            result = (result << 4) | v;
            if (result > 0x1FFFF) // overflow guard (> 17 bits is certainly invalid for a CMap code)
                return -1;
        }
        return result;
    }

    // Decodes a hex-string token's content (between '<' and '>') to its raw bytes. An odd number of
    // hex digits pads the final nibble low, matching ISO 32000-1 §7.3.4.3.
    private static byte[] HexStringToBytes(ReadOnlySpan<byte> raw)
    {
        var bytes = new List<byte>(raw.Length / 2);
        var hi = -1;
        for (var i = 1; i < raw.Length && raw[i] != (byte)'>'; i++)
        {
            var v = Hex(raw[i]);
            if (v < 0)
                continue;
            if (hi < 0)
            {
                hi = v;
            }
            else
            {
                bytes.Add((byte)((hi << 4) | v));
                hi = -1;
            }
        }
        if (hi >= 0)
            bytes.Add((byte)(hi << 4));
        return bytes.ToArray();
    }

    private static int ParseInt(ReadOnlySpan<byte> raw)
    {
        if (!int.TryParse(Encoding.Latin1.GetString(raw), out var v))
            return -1;
        return v < 0 ? -1 : v;
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 1; i < raw.Length; i++) // skip leading '/'
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

    // Decodes a content-stream string token (with its delimiters) to its raw bytes.
    private static byte[] DecodeString(ReadOnlySpan<byte> raw, bool hex)
    {
        var bytes = new List<byte>(raw.Length);
        if (hex)
        {
            var hi = -1;
            for (var i = 1; i < raw.Length && raw[i] != (byte)'>'; i++)
            {
                var v = Hex(raw[i]);
                if (v < 0)
                    continue;
                if (hi < 0)
                    hi = v;
                else
                {
                    bytes.Add((byte)((hi << 4) | v));
                    hi = -1;
                }
            }
            if (hi >= 0)
                bytes.Add((byte)(hi << 4));
            return bytes.ToArray();
        }

        for (var i = 1; i < raw.Length && raw[i] != (byte)')'; i++)
        {
            if (raw[i] == (byte)'\\' && i + 1 < raw.Length)
            {
                i++;
                bytes.Add(raw[i] switch
                {
                    (byte)'n' => (byte)'\n',
                    (byte)'r' => (byte)'\r',
                    (byte)'t' => (byte)'\t',
                    (byte)'b' => (byte)'\b',
                    (byte)'f' => (byte)'\f',
                    _ => raw[i],
                });
            }
            else
            {
                bytes.Add(raw[i]);
            }
        }
        return bytes.ToArray();
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    // ── Data types ─────────────────────────────────────────────────────────────────────────────────

    // Associates a CMap stream object number with its parsed CMap content.
    private sealed class EmbeddedCMap(int cmapObjectNumber, ParsedCMap cmap)
    {
        public int CmapObjectNumber { get; } = cmapObjectNumber;
        public ParsedCMap Cmap { get; } = cmap;
    }

    // A parsed subset of a CMap program: the codespace, cidrange, and cidchar sections.
    private sealed class ParsedCMap(
        IReadOnlyList<CidRange> ranges, IReadOnlyList<CidChar> chars, IReadOnlyList<Codespace> codespaces)
    {
        private readonly IReadOnlyList<CidRange> _ranges = ranges;
        private readonly IReadOnlyList<CidChar> _chars = chars;
        private readonly IReadOnlyList<Codespace> _codespaces = codespaces;

        // Splits a CMap-encoded string into character-code values using the declared codespace
        // ranges (ISO 32000-1 §9.7.6.2). At each position the first codespace entry whose bytes
        // match is consumed; an unmatched lead byte advances by the shortest codespace length
        // WITHOUT yielding a code (so an invalid byte never fabricates a spurious code). When the
        // CMap declares no codespace, nothing is yielded — embedded CMaps always declare one, so
        // this only suppresses checking for a malformed CMap, which is the safe direction.
        public IEnumerable<int> DecodeCodes(byte[] bytes)
        {
            if (_codespaces.Count == 0)
                yield break;

            var minLen = int.MaxValue;
            foreach (var cs in _codespaces)
                if (cs.Length < minLen)
                    minLen = cs.Length;

            var i = 0;
            while (i < bytes.Length)
            {
                var matched = false;
                foreach (var cs in _codespaces)
                {
                    if (i + cs.Length <= bytes.Length && cs.Matches(bytes, i))
                    {
                        var code = 0;
                        for (var k = 0; k < cs.Length; k++)
                            code = (code << 8) | bytes[i + k];
                        yield return code;
                        i += cs.Length;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                    i += minLen; // skip an undecodable lead byte (or run) without yielding a code
            }
        }

        // Returns the CID for charCode if any cidrange or cidchar mapping covers it, or false when
        // no mapping is found. Ranges are checked before individual chars, matching PostScript CMap
        // lookup order.
        public bool TryLookupCid(int charCode, out int cid)
        {
            foreach (var r in _ranges)
            {
                if (charCode >= r.SrcLo && charCode <= r.SrcHi)
                {
                    cid = r.DstStart + (charCode - r.SrcLo);
                    return true;
                }
            }
            foreach (var c in _chars)
            {
                if (c.Src == charCode)
                {
                    cid = c.Dst;
                    return true;
                }
            }
            cid = 0;
            return false;
        }
    }

    // One `begincodespacerange` entry: byte sequences of length Lo.Length whose every byte k lies in
    // [Lo[k], Hi[k]] are valid character codes of that length.
    private sealed class Codespace(byte[] lo, byte[] hi)
    {
        private readonly byte[] _lo = lo;
        private readonly byte[] _hi = hi;

        public int Length => _lo.Length;

        // True when the Length bytes of <paramref name="bytes"/> starting at <paramref name="off"/>
        // are within the per-byte [lo, hi] bounds.
        public bool Matches(byte[] bytes, int off)
        {
            for (var k = 0; k < _lo.Length; k++)
            {
                var b = bytes[off + k];
                if (b < _lo[k] || b > _hi[k])
                    return false;
            }
            return true;
        }
    }

    // One `begincidrange` entry: maps source codes [SrcLo, SrcHi] to [DstStart, DstStart+(SrcHi−SrcLo)].
    private readonly struct CidRange(int srcLo, int srcHi, int dstStart)
    {
        public int SrcLo { get; } = srcLo;
        public int SrcHi { get; } = srcHi;
        public int DstStart { get; } = dstStart;
    }

    // One `begincidchar` entry: maps a single source code Src to destination CID Dst.
    private readonly struct CidChar(int src, int dst)
    {
        public int Src { get; } = src;
        public int Dst { get; } = dst;
    }
}
