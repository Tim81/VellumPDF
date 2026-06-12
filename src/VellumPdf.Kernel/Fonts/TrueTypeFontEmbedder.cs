// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Fonts.Cff;
using VellumPdf.Fonts.Sfnt;

namespace VellumPdf.Fonts;

/// <summary>
/// Builds the PDF objects required to embed a TrueType/OpenType font as Type0 /
/// CIDFontType2 (glyf outlines) or CIDFontType0 (CFF/OTTO outlines) with
/// Identity-H encoding (ISO 32000-2 §9.7 / §9.9).
///
/// This is the primary embedding path: it supports the full Unicode range,
/// avoids the 256-glyph limit of Type1/WinAnsi, and produces compliant
/// text that is searchable and copy-pasteable.
/// </summary>
public sealed class TrueTypeFontEmbedder
{
    private readonly SfntFont _sfnt;
    private readonly ReadOnlyMemory<byte> _fontData; // retained for whole-font CFF embedding
    private readonly CmapTable _cmap;
    private readonly HeadTable _head;
    private readonly HheaTable _hhea;
    private readonly HmtxTable _hmtx;
    private readonly MaxpTable _maxp;
    private readonly NameTable _name;
    private readonly Os2Table _os2;
    private readonly PostTable _post;

    private readonly HashSet<int> _usedGids = new() { 0 }; // always keep .notdef
    private readonly Dictionary<int, ushort> _unicodeToGid = new();
    private readonly Tag _cffTag = new("CFF ");

    // Set to true when CffSubsetter throws NotSupportedException (CID-keyed font).
    // In that case we embed the whole font verbatim and must not use the subset-tagged name.
    private bool _cffFellBackToWholeFont;

    /// <summary>The resource name used to reference this font from a page's resource dictionary.</summary>
    public string ResourceName { get; }

    /// <summary>Parses the SFNT font program and prepares it for embedding under the given resource name.</summary>
    public TrueTypeFontEmbedder(ReadOnlyMemory<byte> fontData, string resourceName)
    {
        _sfnt = SfntFont.Parse(fontData);
        _fontData = fontData;

        _cmap = CmapTable.Parse(_sfnt);
        _head = HeadTable.Parse(_sfnt);
        _hhea = HheaTable.Parse(_sfnt);
        _maxp = MaxpTable.Parse(_sfnt);
        _name = NameTable.Parse(_sfnt);
        _os2 = Os2Table.Parse(_sfnt);
        _post = PostTable.Parse(_sfnt);
        _hmtx = HmtxTable.Parse(_sfnt, _hhea, _maxp.NumGlyphs);
        ResourceName = resourceName;
    }

    /// <summary>Returns the glyph ID for a Unicode code point, registering it for subsetting.</summary>
    public ushort GetGlyphId(int codePoint)
    {
        if (_unicodeToGid.TryGetValue(codePoint, out var cachedGid)) return cachedGid;
        var gid = _cmap.GetGlyphId(codePoint);
        _unicodeToGid[codePoint] = gid;
        _usedGids.Add(gid);
        return gid;
    }

    /// <summary>Advance width in PDF units (1/1000 pt) at 1-pt font size.</summary>
    public int GetAdvanceWidth(ushort gid)
    {
        var w = _hmtx.GetAdvanceWidth(gid);
        return (int)Math.Round(w * 1000.0 / _head.UnitsPerEm);
    }

    /// <summary>Measures a Unicode string in PDF points at the given size.</summary>
    public double MeasureString(string text, double pointSize)
    {
        var total = 0.0;
        foreach (var cp in EnumerateCodePoints(text))
        {
            var gid = GetGlyphId(cp);
            total += GetAdvanceWidth(gid);
        }
        return total / 1000.0 * pointSize;
    }

    private string PostScriptName =>
        _name.PostScriptName ?? _name.FamilyName ?? "Unknown";

    // A subsetted font's name must carry a six-uppercase-letter tag + '+' prefix
    // (ISO 32000-1 §9.6.4 / §9.7.4.2). Both CFF/OTTO and glyf-outline fonts are
    // now subsetted, so both carry the tag.
    // When the CFF subsetter fell back to whole-font embedding (CID-keyed font),
    // the subset tag must NOT be applied — the font is not actually subsetted.
    private string SubsetPostScriptName =>
        _cffFellBackToWholeFont ? PostScriptName : $"{SubsetTag()}+{PostScriptName}";

    // Six uppercase letters derived deterministically from the embedded glyph set,
    // so the same subset always gets the same tag and output stays reproducible.
    private string SubsetTag()
    {
        var gids = _usedGids.Order().ToArray();
        var input = new byte[gids.Length * 2];
        for (var i = 0; i < gids.Length; i++)
        {
            input[i * 2] = (byte)(gids[i] >> 8);
            input[i * 2 + 1] = (byte)gids[i];
        }
        var hash = System.Security.Cryptography.SHA256.HashData(input);
        var tag = new char[6];
        for (var i = 0; i < 6; i++)
            tag[i] = (char)('A' + hash[i] % 26);
        return new string(tag);
    }

    // ── Build all required PDF objects ──────────────────────────────────────

    /// <summary>Builds the top-level Type0 composite font dictionary referencing the descendant CIDFont and ToUnicode CMap.</summary>
    public Core.PdfDictionary BuildFontDictionary(
        Core.PdfIndirectReference cidFontRef,
        Core.PdfIndirectReference toUnicodeRef)
    {
        // /DescendantFonts must be an inline array per PDF/A validators — not an indirect ref.
        var descendantFonts = new Core.PdfArray([cidFontRef]);
        var d = new Core.PdfDictionary()
            .Set(Core.PdfName.Type, Core.PdfName.Font)
            .Set(Core.PdfName.Subtype, new Core.PdfName("Type0"))
            .Set(Core.PdfName.BaseFont, new Core.PdfName(SubsetPostScriptName))
            .Set(new Core.PdfName("Encoding"), new Core.PdfName("Identity-H"))
            .Set(new Core.PdfName("DescendantFonts"), descendantFonts)
            .Set(new Core.PdfName("ToUnicode"), toUnicodeRef);
        return d;
    }

    /// <summary>Builds the descendant CIDFont dictionary (CIDFontType0 for CFF, CIDFontType2 for glyf) with its width array.</summary>
    public Core.PdfDictionary BuildCidFontDictionary(Core.PdfIndirectReference descriptorRef)
    {
        var widths = BuildWidthsArray();
        if (_sfnt.IsCff)
        {
            // CFF path: CIDFontType0, no /CIDToGIDMap (CIDFontType2-only key).
            // /BaseFont must carry the subset tag to match the descriptor and Type0 dict.
            return new Core.PdfDictionary()
                .Set(Core.PdfName.Type, Core.PdfName.Font)
                .Set(Core.PdfName.Subtype, new Core.PdfName("CIDFontType0"))
                .Set(Core.PdfName.BaseFont, new Core.PdfName(SubsetPostScriptName))
                .Set(new Core.PdfName("CIDSystemInfo"), BuildCidSystemInfo())
                .Set(new Core.PdfName("FontDescriptor"), descriptorRef)
                .Set(new Core.PdfName("DW"), new Core.PdfInteger(1000))
                .Set(new Core.PdfName("W"), widths);
        }

        // TrueType (glyf) path: CIDFontType2 with Identity CIDToGIDMap
        return new Core.PdfDictionary()
            .Set(Core.PdfName.Type, Core.PdfName.Font)
            .Set(Core.PdfName.Subtype, new Core.PdfName("CIDFontType2"))
            .Set(Core.PdfName.BaseFont, new Core.PdfName(SubsetPostScriptName))
            .Set(new Core.PdfName("CIDSystemInfo"), BuildCidSystemInfo())
            .Set(new Core.PdfName("FontDescriptor"), descriptorRef)
            .Set(new Core.PdfName("DW"), new Core.PdfInteger(1000))
            .Set(new Core.PdfName("W"), widths)
            .Set(new Core.PdfName("CIDToGIDMap"), new Core.PdfName("Identity"));
    }

    /// <summary>Builds the /FontDescriptor dictionary (flags, metrics, bounding box) pointing at the embedded font file.</summary>
    public Core.PdfDictionary BuildFontDescriptor(Core.PdfIndirectReference fontFileRef)
    {
        var unitsPerEm = _head.UnitsPerEm;
        double Scale(int v) => Math.Round(v * 1000.0 / unitsPerEm);

        var flags = 32; // Nonsymbolic (bit 6 = 32)
        if (_post.IsFixedPitch) flags |= 1;
        if (_post.ItalicAngle != 0) flags |= 64; // Italic (bit 7)

        var descriptor = new Core.PdfDictionary()
            .Set(Core.PdfName.Type, new Core.PdfName("FontDescriptor"))
            .Set(new Core.PdfName("FontName"), new Core.PdfName(SubsetPostScriptName))
            .Set(new Core.PdfName("Flags"), (long)flags)
            .Set(new Core.PdfName("ItalicAngle"), new Core.PdfReal(_post.ItalicAngle))
            .Set(new Core.PdfName("Ascent"), new Core.PdfReal(Scale(_os2.TypoAscender)))
            .Set(new Core.PdfName("Descent"), new Core.PdfReal(Scale(_os2.TypoDescender)))
            .Set(new Core.PdfName("CapHeight"), new Core.PdfReal(Scale(_os2.CapHeight != 0 ? _os2.CapHeight : _os2.TypoAscender)))
            .Set(new Core.PdfName("StemV"), new Core.PdfInteger(80)) // heuristic
            .Set(new Core.PdfName("FontBBox"), BuildFontBBox());

        // CFF fonts use /FontFile3 (with /Subtype /OpenType); TrueType fonts use /FontFile2.
        if (_sfnt.IsCff)
            descriptor.Set(new Core.PdfName("FontFile3"), fontFileRef);
        else
            descriptor.Set(new Core.PdfName("FontFile2"), fontFileRef);

        return descriptor;
    }

    /// <summary>Builds the embedded font program stream: a subsetted glyf font, or a subsetted CFF/OTTO program as /FontFile3.</summary>
    public Core.PdfStream BuildFontFileStream()
    {
        if (_sfnt.IsCff)
        {
            // CFF/OTTO: subset the CFF table and reassemble the sfnt shell.
            // If the font is CID-keyed (NotSupportedException from CffSubsetter), fall back
            // to whole-font embedding so those fonts still work.
            var subsetBytes = BuildSubsetCffFont();
            var stream = new Core.PdfStream(subsetBytes);
            stream.Dictionary.Set(new Core.PdfName("Subtype"), new Core.PdfName("OpenType"));
            return stream;
        }

        var glyf = BuildSubsetFont();
        return new Core.PdfStream(glyf);
    }

    /// <summary>Builds the /ToUnicode CMap stream mapping glyph IDs back to Unicode so text stays searchable and copyable.</summary>
    public Core.PdfStream BuildToUnicodeCMap()
    {
        var cmap = BuildToUnicodeCMapContent();
        return new Core.PdfStream(Encoding.ASCII.GetBytes(cmap));
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private Core.PdfDictionary BuildCidSystemInfo() => new Core.PdfDictionary()
        .Set(new Core.PdfName("Registry"), new Core.PdfLiteralString(Encoding.ASCII.GetBytes("Adobe")))
        .Set(new Core.PdfName("Ordering"), new Core.PdfLiteralString(Encoding.ASCII.GetBytes("Identity")))
        .Set(new Core.PdfName("Supplement"), new Core.PdfInteger(0));

    private Core.PdfArray BuildFontBBox()
    {
        // Use hhea ascender/descender scaled to 1000 units
        var scale = 1000.0 / _head.UnitsPerEm;
        return new Core.PdfArray([
            new Core.PdfInteger(0),
            new Core.PdfReal(Math.Round(_hhea.Descender * scale)),
            new Core.PdfReal(Math.Round(1000 * scale)),
            new Core.PdfReal(Math.Round(_hhea.Ascender  * scale))
        ]);
    }

    private Core.PdfArray BuildWidthsArray()
    {
        // PDF CIDFont W array: [first [w1 w2 …]] or [first last w] (§9.7.4.3)
        // Use individual-glyph form for the used GIDs.
        var arr = new Core.PdfArray();
        var gids = _usedGids.Order().ToArray();
        if (gids.Length == 0) return arr;

        var i = 0;
        while (i < gids.Length)
        {
            var runStart = gids[i];
            var widthList = new Core.PdfArray();
            while (i < gids.Length)
            {
                var expected = runStart + widthList.Count;
                if (i < gids.Length && gids[i] == expected)
                {
                    widthList.Add(new Core.PdfInteger(GetAdvanceWidth((ushort)gids[i])));
                    i++;
                }
                else break;
            }
            arr.Add(new Core.PdfInteger(runStart));
            arr.Add(widthList);
        }
        return arr;
    }

    private byte[] BuildSubsetFont()
    {
        // Expand composites, then build subset glyf+loca
        var subsetter = new GlyfSubsetter(_sfnt);
        var keepSet = new HashSet<int>(_usedGids);
        subsetter.ExpandComposites(keepSet);
        var (glyfBytes, locaBytes) = subsetter.BuildSubset(keepSet);

        // Reassemble the font: copy all original tables except glyf, loca, head;
        // replace those with subset versions. head.indexToLocFormat forced to 1 (long).
        return AssembleFont(glyfBytes, locaBytes);
    }

    /// <summary>
    /// Subsets the CFF table and reassembles the full sfnt (OTTO) shell around it.
    /// Falls back to whole-font embedding when the font is CID-keyed (NotSupportedException).
    /// </summary>
    private byte[] BuildSubsetCffFont()
    {
        var cffTableBytes = _sfnt.GetTableBytes(_cffTag);
        CffFont cff;
        try
        {
            cff = CffFont.Parse(cffTableBytes);
        }
        catch (InvalidDataException)
        {
            // Malformed CFF — fall back to whole-font embedding.
            return _fontData.ToArray();
        }

        byte[] newCff;
        try
        {
            newCff = CffSubsetter.Subset(cff, _usedGids);
        }
        catch (NotSupportedException)
        {
            // CID-keyed font — fall back to whole-font embedding.
            // Mark the flag so that /BaseFont and /FontName use the untagged PostScript name
            // (ISO 32000 subset-tag semantics: the tag signals subsetting was actually done).
            _cffFellBackToWholeFont = true;
            return _fontData.ToArray();
        }

        return AssembleCffFont(newCff);
    }

    /// <summary>
    /// Reassembles the OTTO sfnt with the <c>CFF </c> table replaced by
    /// <paramref name="newCffBytes"/>.  All other tables are copied verbatim.
    /// Per-table checksums, the sfnt table directory, and the <c>head</c>
    /// <c>checkSumAdjustment</c> are all recomputed.
    /// </summary>
    private byte[] AssembleCffFont(byte[] newCffBytes)
    {
        var head = new Tag("head");

        // Tables to keep verbatim (every table except CFF )
        var tablesToKeep = _sfnt.Tables.Keys
            .Where(t => t != _cffTag)
            .OrderBy(t => t.ToString())
            .ToList();

        var tableCount = tablesToKeep.Count + 1; // +1 for CFF
        var headerSize = 12 + tableCount * 16;

        var layout = new List<(Tag tag, byte[] data)>();
        foreach (var tag in tablesToKeep)
        {
            var bytes = _sfnt.GetTableBytes(tag).ToArray();
            if (tag == head)
                // Zero checkSumAdjustment — will be recomputed below.
                bytes[8] = bytes[9] = bytes[10] = bytes[11] = 0;
            layout.Add((tag, bytes));
        }
        layout.Add((_cffTag, newCffBytes));
        layout.Sort((a, b) => string.Compare(a.tag.ToString(), b.tag.ToString(), StringComparison.Ordinal));

        // Compute per-table file offsets (4-byte aligned).
        var offset = headerSize;
        var offsets = new int[layout.Count];
        for (var i = 0; i < layout.Count; i++)
        {
            offsets[i] = offset;
            offset += layout[i].data.Length;
            var rem = offset % 4;
            if (rem != 0) offset += 4 - rem;
        }

        var ms = new MemoryStream(offset + 16);

        // Write sfnt offset table with 'OTTO' (0x4F54544F) signature.
        WriteU32(ms, 0x4F54544Fu);
        WriteU16(ms, (ushort)tableCount);
        var sr = (ushort)(FloorPow2(tableCount) * 16);
        WriteU16(ms, sr);
        WriteU16(ms, (ushort)Log2(FloorPow2(tableCount)));
        WriteU16(ms, (ushort)(tableCount * 16 - sr));

        // Write table directory.
        for (var i = 0; i < layout.Count; i++)
        {
            var (tag, data) = layout[i];
            WriteTag(ms, tag);
            WriteU32(ms, Checksum(data));
            WriteU32(ms, (uint)offsets[i]);
            WriteU32(ms, (uint)data.Length);
        }

        // Write padded table data.
        for (var i = 0; i < layout.Count; i++)
        {
            ms.Write(layout[i].data);
            var rem = (int)(ms.Position % 4);
            if (rem != 0) for (var j = 0; j < 4 - rem; j++) ms.WriteByte(0);
        }

        var fontBytes = ms.ToArray();

        // Patch head.checkSumAdjustment = 0xB1B0AFBA - checksum(entire font).
        var wholeChecksum = Checksum(fontBytes);
        var adjustment = unchecked(0xB1B0AFBAu - wholeChecksum);
        for (var i = 0; i < layout.Count; i++)
        {
            if (layout[i].tag == head)
            {
                var ho = offsets[i];
                fontBytes[ho + 8] = (byte)(adjustment >> 24);
                fontBytes[ho + 9] = (byte)(adjustment >> 16);
                fontBytes[ho + 10] = (byte)(adjustment >> 8);
                fontBytes[ho + 11] = (byte)adjustment;
                break;
            }
        }

        return fontBytes;
    }

    private byte[] AssembleFont(byte[] newGlyf, byte[] newLoca)
    {
        // Collect tables from the original, replacing glyf, loca; patching head.
        var glyf = new Tag("glyf");
        var loca = new Tag("loca");
        var head = new Tag("head");

        var tablesToKeep = _sfnt.Tables.Keys
            .Where(t => t != glyf && t != loca)
            .OrderBy(t => t.ToString())
            .ToList();

        // We'll write: offset table + table directory + table data
        var tableCount = tablesToKeep.Count + 2; // +2 for glyf, loca
        var headerSize = 12 + tableCount * 16;

        // Lay out table data at 4-byte aligned boundaries after the header
        var layout = new List<(Tag tag, byte[] data)>();
        foreach (var tag in tablesToKeep)
        {
            var bytes = _sfnt.GetTableBytes(tag).ToArray();
            if (tag == head)
            {
                // Patch indexToLocFormat to 1 (long loca)
                bytes[50] = 0; bytes[51] = 1;
                // Clear checkSumAdjustment (recalculating properly needs a full-file pass)
                bytes[8] = bytes[9] = bytes[10] = bytes[11] = 0;
            }
            layout.Add((tag, bytes));
        }
        layout.Add((glyf, newGlyf));
        layout.Add((loca, newLoca));
        layout.Sort((a, b) => string.Compare(a.tag.ToString(), b.tag.ToString(), StringComparison.Ordinal));

        var ms = new MemoryStream();
        var offset = headerSize;

        // Compute offsets
        var offsets = new int[layout.Count];
        for (var i = 0; i < layout.Count; i++)
        {
            offsets[i] = offset;
            offset += layout[i].data.Length;
            var rem = offset % 4;
            if (rem != 0) offset += 4 - rem;
        }

        // Write offset table
        WriteU32(ms, 0x00010000); // sfVersion TrueType
        WriteU16(ms, (ushort)tableCount);
        // searchRange, entrySelector, rangeShift
        var sr = (ushort)(FloorPow2(tableCount) * 16);
        WriteU16(ms, sr);
        WriteU16(ms, (ushort)Log2(FloorPow2(tableCount)));
        WriteU16(ms, (ushort)(tableCount * 16 - sr));

        // Write table directory
        for (var i = 0; i < layout.Count; i++)
        {
            var (tag, data) = layout[i];
            WriteTag(ms, tag);
            WriteU32(ms, Checksum(data));
            WriteU32(ms, (uint)offsets[i]);
            WriteU32(ms, (uint)data.Length);
        }

        // Write table data with padding
        for (var i = 0; i < layout.Count; i++)
        {
            ms.Write(layout[i].data);
            var rem = (int)(ms.Position % 4);
            if (rem != 0) for (var j = 0; j < 4 - rem; j++) ms.WriteByte(0);
        }

        var fontBytes = ms.ToArray();

        // Compute and patch checkSumAdjustment in the head table.
        // The head table checkSumAdjustment (offset 8, uint32 big-endian) must be
        // 0xB1B0AFBA - checksum(entire font).
        var wholeFileChecksum = Checksum(fontBytes);
        var adjustment = unchecked(0xB1B0AFBAu - wholeFileChecksum);

        // Find the head table entry in the directory to get its file offset
        for (var i = 0; i < layout.Count; i++)
        {
            if (layout[i].tag == head)
            {
                // offsets[i] is the offset of the head table data within fontBytes
                var headOffset = offsets[i];
                fontBytes[headOffset + 8] = (byte)(adjustment >> 24);
                fontBytes[headOffset + 9] = (byte)(adjustment >> 16);
                fontBytes[headOffset + 10] = (byte)(adjustment >> 8);
                fontBytes[headOffset + 11] = (byte)adjustment;
                break;
            }
        }

        return fontBytes;
    }

    private string BuildToUnicodeCMapContent()
    {
        var sb = new StringBuilder();
        sb.AppendLine("/CIDInit /ProcSet findresource begin");
        sb.AppendLine("12 dict begin");
        sb.AppendLine("begincmap");
        sb.AppendLine("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def");
        sb.AppendLine("/CMapName /Adobe-Identity-UCS def");
        sb.AppendLine("/CMapType 2 def");
        sb.AppendLine("1 begincodespacerange");
        sb.AppendLine("<0000> <FFFF>");
        sb.AppendLine("endcodespacerange");

        var pairs = _unicodeToGid
            .Select(kv => (gid: kv.Value, cp: kv.Key))
            .OrderBy(p => p.gid)
            .ToList();

        // Write in chunks of 100
        const int chunk = 100;
        for (var start = 0; start < pairs.Count; start += chunk)
        {
            var end = Math.Min(start + chunk, pairs.Count);
            sb.AppendLine($"{end - start} beginbfchar");
            for (var i = start; i < end; i++)
            {
                var cp = pairs[i].cp;
                string cpHex;
                if (cp <= 0xFFFF)
                {
                    // BMP: 4 hex digits
                    cpHex = $"{cp:X4}";
                }
                else
                {
                    // Supplementary: encode as UTF-16BE surrogate pair (8 hex digits)
                    cp -= 0x10000;
                    var hi = 0xD800 + (cp >> 10);
                    var lo = 0xDC00 + (cp & 0x3FF);
                    cpHex = $"{hi:X4}{lo:X4}";
                }
                sb.AppendLine($"<{pairs[i].gid:X4}> <{cpHex}>");
            }
            sb.AppendLine("endbfchar");
        }

        sb.AppendLine("endcmap");
        sb.AppendLine("CMapName currentdict /CMap defineresource pop");
        sb.AppendLine("end end");
        return sb.ToString();
    }

    internal static uint Checksum(byte[] data)
    {
        uint sum = 0;
        var i = 0;
        for (; i + 3 < data.Length; i += 4)
            sum += ((uint)data[i] << 24) | ((uint)data[i + 1] << 16) | ((uint)data[i + 2] << 8) | data[i + 3];
        // Partial last word: left-justify (zero-pad on the right).
        // e.g. [b0,b1,b2] → b0<<24 | b1<<16 | b2<<8  (not right-justified).
        var rem = data.Length - i;
        if (rem > 0)
        {
            uint last = 0;
            for (var j = 0; j < rem; j++) last |= (uint)data[i + j] << ((3 - j) * 8);
            sum += last;
        }
        return sum;
    }

    private static int FloorPow2(int n) { var r = 1; while (r * 2 <= n) r *= 2; return r; }
    private static int Log2(int n) { var r = 0; while (n > 1) { n /= 2; r++; } return r; }

    private static void WriteU16(Stream s, ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }
    private static void WriteTag(Stream s, Tag t)
    {
        var str = t.ToString();
        for (var i = 0; i < 4; i++) s.WriteByte((byte)str[i]);
    }

    private static IEnumerable<int> EnumerateCodePoints(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            var cp = char.ConvertToUtf32(text, i);
            yield return cp;
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
        }
    }
}
