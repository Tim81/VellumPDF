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
            "WinAnsiEncoding" => SimpleFontEncoding.WinAnsi,
            "MacRomanEncoding" => SimpleFontEncoding.MacRoman,
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
            "WinAnsiEncoding" => SimpleFontEncoding.WinAnsi,
            "MacRomanEncoding" => SimpleFontEncoding.MacRoman,
            "StandardEncoding" => SimpleFontEncoding.Standard,
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
                // CffWidths.TryGetWidth applies the font's FontMatrix scaling and returns a width in
                // 1000-unit text space — the same units as the PDF /W array. We compare this scaled
                // width to the declared /W value with a tolerance of 1 unit.
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

}
