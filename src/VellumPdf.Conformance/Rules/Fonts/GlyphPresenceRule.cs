// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.2.11.4.1 (Glyph presence), §6.2.11.5 (Glyph widths), and §6.2.11.8 (.notdef). Every
/// glyph referenced for rendering shall be present in the embedded font program, the width declared
/// for it shall match the program's advance width, and the <c>.notdef</c> glyph (index 0) shall not
/// be referenced. For a composite font using Identity encoding, the bytes shown by a text operator
/// are glyph indices directly, so a glyph index of 0 references <c>.notdef</c>, an index at or beyond
/// the program's glyph count is absent, and the declared CID width (<c>/W</c> or <c>/DW</c>) is
/// compared against the program's advance width.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.11.4.1 and ISO 32000-1:2008, 9.4.3 / 9.7.4. Clean-room:
/// derived from the specification text. The embedded program's glyph count comes from
/// <see cref="SfntMetrics"/> (the maxp table); the glyph indices used are read from the page
/// content streams. Cross-validated against veraPDF (a Type0/Identity-H font shown a glyph index
/// beyond its TrueType program's glyph count fails clause 6.2.11.4.1-2).
/// <para>
/// This slice covers composite (Type 0) fonts with Identity-H/Identity-V encoding and an embedded
/// CIDFontType2 (TrueType) program — the common embedded-subset path. Simple-font glyph presence
/// (which needs encoding + cmap resolution), CFF programs, and glyphs drawn from within form
/// XObjects, patterns, Type 3 procedures, or annotation appearances are deferred.
/// </para>
/// </remarks>
internal sealed class GlyphPresenceRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.11.4.1-glyph-present";

    public string Clause => "ISO 19005-2:2011, 6.2.11.4.1";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile2 = new("FontFile2");

    public void Evaluate(PreflightContext context)
    {
        // Keyed "<program object>:<finding>" so each program is flagged at most once per finding kind.
        var reported = new HashSet<string>();

        foreach (var page in context.EnumeratePages())
        {
            // Map each Identity-encoded Type0/CIDFontType2 font resource to its program glyph count.
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fonts)
                continue;

            var identityFonts = new Dictionary<string, IdentityFont>(StringComparer.Ordinal);
            foreach (var entry in fonts.Entries)
                if (TryGetIdentityFont(context, entry.Value) is { } font)
                    identityFonts[entry.Key.Value] = font;

            if (identityFonts.Count == 0)
                continue;

            var content = ContentStreamUsage.GetPageContent(context, page);
            if (content is null)
                continue;

            ScanContent(context, content, identityFonts, reported);
        }
    }

    private sealed class IdentityFont(int programObject, SfntMetrics metrics, CidWidths widths)
    {
        public int ProgramObject { get; } = programObject;
        public SfntMetrics Metrics { get; } = metrics;
        public CidWidths Widths { get; } = widths;
    }

    // Returns the metrics + declared CID widths for a Type0 font with Identity-H/V encoding and an
    // embedded CIDFontType2 descendant, or null when the font is not of that form.
    private static IdentityFont? TryGetIdentityFont(PreflightContext context, PdfObject? fontRef)
    {
        if (context.Resolve(fontRef) is not PdfDictionary font)
            return null;
        if (context.Resolve(font.Get(PdfName.Subtype)) is not PdfName { Value: "Type0" })
            return null;
        if (context.Resolve(font.Get(_encoding)) is not PdfName { Value: "Identity-H" or "Identity-V" })
            return null;
        if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants || descendants.Count == 0)
            return null;
        if (context.Resolve(descendants[0]) is not PdfDictionary cidFont
            || context.Resolve(cidFont.Get(PdfName.Subtype)) is not PdfName { Value: "CIDFontType2" })
            return null;
        if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;
        if (descriptor.Get(_fontFile2) is not PdfIndirectReference fontFileRef
            || context.ResolveStream(fontFileRef) is not { } program
            || context.DecodeStream(program) is not { } programBytes)
            return null;
        if (SfntMetrics.TryParse(programBytes) is not { } metrics)
            return null;
        return new IdentityFont(fontFileRef.ObjectNumber, metrics, CidWidths.Parse(context, cidFont));
    }

    // Walks the content stream, tracking the current font, and for an Identity-encoded font treats
    // the bytes of each shown string as 2-byte big-endian glyph indices.
    private void ScanContent(
        PreflightContext context, byte[] content, Dictionary<string, IdentityFont> identityFonts, HashSet<string> reported)
    {
        IdentityFont? current = null;
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
                        // The font name is the first operand of `name size Tf`, so it is not the token
                        // immediately before Tf; keep it until the next operator consumes it.
                        lastName = DecodeName(token.Raw.Span);
                        break;
                    case TokenKind.LiteralString:
                    case TokenKind.HexString:
                        pending.Add(DecodeString(token.Raw.Span, token.Kind == TokenKind.HexString));
                        break;
                    case TokenKind.Keyword:
                        var op = Encoding.Latin1.GetString(token.Raw.Span);
                        if (op == "Tf")
                            current = lastName is not null && identityFonts.TryGetValue(lastName, out var f) ? f : null;
                        else if (op is "Tj" or "TJ" or "'" or "\"")
                            ConsumeGlyphs(context, current, pending, reported);
                        pending.Clear();
                        lastName = null; // operands are consumed by the operator
                        break;
                    default:
                        // Numbers and array delimiters are operands — keep the pending name and strings.
                        break;
                }
            }
        }
        catch
        {
            // Malformed content — stop scanning this page; rules degrade rather than abort.
        }
    }

    private void ConsumeGlyphs(
        PreflightContext context, IdentityFont? current, List<byte[]> strings, HashSet<string> reported)
    {
        if (current is not { } font)
            return;
        foreach (var bytes in strings)
        {
            for (var i = 0; i + 1 < bytes.Length; i += 2)
            {
                var gid = (bytes[i] << 8) | bytes[i + 1];

                // §6.2.11.8: a conforming file shall not reference the .notdef glyph (index 0).
                if (gid == 0 && reported.Add($"{font.ProgramObject}:notdef"))
                    context.Report("ISO19005-2:6.2.11.8-notdef", "ISO 19005-2:2011, 6.2.11.8",
                        PreflightSeverity.Error,
                        "The document references the .notdef glyph (glyph index 0) of a composite font, "
                        + "which is not permitted in PDF/A-2.");

                // §6.2.11.4.1: every glyph referenced shall be present in the embedded program.
                else if (gid >= font.Metrics.NumGlyphs && reported.Add($"{font.ProgramObject}:present"))
                    context.Report(RuleId, Clause, PreflightSeverity.Error,
                        $"A glyph (index {gid}) drawn with a composite font is not present in the embedded "
                        + $"font program, which defines {font.Metrics.NumGlyphs} glyphs.");

                // §6.2.11.5: the declared width shall match the embedded program's advance width.
                else if (font.Metrics.AdvanceWidth1000(gid) is { } programWidth
                    && Math.Abs(font.Widths.GetWidth(gid) - programWidth) > 1
                    && reported.Add($"{font.ProgramObject}:width"))
                    context.Report("ISO19005-2:6.2.11.5-glyph-width", "ISO 19005-2:2011, 6.2.11.5",
                        PreflightSeverity.Error,
                        $"The width declared for glyph {gid} ({font.Widths.GetWidth(gid)}) does not match the "
                        + $"embedded font program's advance width ({programWidth}).");
            }
        }
    }

    // The per-CID widths declared by a CIDFont's /W array (and /DW default), looked up without
    // materialising large ranges (ISO 32000-1 §9.7.4.3).
    private sealed class CidWidths
    {
        private readonly Dictionary<int, int> _singles = new();
        private readonly List<(int First, int Last, int Width)> _ranges = [];
        private readonly int _default;

        private CidWidths(int defaultWidth) => _default = defaultWidth;

        public int GetWidth(int cid)
        {
            if (_singles.TryGetValue(cid, out var w))
                return w;
            foreach (var (first, last, width) in _ranges)
                if (cid >= first && cid <= last)
                    return width;
            return _default;
        }

        public static CidWidths Parse(PreflightContext context, PdfDictionary cidFont)
        {
            var dw = context.Resolve(cidFont.Get(new PdfName("DW"))) is PdfInteger d ? (int)d.Value : 1000;
            var widths = new CidWidths(dw);
            if (context.Resolve(cidFont.Get(new PdfName("W"))) is not PdfArray w)
                return widths;

            var i = 0;
            while (i < w.Count)
            {
                if (context.Resolve(w[i]) is not PdfInteger c)
                    break;
                i++;
                if (i < w.Count && context.Resolve(w[i]) is PdfArray run)
                {
                    for (var j = 0; j < run.Count; j++)
                        if (AsInt(context.Resolve(run[j])) is { } value)
                            widths._singles[(int)c.Value + j] = value;
                    i++;
                }
                else if (i + 1 < w.Count
                    && AsInt(context.Resolve(w[i])) is { } last
                    && AsInt(context.Resolve(w[i + 1])) is { } rangeWidth)
                {
                    widths._ranges.Add(((int)c.Value, last, rangeWidth));
                    i += 2;
                }
                else
                {
                    break;
                }
            }
            return widths;
        }

        private static int? AsInt(PdfObject? obj) => obj switch
        {
            PdfInteger n => (int)n.Value,
            PdfReal r => (int)Math.Round(r.Value),
            _ => null,
        };
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
                bytes.Add((byte)(hi << 4)); // odd final digit is padded with a trailing zero
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
}
