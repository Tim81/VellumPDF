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
            var embeddedCMaps = new Dictionary<string, EmbeddedCMapEntry>(StringComparer.Ordinal);
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

    // Resolves a font dictionary reference to an EmbeddedCMapEntry descriptor when it is a Type0 font
    // with an embedded CMap stream (non-Identity, non-predefined-name /Encoding).
    private static EmbeddedCMapEntry? TryGetEmbeddedCMap(PreflightContext context, PdfObject? fontRef)
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
        var parsedCMap = EmbeddedCMapParser.Parse(cmapBytes);
        if (parsedCMap is null)
            return null; // malformed CMap — skip defensively

        return new EmbeddedCMapEntry(cmapRef.ObjectNumber, parsedCMap);
    }

    // Walks the content stream, tracking the current font via Tf, and for each text-show operator
    // resolves the character codes to CIDs using the embedded CMap.
    private void ScanContent(
        PreflightContext context,
        byte[] content,
        Dictionary<string, EmbeddedCMapEntry> embeddedCMaps,
        HashSet<int> reported)
    {
        EmbeddedCMapEntry? current = null;

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
        EmbeddedCMapEntry? current,
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
                var next = raw[i];
                // Line continuation: \CR, \LF, or \CRLF → drop the backslash and EOL.
                if (next == (byte)'\r')
                {
                    if (i + 1 < raw.Length && raw[i + 1] == (byte)'\n')
                        i++; // consume the LF of CRLF
                    continue;
                }
                if (next == (byte)'\n')
                    continue;

                // Octal escape: \ followed by 1–3 octal digits, value mod 256.
                if (next is >= (byte)'0' and <= (byte)'7')
                {
                    var val = next - '0';
                    if (i + 1 < raw.Length && raw[i + 1] is >= (byte)'0' and <= (byte)'7')
                    {
                        i++;
                        val = val * 8 + (raw[i] - '0');
                        if (i + 1 < raw.Length && raw[i + 1] is >= (byte)'0' and <= (byte)'7')
                        {
                            i++;
                            val = val * 8 + (raw[i] - '0');
                        }
                    }
                    bytes.Add((byte)(val & 0xFF));
                    continue;
                }

                bytes.Add(next switch
                {
                    (byte)'n' => (byte)'\n',
                    (byte)'r' => (byte)'\r',
                    (byte)'t' => (byte)'\t',
                    (byte)'b' => (byte)'\b',
                    (byte)'f' => (byte)'\f',
                    _ => next,
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

    // Associates a CMap stream object number with its parsed CMap content.
    private sealed class EmbeddedCMapEntry(int cmapObjectNumber, ParsedCMap cmap)
    {
        public int CmapObjectNumber { get; } = cmapObjectNumber;
        public ParsedCMap Cmap { get; } = cmap;
    }
}
