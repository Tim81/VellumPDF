// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Fonts.Cff;
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
    private static readonly PdfName _fontFile = new("FontFile");
    private static readonly PdfName _fontFile3 = new("FontFile3");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");
    private static readonly PdfName _flags = new("Flags");
    private static readonly PdfName _firstChar = new("FirstChar");
    private static readonly PdfName _lastChar = new("LastChar");
    private static readonly PdfName _widths = new("Widths");
    private static readonly PdfName _baseEncoding = new("BaseEncoding");
    private static readonly PdfName _differences = new("Differences");
    private static readonly PdfName _length1 = new("Length1");
    private static readonly PdfName _length2 = new("Length2");

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
            PdfName { Value: "Type1" } => TryGetSimpleType1Font(context, font),
            _ => null,
        };
    }

    // ── Type 0 composite fonts ────────────────────────────────────────────────────────────────────

    private static CheckedFont? TryGetType0Font(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants || descendants.Count == 0)
            return null;
        if (context.Resolve(descendants[0]) is not PdfDictionary cidFont)
            return null;

        var cidSubtype = context.Resolve(cidFont.Get(PdfName.Subtype));

        if (cidSubtype is PdfName { Value: "CIDFontType2" })
            return TryGetCidFontType2(context, font, cidFont);
        if (cidSubtype is PdfName { Value: "CIDFontType0" })
            return TryGetCidFontType0(context, font, cidFont);
        return null;
    }

    private static CheckedFont? TryGetCidFontType2(PreflightContext context, PdfDictionary font, PdfDictionary cidFont)
    {
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

    // Wave 2b — CIDFontType0 with embedded CFF via /FontFile3 (CIDFontType0C or OpenType).
    private static CheckedFont? TryGetCidFontType0(PreflightContext context, PdfDictionary font, PdfDictionary cidFont)
    {
        if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;

        // /FontFile3 is the only valid embedding key for CIDFontType0.
        if (descriptor.Get(_fontFile3) is not PdfIndirectReference fontFile3Ref)
            return null;
        if (context.ResolveStream(fontFile3Ref) is not { } ff3Stream)
            return null;
        if (context.DecodeStream(ff3Stream) is not { } ff3Bytes)
            return null;

        var ff3Subtype = (context.Resolve(ff3Stream.Dictionary.Get(PdfName.Subtype)) as PdfName)?.Value;

        // Extract the raw CFF bytes depending on the wrapper format.
        byte[] cffBytes;
        if (ff3Subtype == "OpenType")
        {
            // OpenType-CFF: full SFNT — extract the 'CFF ' table.
            try
            {
                var sfnt = SfntFont.Parse(new ReadOnlyMemory<byte>(ff3Bytes));
                if (!sfnt.HasTable(new Tag("CFF ")))
                    return null;
                cffBytes = sfnt.GetTableBytes(new Tag("CFF ")).ToArray();
            }
            catch
            {
                return null;
            }
        }
        else if (ff3Subtype is "CIDFontType0C" or "Type1C")
        {
            cffBytes = ff3Bytes;
        }
        else
        {
            return null; // unsupported or missing subtype
        }

        CffFont cff;
        try { cff = CffFont.Parse(new ReadOnlyMemory<byte>(cffBytes)); }
        catch { return null; }

        if (!CffWidths.TryCreate(cff, out var cffWidths) || cffWidths is null)
            return null;

        var cidWidths = CidWidths.Parse(context, cidFont);

        var rawEncoding = font.Get(_encoding);
        var encoding = context.Resolve(rawEncoding);

        if (encoding is PdfName { Value: "Identity-H" or "Identity-V" })
        {
            // Identity-H/V: code == CID. For CIDFontType0 embedded by VellumPdf (and most
            // real-world producers), GIDs are used as codes so CID == GID. Passing cidToGid=null
            // makes CheckCid use gid=cid directly, which is correct for Identity encoding.
            return new CidFontType0CFont(fontFile3Ref.ObjectNumber, cffWidths, cidWidths, cidToGid: null, cmap: null);
        }

        // Embedded CMap stream path: code → CID via CMap, then CID → GID via CFF charset.
        if (encoding is PdfName)
            return null; // predefined named CMap — no mapping table available
        if (rawEncoding is not PdfIndirectReference cmapRef)
            return null;
        if (context.ResolveStream(cmapRef) is not { } cmapStream)
            return null;
        if (context.DecodeStream(cmapStream) is not { } cmapBytes)
            return null;
        var parsedCMap = EmbeddedCMapParser.Parse(cmapBytes);
        if (parsedCMap is null)
            return null;

        // For embedded CMap path, build CID→GID map from the CFF charset if CID-keyed.
        Dictionary<int, int>? cidToGid = cff.IsCidKeyed
            ? CffWidths.TryBuildCidToGidMap(cff)
            : null;

        return new CidFontType0CFont(fontFile3Ref.ObjectNumber, cffWidths, cidWidths, cidToGid, parsedCMap);
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

    // Wave 2b — simple Type1 font with embedded /FontFile program.
    private static CheckedFont? TryGetSimpleType1Font(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;

        // Only check if the font has an embedded /FontFile (binary Type1) program.
        if (descriptor.Get(_fontFile) is not PdfIndirectReference fontFileRef)
            return null;
        if (context.ResolveStream(fontFileRef) is not { } ffStream)
            return null;
        if (context.DecodeStream(ffStream) is not { } ffBytes)
            return null;

        var length1 = context.Resolve(ffStream.Dictionary.Get(_length1)) is PdfInteger l1 ? (int)l1.Value : 0;

        var glyphNames = Type1Glyphs.TryEnumerate(ffBytes, length1);
        if (glyphNames is null)
            return null;

        var widths = Type1Glyphs.TryGetWidths(ffBytes, length1);
        // widths may be null if decryption fails — we still do presence checks.

        // Resolve encoding: /Encoding can be a name or a dict with /Differences.
        var rawEncoding = font.Get(_encoding);
        if (rawEncoding is null)
            return null;
        var resolvedEncoding = context.Resolve(rawEncoding);

        string? baseEncName;
        PdfArray? differences = null;

        if (resolvedEncoding is PdfName en)
        {
            baseEncName = en.Value;
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

        // Build charcode→glyphname array (256 slots).
        var codeToName = new string?[256];

        var baseTable = baseEncName switch
        {
            "WinAnsiEncoding" => WinAnsiGlyphNames,
            "MacRomanEncoding" => MacRomanGlyphNames,
            "StandardEncoding" => StandardEncoding,
            _ => null,
        };
        if (baseTable is not null)
            Array.Copy(baseTable, codeToName, 256);

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
                        codeToName[code] = gn.Value;
                    code++;
                }
            }
        }

        // /Widths array for declared width lookup.
        var firstChar = context.Resolve(font.Get(_firstChar)) is PdfInteger fc ? (int)fc.Value : -1;
        var lastChar = context.Resolve(font.Get(_lastChar)) is PdfInteger lc ? (int)lc.Value : -1;

        int[]? widthArray = null;
        if (firstChar >= 0 && lastChar >= firstChar && lastChar <= 255
            && context.Resolve(font.Get(_widths)) is PdfArray wa)
        {
            widthArray = new int[lastChar - firstChar + 1];
            for (var wi = 0; wi < widthArray.Length && wi < wa.Count; wi++)
                widthArray[wi] = context.Resolve(wa[wi]) switch
                {
                    PdfInteger wv => (int)wv.Value,
                    PdfReal wr => (int)Math.Round(wr.Value),
                    _ => 0,
                };
        }

        return new SimpleType1Font(fontFileRef.ObjectNumber, glyphNames, widths, codeToName, firstChar, lastChar, widthArray);
    }

    // ── Content stream scanner ────────────────────────────────────────────────────────────────────

    private void ScanContent(
        PreflightContext context, byte[] content, Dictionary<string, CheckedFont> fontMap, HashSet<string> reported)
    {
        CheckedFont? current = null;
        var tr = 0; // current text rendering mode (Tr); 3 = invisible, exempt per ISO 19005-2 §6.2.11
        try
        {
            var lexer = new PdfLexer(content);
            string? lastName = null;
            var pending = new List<byte[]>();
            var pendingNumbers = new List<double>();

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
                    case TokenKind.Integer:
                        if (int.TryParse(Encoding.Latin1.GetString(token.Raw.Span), out var iv))
                            pendingNumbers.Add(iv);
                        break;
                    case TokenKind.Real:
                        if (double.TryParse(Encoding.Latin1.GetString(token.Raw.Span),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var rv))
                            pendingNumbers.Add(rv);
                        break;
                    case TokenKind.Keyword:
                        var op = Encoding.Latin1.GetString(token.Raw.Span);
                        if (op == "Tf")
                            current = lastName is not null && fontMap.TryGetValue(lastName, out var f) ? f : null;
                        else if (op == "Tr" && pendingNumbers.Count > 0)
                            tr = (int)pendingNumbers[pendingNumbers.Count - 1];
                        else if (op == "BT")
                            tr = 0; // reset Tr to default at start of text object
                        else if (op is "Tj" or "TJ" or "'" or "\"")
                            ConsumeGlyphs(context, current, pending, reported, tr);
                        pending.Clear();
                        pendingNumbers.Clear();
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
        PreflightContext context, CheckedFont? current, List<byte[]> strings, HashSet<string> reported, int tr)
    {
        if (current is null)
            return;
        // Text rendering mode 3 (invisible): glyph not rendered, exempt from presence/width checks.
        if (tr == 3)
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

    private abstract class CheckedFont(int programObject, SfntMetrics? metrics)
    {
        public int ProgramObject { get; } = programObject;
        public SfntMetrics? Metrics { get; } = metrics;

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
                    rule.CheckGlyph(context, gid, declaredWidth, ProgramObject, Metrics!, reported);
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
                    rule.CheckGlyph(context, gid, declaredWidth, ProgramObject, Metrics!, reported);
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

    // Wave 2b — CIDFontType0 with embedded CFF program (/FontFile3 CIDFontType0C or OpenType).
    // Identity-H/V: 2-byte-BE code → CID; CID → GID via CFF charset (or CID==GID for non-CID CFF).
    private sealed class CidFontType0CFont(
        int programObject, CffWidths cffWidths, CidWidths cidWidths,
        Dictionary<int, int>? cidToGid, ParsedCMap? cmap)
        : CheckedFont(programObject, null!)
    {
        private readonly CffWidths _cffWidths = cffWidths;
        private readonly CidWidths _cidWidths = cidWidths;
        private readonly Dictionary<int, int>? _cidToGid = cidToGid;
        private readonly ParsedCMap? _cmap = cmap;

        public override void CheckStrings(
            PreflightContext context, List<byte[]> strings, HashSet<string> reported, GlyphPresenceRule rule)
        {
            if (_cmap is null)
                CheckIdentityStrings(context, strings, reported, rule);
            else
                CheckCMapStrings(context, strings, reported, rule);
        }

        private void CheckIdentityStrings(
            PreflightContext context, List<byte[]> strings, HashSet<string> reported, GlyphPresenceRule rule)
        {
            foreach (var bytes in strings)
            {
                for (var i = 0; i + 1 < bytes.Length; i += 2)
                {
                    var cid = (bytes[i] << 8) | bytes[i + 1];
                    CheckCid(context, cid, reported, rule);
                }
            }
        }

        private void CheckCMapStrings(
            PreflightContext context, List<byte[]> strings, HashSet<string> reported, GlyphPresenceRule rule)
        {
            foreach (var bytes in strings)
            {
                foreach (var code in _cmap!.DecodeCodes(bytes))
                {
                    if (!_cmap.TryLookupCid(code, out var cid))
                        continue;
                    CheckCid(context, cid, reported, rule);
                }
            }
        }

        private void CheckCid(PreflightContext context, int cid, HashSet<string> reported, GlyphPresenceRule rule)
        {
            // Map CID → GID
            int gid;
            if (_cidToGid is not null)
            {
                if (!_cidToGid.TryGetValue(cid, out gid))
                    gid = 0; // CID not in charset → .notdef
            }
            else
            {
                gid = cid; // non-CID-keyed CFF: GID == CID
            }

            if (gid == 0 && reported.Add($"{ProgramObject}:notdef"))
            {
                context.Report("ISO19005-2:6.2.11.8-notdef", "ISO 19005-2:2011, 6.2.11.8",
                    PreflightSeverity.Error,
                    "The document references the .notdef glyph (glyph index 0) of a font, "
                    + "which is not permitted in PDF/A-2.");
                return;
            }

            if (gid >= _cffWidths.GlyphCount && reported.Add($"{ProgramObject}:present"))
            {
                context.Report("ISO19005-2:6.2.11.4.1-glyph-present", "ISO 19005-2:2011, 6.2.11.4.1",
                    PreflightSeverity.Error,
                    $"A glyph (CID {cid}, GID {gid}) drawn with a CFF font is not present in the embedded "
                    + $"font program, which defines {_cffWidths.GlyphCount} glyphs.");
                return;
            }

            if (_cffWidths.TryGetWidth(gid, out var programWidthRaw))
            {
                // CFF widths are in glyph-space units. For CIDFontType0 fonts the unitsPerEm may differ;
                // however, the PDF /W array is already in thousandths-of-a-unit (same units as the glyph
                // space for a 1000-unit-em CFF font, which is the overwhelming common case). We compare
                // the raw CFF width to the declared /W value with a tolerance of 1 unit.
                var declaredWidth = _cidWidths.GetWidth(cid);
                var programWidth = (int)Math.Round(programWidthRaw);
                if (Math.Abs(declaredWidth - programWidth) > 1 && reported.Add($"{ProgramObject}:width"))
                {
                    context.Report("ISO19005-2:6.2.11.5-glyph-width", "ISO 19005-2:2011, 6.2.11.5",
                        PreflightSeverity.Error,
                        $"The width declared for CID {cid} ({declaredWidth}) does not match the "
                        + $"embedded CFF font program's advance width ({programWidth}).");
                }
            }
        }
    }

    // Wave 2b — simple Type1 font with embedded /FontFile program.
    // Charcode (1 byte) → glyph name (via encoding/Differences) → presence in Type1 CharStrings.
    private sealed class SimpleType1Font(
        int programObject, HashSet<string> glyphNames,
        Dictionary<string, double>? charstringWidths,
        string?[] codeToName, int firstChar, int lastChar, int[]? widthArray)
        : CheckedFont(programObject, null!)
    {
        private readonly HashSet<string> _glyphNames = glyphNames;
        private readonly Dictionary<string, double>? _charstringWidths = charstringWidths;
        private readonly string?[] _codeToName = codeToName;
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
                    var glyphName = charCode < _codeToName.Length ? _codeToName[charCode] : null;
                    if (glyphName is null)
                        continue; // no mapping for this code — skip defensively (no FP)

                    if (glyphName == ".notdef")
                        continue; // .notdef exemption: §6.2.11.8 is different for simple fonts

                    // Presence: glyph name must be in the embedded program's CharStrings.
                    if (!_glyphNames.Contains(glyphName))
                    {
                        if (reported.Add($"{ProgramObject}:present"))
                            context.Report("ISO19005-2:6.2.11.4.1-glyph-present", "ISO 19005-2:2011, 6.2.11.4.1",
                                PreflightSeverity.Error,
                                $"Glyph '{glyphName}' drawn with a Type1 font is not present in the embedded "
                                + "font program's CharStrings dictionary.");
                        continue;
                    }

                    // Width: compare declared /Widths entry to charstring advance width.
                    if (_charstringWidths is not null
                        && _charstringWidths.TryGetValue(glyphName, out var programWidth)
                        && _widthArray is not null
                        && charCode >= _firstChar && charCode <= _lastChar)
                    {
                        var declaredWidth = _widthArray[charCode - _firstChar];
                        if (declaredWidth != 0 && Math.Abs(declaredWidth - programWidth) > 1
                            && reported.Add($"{ProgramObject}:width"))
                        {
                            context.Report("ISO19005-2:6.2.11.5-glyph-width", "ISO 19005-2:2011, 6.2.11.5",
                                PreflightSeverity.Error,
                                $"The width declared for glyph '{glyphName}' ({declaredWidth}) does not match "
                                + $"the embedded Type1 font program's advance width ({(int)Math.Round(programWidth)}).");
                        }
                    }
                }
            }
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
                    var sfntMetrics = Metrics!;
                    if (gid >= sfntMetrics.NumGlyphs && reported.Add($"{ProgramObject}:present"))
                    {
                        context.Report("ISO19005-2:6.2.11.4.1-glyph-present", "ISO 19005-2:2011, 6.2.11.4.1",
                            PreflightSeverity.Error,
                            $"Glyph '{glyphName}' (GID {gid}) drawn with a simple TrueType font is not present in the "
                            + $"embedded font program, which defines {sfntMetrics.NumGlyphs} glyphs.");
                        continue;
                    }

                    // Width check.
                    if (sfntMetrics.AdvanceWidth1000(gid) is { } programWidth
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

    // Adobe Standard Encoding (ISO 32000-1 Annex D.1), used as base for many Type1 fonts.
    private static readonly string?[] StandardEncoding = BuildStandardEncoding();

    private static string?[] BuildStandardEncoding()
    {
        var t = new string?[256];
        // 0x20–0x7E: printable ASCII (same glyph names as WinAnsi for this range)
        t[0x20] = "space"; t[0x21] = "exclam"; t[0x22] = "quotedbl"; t[0x23] = "numbersign";
        t[0x24] = "dollar"; t[0x25] = "percent"; t[0x26] = "ampersand"; t[0x27] = "quoteright";
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
        t[0x5E] = "asciicircum"; t[0x5F] = "underscore"; t[0x60] = "quoteleft";
        t[0x61] = "a"; t[0x62] = "b"; t[0x63] = "c"; t[0x64] = "d"; t[0x65] = "e"; t[0x66] = "f";
        t[0x67] = "g"; t[0x68] = "h"; t[0x69] = "i"; t[0x6A] = "j"; t[0x6B] = "k"; t[0x6C] = "l";
        t[0x6D] = "m"; t[0x6E] = "n"; t[0x6F] = "o"; t[0x70] = "p"; t[0x71] = "q"; t[0x72] = "r";
        t[0x73] = "s"; t[0x74] = "t"; t[0x75] = "u"; t[0x76] = "v"; t[0x77] = "w"; t[0x78] = "x";
        t[0x79] = "y"; t[0x7A] = "z";
        t[0x7B] = "braceleft"; t[0x7C] = "bar"; t[0x7D] = "braceright"; t[0x7E] = "asciitilde";
        // 0xA1–0xFF: Standard Encoding extended range (ISO 32000-1 Annex D.1)
        t[0xA1] = "exclamdown"; t[0xA2] = "cent"; t[0xA3] = "sterling"; t[0xA4] = "fraction";
        t[0xA5] = "yen"; t[0xA6] = "florin"; t[0xA7] = "section"; t[0xA8] = "currency";
        t[0xA9] = "quotesingle"; t[0xAA] = "quotedblleft"; t[0xAB] = "guillemotleft";
        t[0xAC] = "guilsinglleft"; t[0xAD] = "guilsinglright"; t[0xAE] = "fi"; t[0xAF] = "fl";
        t[0xB1] = "endash"; t[0xB2] = "dagger"; t[0xB3] = "daggerdbl"; t[0xB4] = "periodcentered";
        t[0xB6] = "paragraph"; t[0xB7] = "bullet"; t[0xB8] = "quotesinglbase"; t[0xB9] = "quotedblbase";
        t[0xBA] = "quotedblright"; t[0xBB] = "guillemotright"; t[0xBC] = "ellipsis";
        t[0xBD] = "perthousand"; t[0xBF] = "questiondown";
        t[0xC1] = "grave"; t[0xC2] = "acute"; t[0xC3] = "circumflex"; t[0xC4] = "tilde";
        t[0xC5] = "macron"; t[0xC6] = "breve"; t[0xC7] = "dotaccent"; t[0xC8] = "dieresis";
        t[0xCA] = "ring"; t[0xCB] = "cedilla"; t[0xCD] = "hungarumlaut"; t[0xCE] = "ogonek";
        t[0xCF] = "caron"; t[0xD0] = "emdash";
        t[0xE1] = "AE"; t[0xE3] = "ordfeminine"; t[0xE8] = "Lslash"; t[0xE9] = "Oslash";
        t[0xEA] = "OE"; t[0xEB] = "ordmasculine"; t[0xF1] = "ae"; t[0xF5] = "dotlessi";
        t[0xF8] = "lslash"; t[0xF9] = "oslash"; t[0xFA] = "oe"; t[0xFB] = "germandbls";
        return t;
    }
}
