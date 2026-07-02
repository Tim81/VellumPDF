// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Fonts.Sfnt;
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
/// Wave 2a coverage (implemented paths):
/// <list type="bullet">
///   <item>Type 0 / Identity-H/V / CIDFontType2 / Identity CIDToGIDMap (original path).</item>
///   <item>Type 0 / Identity-H/V / CIDFontType2 / stream CIDToGIDMap (4a).</item>
///   <item>Type 0 / embedded CMap stream / CIDFontType2 / Identity or stream CIDToGIDMap (4b).</item>
///   <item>Simple non-symbolic TrueType / WinAnsi or MacRoman encoding (4c).</item>
/// </list>
/// CFF programs, CIDFontType0, and Type 1 simple fonts are deferred to Wave 2b.
/// Glyphs drawn from within form XObjects, patterns, Type 3 procedures, or annotation appearances
/// are not yet detected here.
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
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");
    private static readonly PdfName _flags = new("Flags");
    private static readonly PdfName _firstChar = new("FirstChar");
    private static readonly PdfName _lastChar = new("LastChar");
    private static readonly PdfName _widths = new("Widths");
    private static readonly PdfName _baseEncoding = new("BaseEncoding");
    private static readonly PdfName _differences = new("Differences");

    // ISO 32000-1 Table 121, bit 3 (Symbolic).
    private const int SymbolicFlag = 1 << 2;

    public void Evaluate(PreflightContext context)
    {
        // Keyed "<program object>:<finding>" so each program is flagged at most once per finding kind.
        var reported = new HashSet<string>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fonts)
                continue;

            var fontMap = new Dictionary<string, CheckedFont>(StringComparer.Ordinal);
            foreach (var entry in fonts.Entries)
                if (TryGetCheckedFont(context, entry.Value) is { } font)
                    fontMap[entry.Key.Value] = font;

            if (fontMap.Count == 0)
                continue;

            var content = ContentStreamUsage.GetPageContent(context, page);
            if (content is null)
                continue;

            ScanContent(context, content, fontMap, reported);
        }
    }

    // ── Font resolution ───────────────────────────────────────────────────────────────────────────

    private static CheckedFont? TryGetCheckedFont(PreflightContext context, PdfObject? fontRef)
    {
        if (context.Resolve(fontRef) is not PdfDictionary font)
            return null;

        return context.Resolve(font.Get(PdfName.Subtype)) switch
        {
            PdfName { Value: "Type0" } => TryGetType0Font(context, font),
            PdfName { Value: "TrueType" } => TryGetSimpleTrueTypeFont(context, font),
            _ => null,
        };
    }

    // ── Type 0 composite fonts ────────────────────────────────────────────────────────────────────

    private static CheckedFont? TryGetType0Font(PreflightContext context, PdfDictionary font)
    {
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

        var cidToGidMap = BuildCidToGidMap(context, cidFont, metrics.NumGlyphs);
        var cidWidths = CidWidths.Parse(context, cidFont);

        var rawEncoding = font.Get(_encoding);
        var encoding = context.Resolve(rawEncoding);

        // Path (original + 4a): Identity-H/V with Identity or stream CIDToGIDMap.
        if (encoding is PdfName { Value: "Identity-H" or "Identity-V" })
            return new IdentityHFont(fontFileRef.ObjectNumber, metrics, cidWidths, cidToGidMap);

        // Path 4b: embedded CMap stream (non-Identity, non-predefined).
        if (encoding is PdfName)
            return null; // predefined named CMap — deferred (no character-collection table)
        if (rawEncoding is not PdfIndirectReference cmapRef)
            return null;
        if (context.ResolveStream(cmapRef) is not { } cmapStream)
            return null;
        if (context.DecodeStream(cmapStream) is not { } cmapBytes)
            return null;
        var parsedCMap = EmbeddedCMapParser.Parse(cmapBytes);
        if (parsedCMap is null)
            return null;

        return new EmbeddedCMapFont(fontFileRef.ObjectNumber, metrics, cidWidths, parsedCMap, cidToGidMap);
    }

    // Returns a GID lookup array for the CIDToGIDMap entry, or null for Identity mapping.
    // For a stream, reads 2-byte-BE pairs: gid = map[cid*2]<<8 | map[cid*2+1].
    private static byte[]? BuildCidToGidMap(PreflightContext context, PdfDictionary cidFont, int numGlyphs)
    {
        var raw = cidFont.Get(_cidToGidMap);
        if (context.Resolve(raw) is PdfName { Value: "Identity" })
            return null; // Identity: GID == CID

        if (raw is not PdfIndirectReference mapRef)
            return null;
        if (context.ResolveStream(mapRef) is not { } mapStream)
            return null;
        return context.DecodeStream(mapStream); // raw bytes; GID at cid*2
    }

    // ── Simple non-symbolic TrueType ─────────────────────────────────────────────────────────────

    private static CheckedFont? TryGetSimpleTrueTypeFont(PreflightContext context, PdfDictionary font)
    {
        // Skip symbolic fonts (Symbolic flag set, bit 3) — FP-safe: only check non-symbolic.
        if (context.Resolve(font.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;
        if (context.Resolve(descriptor.Get(_flags)) is not PdfInteger flagsVal)
            return null;
        if (((int)flagsVal.Value & SymbolicFlag) != 0)
            return null; // symbolic — skip

        // Resolve the font program.
        if (descriptor.Get(_fontFile2) is not PdfIndirectReference fontFileRef
            || context.ResolveStream(fontFileRef) is not { } program
            || context.DecodeStream(program) is not { } programBytes)
            return null;
        if (SfntMetrics.TryParse(programBytes) is not { } metrics)
            return null;

        // Parse the Kernel cmap table for codepoint→GID resolution.
        CmapTable? cmapTable;
        try
        {
            var sfnt = SfntFont.Parse(new ReadOnlyMemory<byte>(programBytes));
            cmapTable = CmapTable.Parse(sfnt);
        }
        catch
        {
            return null; // malformed cmap — skip defensively
        }

        // Resolve the encoding: base (WinAnsi/MacRoman) + /Differences overlay.
        var rawEncoding = font.Get(_encoding);
        if (rawEncoding is null)
            return null; // no encoding — §6.2.11.6-2 violation (caught by FontStructureRule), skip here
        var resolvedEncoding = context.Resolve(rawEncoding);

        string? baseEncName;
        PdfArray? differences = null;

        if (resolvedEncoding is PdfName n)
        {
            baseEncName = n.Value;
        }
        else if (resolvedEncoding is PdfDictionary encDict)
        {
            baseEncName = context.Resolve(encDict.Get(_baseEncoding)) is PdfName bn ? bn.Value : null;
            if (context.Resolve(encDict.Get(_differences)) is PdfArray diff)
                differences = diff;
        }
        else
        {
            return null;
        }

        // Only WinAnsi and MacRoman are valid for non-symbolic TrueType (§6.2.11.6-2).
        var baseTable = baseEncName switch
        {
            "WinAnsiEncoding" => WinAnsiGlyphNames,
            "MacRomanEncoding" => MacRomanGlyphNames,
            _ => null,
        };
        if (baseTable is null)
            return null; // non-standard encoding — FontStructureRule flags §6.2.11.6-2, skip here

        // Build the charcode→glyphname array (256 slots), applying /Differences overlay.
        var glyphNames = (string?[])baseTable.Clone();
        if (differences is not null)
        {
            var code = 0;
            for (var di = 0; di < differences.Count; di++)
            {
                var resolved = context.Resolve(differences[di]);
                if (resolved is PdfInteger idx)
                    code = (int)idx.Value;
                else if (resolved is PdfName gn)
                {
                    if (code >= 0 && code < 256)
                        glyphNames[code] = gn.Value;
                    code++;
                }
            }
        }

        // Resolve the /Widths array (FirstChar..LastChar).
        var firstChar = context.Resolve(font.Get(_firstChar)) is PdfInteger fc ? (int)fc.Value : -1;
        var lastChar = context.Resolve(font.Get(_lastChar)) is PdfInteger lc ? (int)lc.Value : -1;
        if (firstChar < 0 || lastChar < firstChar || lastChar > 255)
            return null; // malformed — FontStructureRule handles it

        int[]? widthArray = null;
        if (context.Resolve(font.Get(_widths)) is PdfArray wa)
        {
            widthArray = new int[lastChar - firstChar + 1];
            for (var i = 0; i < widthArray.Length && i < wa.Count; i++)
                widthArray[i] = context.Resolve(wa[i]) switch
                {
                    PdfInteger wi => (int)wi.Value,
                    PdfReal wr => (int)Math.Round(wr.Value),
                    _ => 0,
                };
        }

        return new SimpleTrueTypeFont(
            fontFileRef.ObjectNumber, metrics, cmapTable, glyphNames, firstChar, lastChar, widthArray);
    }

    // ── Content stream scanner ────────────────────────────────────────────────────────────────────

    private void ScanContent(
        PreflightContext context, byte[] content, Dictionary<string, CheckedFont> fontMap, HashSet<string> reported)
    {
        CheckedFont? current = null;
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
                            current = lastName is not null && fontMap.TryGetValue(lastName, out var f) ? f : null;
                        else if (op is "Tj" or "TJ" or "'" or "\"")
                            ConsumeGlyphs(context, current, pending, reported);
                        pending.Clear();
                        lastName = null;
                        break;
                    default:
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
        PreflightContext context, CheckedFont? current, List<byte[]> strings, HashSet<string> reported)
    {
        if (current is null)
            return;
        current.CheckStrings(context, strings, reported, this);
    }

    // ── Shared glyph-level checks ─────────────────────────────────────────────────────────────────

    internal void CheckGlyph(
        PreflightContext context, int gid, int declaredWidth,
        int programObject, SfntMetrics metrics, HashSet<string> reported)
    {
        if (gid == 0 && reported.Add($"{programObject}:notdef"))
        {
            context.Report("ISO19005-2:6.2.11.8-notdef", "ISO 19005-2:2011, 6.2.11.8",
                PreflightSeverity.Error,
                "The document references the .notdef glyph (glyph index 0) of a font, "
                + "which is not permitted in PDF/A-2.");
            return;
        }

        if (gid >= metrics.NumGlyphs && reported.Add($"{programObject}:present"))
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                $"A glyph (index {gid}) drawn with a font is not present in the embedded "
                + $"font program, which defines {metrics.NumGlyphs} glyphs.");
            return;
        }

        if (metrics.AdvanceWidth1000(gid) is { } programWidth
            && Math.Abs(declaredWidth - programWidth) > 1
            && reported.Add($"{programObject}:width"))
        {
            context.Report("ISO19005-2:6.2.11.5-glyph-width", "ISO 19005-2:2011, 6.2.11.5",
                PreflightSeverity.Error,
                $"The width declared for glyph {gid} ({declaredWidth}) does not match the "
                + $"embedded font program's advance width ({programWidth}).");
        }
    }

    // ── Font wrappers ─────────────────────────────────────────────────────────────────────────────

    private abstract class CheckedFont(int programObject, SfntMetrics metrics)
    {
        public int ProgramObject { get; } = programObject;
        public SfntMetrics Metrics { get; } = metrics;

        public abstract void CheckStrings(
            PreflightContext context, List<byte[]> strings, HashSet<string> reported, GlyphPresenceRule rule);
    }

    // Original + 4a path: Type0 / Identity-H/V. Bytes are 2-byte-BE GIDs.
    // When cidToGidMap is non-null it is the stream CIDToGIDMap; otherwise CID==GID (Identity).
    private sealed class IdentityHFont(
        int programObject, SfntMetrics metrics, CidWidths widths, byte[]? cidToGidMap)
        : CheckedFont(programObject, metrics)
    {
        private readonly CidWidths _widths = widths;
        private readonly byte[]? _cidToGidMap = cidToGidMap;

        public override void CheckStrings(
            PreflightContext context, List<byte[]> strings, HashSet<string> reported, GlyphPresenceRule rule)
        {
            foreach (var bytes in strings)
            {
                for (var i = 0; i + 1 < bytes.Length; i += 2)
                {
                    var cid = (bytes[i] << 8) | bytes[i + 1];
                    var gid = MapCidToGid(cid);
                    var declaredWidth = _widths.GetWidth(cid);
                    rule.CheckGlyph(context, gid, declaredWidth, ProgramObject, Metrics, reported);
                }
            }
        }

        private int MapCidToGid(int cid)
        {
            if (_cidToGidMap is null)
                return cid; // Identity
            var idx = cid * 2;
            if (idx < 0 || idx + 1 >= _cidToGidMap.Length)
                return 0; // out of map range → .notdef
            return (_cidToGidMap[idx] << 8) | _cidToGidMap[idx + 1];
        }
    }

    // Path 4b: Type0 / embedded CMap stream. Byte→CID via ParsedCMap; CID→GID via Identity or stream.
    private sealed class EmbeddedCMapFont(
        int programObject, SfntMetrics metrics, CidWidths widths,
        ParsedCMap cmap, byte[]? cidToGidMap)
        : CheckedFont(programObject, metrics)
    {
        private readonly CidWidths _widths = widths;
        private readonly ParsedCMap _cmap = cmap;
        private readonly byte[]? _cidToGidMap = cidToGidMap;

        public override void CheckStrings(
            PreflightContext context, List<byte[]> strings, HashSet<string> reported, GlyphPresenceRule rule)
        {
            foreach (var bytes in strings)
            {
                foreach (var code in _cmap.DecodeCodes(bytes))
                {
                    if (!_cmap.TryLookupCid(code, out var cid))
                        continue;
                    var gid = MapCidToGid(cid);
                    var declaredWidth = _widths.GetWidth(cid);
                    rule.CheckGlyph(context, gid, declaredWidth, ProgramObject, Metrics, reported);
                }
            }
        }

        private int MapCidToGid(int cid)
        {
            if (_cidToGidMap is null)
                return cid;
            var idx = cid * 2;
            if (idx < 0 || idx + 1 >= _cidToGidMap.Length)
                return 0;
            return (_cidToGidMap[idx] << 8) | _cidToGidMap[idx + 1];
        }
    }

    // Path 4c: simple non-symbolic TrueType. Charcode (1 byte) → glyph name → codepoint via AGL →
    // GID via cmap table → width via hmtx (SfntMetrics.AdvanceWidth1000).
    private sealed class SimpleTrueTypeFont(
        int programObject, SfntMetrics metrics, CmapTable cmapTable,
        string?[] glyphNames, int firstChar, int lastChar, int[]? widthArray)
        : CheckedFont(programObject, metrics)
    {
        private readonly CmapTable _cmapTable = cmapTable;
        private readonly string?[] _glyphNames = glyphNames;
        private readonly int _firstChar = firstChar;
        private readonly int _lastChar = lastChar;
        private readonly int[]? _widthArray = widthArray;

        public override void CheckStrings(
            PreflightContext context, List<byte[]> strings, HashSet<string> reported, GlyphPresenceRule rule)
        {
            foreach (var bytes in strings)
            {
                foreach (var b in bytes)
                {
                    var charCode = (int)b;
                    var glyphName = charCode < _glyphNames.Length ? _glyphNames[charCode] : null;
                    if (glyphName is null || glyphName == ".notdef")
                    {
                        // .notdef or unmapped — §6.2.11.8 fires only when GID 0 is referenced.
                        // For simple fonts the spec check applies differently; skip to avoid FP.
                        continue;
                    }

                    if (!AdobeGlyphList.TryGetCodepoint(glyphName, out var codePoint))
                        continue; // cannot resolve — skip defensively

                    if (!_cmapTable.TryGetGlyphId(codePoint, out var gid))
                        continue; // glyph not in cmap — cannot determine GID, skip

                    // Width from /Widths array (1-indexed by charcode offset from FirstChar).
                    var declaredWidth = 0;
                    if (_widthArray is not null && charCode >= _firstChar && charCode <= _lastChar)
                        declaredWidth = _widthArray[charCode - _firstChar];

                    // Presence check: GID must be < NumGlyphs.
                    if (gid >= Metrics.NumGlyphs && reported.Add($"{ProgramObject}:present"))
                    {
                        context.Report("ISO19005-2:6.2.11.4.1-glyph-present", "ISO 19005-2:2011, 6.2.11.4.1",
                            PreflightSeverity.Error,
                            $"Glyph '{glyphName}' (GID {gid}) drawn with a simple TrueType font is not present in the "
                            + $"embedded font program, which defines {Metrics.NumGlyphs} glyphs.");
                        continue;
                    }

                    // Width check.
                    if (Metrics.AdvanceWidth1000(gid) is { } programWidth
                        && declaredWidth != 0
                        && Math.Abs(declaredWidth - programWidth) > 1
                        && reported.Add($"{ProgramObject}:width"))
                    {
                        context.Report("ISO19005-2:6.2.11.5-glyph-width", "ISO 19005-2:2011, 6.2.11.5",
                            PreflightSeverity.Error,
                            $"The width declared for glyph '{glyphName}' ({declaredWidth}) does not match the "
                            + $"embedded font program's advance width ({programWidth}).");
                    }
                }
            }
        }
    }

    // ── CID widths (from CIDFont /W + /DW) ────────────────────────────────────────────────────────

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

    // ── Content stream helpers ────────────────────────────────────────────────────────────────────

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

    // ── Standard encoding tables ──────────────────────────────────────────────────────────────────
    // ISO 32000-1 Annex D: WinAnsiEncoding and MacRomanEncoding glyph name arrays.
    // Indices 0–255; null means the slot is not defined by the encoding.

    private static readonly string?[] WinAnsiGlyphNames = BuildWinAnsi();

    private static string?[] BuildWinAnsi()
    {
        var t = new string?[256];
        // 0x20–0x7E: printable ASCII (matches standard glyph names)
        t[0x20] = "space"; t[0x21] = "exclam"; t[0x22] = "quotedbl"; t[0x23] = "numbersign";
        t[0x24] = "dollar"; t[0x25] = "percent"; t[0x26] = "ampersand"; t[0x27] = "quotesingle";
        t[0x28] = "parenleft"; t[0x29] = "parenright"; t[0x2A] = "asterisk"; t[0x2B] = "plus";
        t[0x2C] = "comma"; t[0x2D] = "hyphen"; t[0x2E] = "period"; t[0x2F] = "slash";
        t[0x30] = "zero"; t[0x31] = "one"; t[0x32] = "two"; t[0x33] = "three";
        t[0x34] = "four"; t[0x35] = "five"; t[0x36] = "six"; t[0x37] = "seven";
        t[0x38] = "eight"; t[0x39] = "nine"; t[0x3A] = "colon"; t[0x3B] = "semicolon";
        t[0x3C] = "less"; t[0x3D] = "equal"; t[0x3E] = "greater"; t[0x3F] = "question";
        t[0x40] = "at";
        t[0x41] = "A"; t[0x42] = "B"; t[0x43] = "C"; t[0x44] = "D"; t[0x45] = "E"; t[0x46] = "F";
        t[0x47] = "G"; t[0x48] = "H"; t[0x49] = "I"; t[0x4A] = "J"; t[0x4B] = "K"; t[0x4C] = "L";
        t[0x4D] = "M"; t[0x4E] = "N"; t[0x4F] = "O"; t[0x50] = "P"; t[0x51] = "Q"; t[0x52] = "R";
        t[0x53] = "S"; t[0x54] = "T"; t[0x55] = "U"; t[0x56] = "V"; t[0x57] = "W"; t[0x58] = "X";
        t[0x59] = "Y"; t[0x5A] = "Z";
        t[0x5B] = "bracketleft"; t[0x5C] = "backslash"; t[0x5D] = "bracketright";
        t[0x5E] = "asciicircum"; t[0x5F] = "underscore"; t[0x60] = "grave";
        t[0x61] = "a"; t[0x62] = "b"; t[0x63] = "c"; t[0x64] = "d"; t[0x65] = "e"; t[0x66] = "f";
        t[0x67] = "g"; t[0x68] = "h"; t[0x69] = "i"; t[0x6A] = "j"; t[0x6B] = "k"; t[0x6C] = "l";
        t[0x6D] = "m"; t[0x6E] = "n"; t[0x6F] = "o"; t[0x70] = "p"; t[0x71] = "q"; t[0x72] = "r";
        t[0x73] = "s"; t[0x74] = "t"; t[0x75] = "u"; t[0x76] = "v"; t[0x77] = "w"; t[0x78] = "x";
        t[0x79] = "y"; t[0x7A] = "z";
        t[0x7B] = "braceleft"; t[0x7C] = "bar"; t[0x7D] = "braceright"; t[0x7E] = "asciitilde";
        // 0x80–0x9E: Windows-1252 extensions
        t[0x80] = "Euro"; t[0x82] = "quotesinglbase"; t[0x83] = "florin";
        t[0x84] = "quotedblbase"; t[0x85] = "ellipsis"; t[0x86] = "dagger";
        t[0x87] = "daggerdbl"; t[0x88] = "circumflex"; t[0x89] = "perthousand";
        t[0x8A] = "Scaron"; t[0x8B] = "guilsinglleft"; t[0x8C] = "OE";
        t[0x8E] = "Zcaron";
        t[0x91] = "quoteleft"; t[0x92] = "quoteright"; t[0x93] = "quotedblleft";
        t[0x94] = "quotedblright"; t[0x95] = "bullet"; t[0x96] = "endash";
        t[0x97] = "emdash"; t[0x98] = "tilde"; t[0x99] = "trademark";
        t[0x9A] = "scaron"; t[0x9B] = "guilsinglright"; t[0x9C] = "oe";
        t[0x9E] = "zcaron"; t[0x9F] = "Ydieresis";
        // 0xA0–0xFF: ISO Latin-1 supplement
        t[0xA0] = "nbspace"; t[0xA1] = "exclamdown"; t[0xA2] = "cent"; t[0xA3] = "sterling";
        t[0xA4] = "currency"; t[0xA5] = "yen"; t[0xA6] = "brokenbar"; t[0xA7] = "section";
        t[0xA8] = "dieresis"; t[0xA9] = "copyright"; t[0xAA] = "ordfeminine"; t[0xAB] = "guillemotleft";
        t[0xAC] = "logicalnot"; t[0xAD] = "softhyphen"; t[0xAE] = "registered"; t[0xAF] = "macron";
        t[0xB0] = "degree"; t[0xB1] = "plusminus"; t[0xB2] = "twosuperior"; t[0xB3] = "threesuperior";
        t[0xB4] = "acute"; t[0xB5] = "mu"; t[0xB6] = "paragraph"; t[0xB7] = "periodcentered";
        t[0xB8] = "cedilla"; t[0xB9] = "onesuperior"; t[0xBA] = "ordmasculine"; t[0xBB] = "guillemotright";
        t[0xBC] = "onequarter"; t[0xBD] = "onehalf"; t[0xBE] = "threequarters"; t[0xBF] = "questiondown";
        t[0xC0] = "Agrave"; t[0xC1] = "Aacute"; t[0xC2] = "Acircumflex"; t[0xC3] = "Atilde";
        t[0xC4] = "Adieresis"; t[0xC5] = "Aring"; t[0xC6] = "AE"; t[0xC7] = "Ccedilla";
        t[0xC8] = "Egrave"; t[0xC9] = "Eacute"; t[0xCA] = "Ecircumflex"; t[0xCB] = "Edieresis";
        t[0xCC] = "Igrave"; t[0xCD] = "Iacute"; t[0xCE] = "Icircumflex"; t[0xCF] = "Idieresis";
        t[0xD0] = "Eth"; t[0xD1] = "Ntilde"; t[0xD2] = "Ograve"; t[0xD3] = "Oacute";
        t[0xD4] = "Ocircumflex"; t[0xD5] = "Otilde"; t[0xD6] = "Odieresis"; t[0xD7] = "multiply";
        t[0xD8] = "Oslash"; t[0xD9] = "Ugrave"; t[0xDA] = "Uacute"; t[0xDB] = "Ucircumflex";
        t[0xDC] = "Udieresis"; t[0xDD] = "Yacute"; t[0xDE] = "Thorn"; t[0xDF] = "germandbls";
        t[0xE0] = "agrave"; t[0xE1] = "aacute"; t[0xE2] = "acircumflex"; t[0xE3] = "atilde";
        t[0xE4] = "adieresis"; t[0xE5] = "aring"; t[0xE6] = "ae"; t[0xE7] = "ccedilla";
        t[0xE8] = "egrave"; t[0xE9] = "eacute"; t[0xEA] = "ecircumflex"; t[0xEB] = "edieresis";
        t[0xEC] = "igrave"; t[0xED] = "iacute"; t[0xEE] = "icircumflex"; t[0xEF] = "idieresis";
        t[0xF0] = "eth"; t[0xF1] = "ntilde"; t[0xF2] = "ograve"; t[0xF3] = "oacute";
        t[0xF4] = "ocircumflex"; t[0xF5] = "otilde"; t[0xF6] = "odieresis"; t[0xF7] = "divide";
        t[0xF8] = "oslash"; t[0xF9] = "ugrave"; t[0xFA] = "uacute"; t[0xFB] = "ucircumflex";
        t[0xFC] = "udieresis"; t[0xFD] = "yacute"; t[0xFE] = "thorn"; t[0xFF] = "ydieresis";
        return t;
    }

    private static readonly string?[] MacRomanGlyphNames = BuildMacRoman();

    private static string?[] BuildMacRoman()
    {
        var t = new string?[256];
        // 0x20–0x7E: same as WinAnsi (ASCII printable)
        t[0x20] = "space"; t[0x21] = "exclam"; t[0x22] = "quotedbl"; t[0x23] = "numbersign";
        t[0x24] = "dollar"; t[0x25] = "percent"; t[0x26] = "ampersand"; t[0x27] = "quotesingle";
        t[0x28] = "parenleft"; t[0x29] = "parenright"; t[0x2A] = "asterisk"; t[0x2B] = "plus";
        t[0x2C] = "comma"; t[0x2D] = "hyphen"; t[0x2E] = "period"; t[0x2F] = "slash";
        t[0x30] = "zero"; t[0x31] = "one"; t[0x32] = "two"; t[0x33] = "three";
        t[0x34] = "four"; t[0x35] = "five"; t[0x36] = "six"; t[0x37] = "seven";
        t[0x38] = "eight"; t[0x39] = "nine"; t[0x3A] = "colon"; t[0x3B] = "semicolon";
        t[0x3C] = "less"; t[0x3D] = "equal"; t[0x3E] = "greater"; t[0x3F] = "question";
        t[0x40] = "at";
        t[0x41] = "A"; t[0x42] = "B"; t[0x43] = "C"; t[0x44] = "D"; t[0x45] = "E"; t[0x46] = "F";
        t[0x47] = "G"; t[0x48] = "H"; t[0x49] = "I"; t[0x4A] = "J"; t[0x4B] = "K"; t[0x4C] = "L";
        t[0x4D] = "M"; t[0x4E] = "N"; t[0x4F] = "O"; t[0x50] = "P"; t[0x51] = "Q"; t[0x52] = "R";
        t[0x53] = "S"; t[0x54] = "T"; t[0x55] = "U"; t[0x56] = "V"; t[0x57] = "W"; t[0x58] = "X";
        t[0x59] = "Y"; t[0x5A] = "Z";
        t[0x5B] = "bracketleft"; t[0x5C] = "backslash"; t[0x5D] = "bracketright";
        t[0x5E] = "asciicircum"; t[0x5F] = "underscore"; t[0x60] = "grave";
        t[0x61] = "a"; t[0x62] = "b"; t[0x63] = "c"; t[0x64] = "d"; t[0x65] = "e"; t[0x66] = "f";
        t[0x67] = "g"; t[0x68] = "h"; t[0x69] = "i"; t[0x6A] = "j"; t[0x6B] = "k"; t[0x6C] = "l";
        t[0x6D] = "m"; t[0x6E] = "n"; t[0x6F] = "o"; t[0x70] = "p"; t[0x71] = "q"; t[0x72] = "r";
        t[0x73] = "s"; t[0x74] = "t"; t[0x75] = "u"; t[0x76] = "v"; t[0x77] = "w"; t[0x78] = "x";
        t[0x79] = "y"; t[0x7A] = "z";
        t[0x7B] = "braceleft"; t[0x7C] = "bar"; t[0x7D] = "braceright"; t[0x7E] = "asciitilde";
        // 0x80–0xFF: Mac Roman extended (ISO 32000-1 Annex D.2)
        t[0x80] = "Adieresis"; t[0x81] = "Aring"; t[0x82] = "Ccedilla"; t[0x83] = "Eacute";
        t[0x84] = "Ntilde"; t[0x85] = "Odieresis"; t[0x86] = "Udieresis"; t[0x87] = "aacute";
        t[0x88] = "agrave"; t[0x89] = "acircumflex"; t[0x8A] = "adieresis"; t[0x8B] = "atilde";
        t[0x8C] = "aring"; t[0x8D] = "ccedilla"; t[0x8E] = "eacute"; t[0x8F] = "egrave";
        t[0x90] = "ecircumflex"; t[0x91] = "edieresis"; t[0x92] = "iacute"; t[0x93] = "igrave";
        t[0x94] = "icircumflex"; t[0x95] = "idieresis"; t[0x96] = "ntilde"; t[0x97] = "oacute";
        t[0x98] = "ograve"; t[0x99] = "ocircumflex"; t[0x9A] = "odieresis"; t[0x9B] = "otilde";
        t[0x9C] = "uacute"; t[0x9D] = "ugrave"; t[0x9E] = "ucircumflex"; t[0x9F] = "udieresis";
        t[0xA0] = "dagger"; t[0xA1] = "degree"; t[0xA2] = "cent"; t[0xA3] = "sterling";
        t[0xA4] = "section"; t[0xA5] = "bullet"; t[0xA6] = "paragraph"; t[0xA7] = "germandbls";
        t[0xA8] = "registered"; t[0xA9] = "copyright"; t[0xAA] = "trademark"; t[0xAB] = "acute";
        t[0xAC] = "dieresis"; t[0xAD] = "notequal"; t[0xAE] = "AE"; t[0xAF] = "Oslash";
        t[0xB0] = "infinity"; t[0xB1] = "plusminus"; t[0xB2] = "lessequal"; t[0xB3] = "greaterequal";
        t[0xB4] = "yen"; t[0xB5] = "mu"; t[0xB6] = "partialdiff"; t[0xB7] = "summation";
        t[0xB8] = "product"; t[0xB9] = "pi"; t[0xBA] = "integral"; t[0xBB] = "ordfeminine";
        t[0xBC] = "ordmasculine"; t[0xBD] = "Omega"; t[0xBE] = "ae"; t[0xBF] = "oslash";
        t[0xC0] = "questiondown"; t[0xC1] = "exclamdown"; t[0xC2] = "logicalnot"; t[0xC3] = "radical";
        t[0xC4] = "florin"; t[0xC5] = "approxequal"; t[0xC6] = "Delta"; t[0xC7] = "guillemotleft";
        t[0xC8] = "guillemotright"; t[0xC9] = "ellipsis"; t[0xCA] = "nbspace"; t[0xCB] = "Agrave";
        t[0xCC] = "Atilde"; t[0xCD] = "Otilde"; t[0xCE] = "OE"; t[0xCF] = "oe";
        t[0xD0] = "endash"; t[0xD1] = "emdash"; t[0xD2] = "quotedblleft"; t[0xD3] = "quotedblright";
        t[0xD4] = "quoteleft"; t[0xD5] = "quoteright"; t[0xD6] = "divide"; t[0xD7] = "lozenge";
        t[0xD8] = "ydieresis"; t[0xD9] = "Ydieresis"; t[0xDA] = "fraction"; t[0xDB] = "Euro";
        t[0xDC] = "guilsinglleft"; t[0xDD] = "guilsinglright"; t[0xDE] = "fi"; t[0xDF] = "fl";
        t[0xE0] = "daggerdbl"; t[0xE1] = "periodcentered"; t[0xE2] = "quotesinglbase"; t[0xE3] = "quotedblbase";
        t[0xE4] = "perthousand"; t[0xE5] = "Acircumflex"; t[0xE6] = "Ecircumflex"; t[0xE7] = "Aacute";
        t[0xE8] = "Edieresis"; t[0xE9] = "Egrave"; t[0xEA] = "Iacute"; t[0xEB] = "Icircumflex";
        t[0xEC] = "Idieresis"; t[0xED] = "Igrave"; t[0xEE] = "Oacute"; t[0xEF] = "Ocircumflex";
        t[0xF0] = "apple"; t[0xF1] = "Ograve"; t[0xF2] = "Uacute"; t[0xF3] = "Ucircumflex";
        t[0xF4] = "Ugrave"; t[0xF5] = "dotlessi"; t[0xF6] = "circumflex"; t[0xF7] = "tilde";
        t[0xF8] = "macron"; t[0xF9] = "breve"; t[0xFA] = "dotaccent"; t[0xFB] = "ring";
        t[0xFC] = "cedilla"; t[0xFD] = "hungarumlaut"; t[0xFE] = "ogonek"; t[0xFF] = "caron";
        return t;
    }
}
