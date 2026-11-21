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

    // The code points in _unicodeToGid whose glyph is a stand-in: the font's own cmap had no glyph
    // for them and NoBreakSpaces.ResolveGlyph substituted the glyph of U+00A0 or U+0020 (#562). The
    // ToUnicode CMap must not let such a code point speak for the glyph when a code point that owns
    // it is in use.
    private readonly HashSet<int> _fallbackCodePoints = new();
    private readonly Tag _cffTag = new("CFF ");

    // Set to true when CffSubsetter throws NotSupportedException (CID-keyed font).
    // In that case we embed the whole font verbatim and must not use the subset-tagged name.
    private bool _cffFellBackToWholeFont;

    /// <summary>The resource name used to reference this font from a page's resource dictionary.</summary>
    public string ResourceName { get; }

    /// <summary>Parses the SFNT font program and prepares it for embedding under the given resource name.</summary>
    /// <exception cref="NotSupportedException">
    /// The font's <c>cmap</c> has no Unicode subtable this library reads (format 0, 4 or 6, or
    /// format 12 on platform 3 / encoding 10 or platform 0 / encoding 4, or on platform 0 /
    /// encoding 10 beside a 16-bit subtable). Also, when format 12 is the only such subtable, the
    /// font is refused if that subtable is malformed, if its encoding records repeat or are out of
    /// order, if a non-Macintosh subtable has a non-zero language, if a format 8, 10, 12 or 13
    /// subtable has a non-zero reserved field, if the only format 12 record is platform 0 /
    /// encoding 10, if a required table is missing or short, if <c>unitsPerEm</c> is 0, if a
    /// <c>loca</c> range lies outside <c>glyf</c>, if a composite glyph names a component at or
    /// past <c>numGlyphs</c>, if the composite glyphs form a cycle, or if the <c>CFF </c> table
    /// does not parse; the message names the rule.
    /// </exception>
    public TrueTypeFontEmbedder(ReadOnlyMemory<byte> fontData, string resourceName)
    {
        _sfnt = SfntFont.Parse(fontData);
        _fontData = fontData;

        _cmap = CmapTable.Parse(_sfnt);

        // A font whose only Unicode cmap subtable is format 12 was refused with
        // NotSupportedException before format 12 was read, whatever else was wrong with it. It is
        // accepted now only if it is sound everywhere this class and its subsetters read, and
        // every refusal keeps that exception type; see ParseOtherTables.
        if (_cmap.OnlyFormat12)
        {
            try
            {
                (_head, _hhea, _maxp, _name, _os2, _post, _hmtx) = ParseOtherTables(_sfnt);
                if (_head.UnitsPerEm == 0)
                    throw new InvalidDataException("head.unitsPerEm is 0, so no metric can be scaled.");
                ValidateOutlines(_sfnt);
            }
            catch (Exception ex) when (ex is not (NotSupportedException or OutOfMemoryException))
            {
                throw new NotSupportedException(
                    "font's only Unicode cmap subtable is format 12 and the font is malformed elsewhere: " + ex.Message, ex);
            }
        }
        else
        {
            (_head, _hhea, _maxp, _name, _os2, _post, _hmtx) = ParseOtherTables(_sfnt);
        }
        ResourceName = resourceName;
    }

    private static (HeadTable, HheaTable, MaxpTable, NameTable, Os2Table, PostTable, HmtxTable) ParseOtherTables(
        SfntFont sfnt)
    {
        var head = HeadTable.Parse(sfnt);
        var hhea = HheaTable.Parse(sfnt);
        var maxp = MaxpTable.Parse(sfnt);
        var name = NameTable.Parse(sfnt);
        var os2 = Os2Table.Parse(sfnt);
        var post = PostTable.Parse(sfnt);
        var hmtx = HmtxTable.Parse(sfnt, hhea, maxp.NumGlyphs);
        return (head, hhea, maxp, name, os2, post, hmtx);
    }

    // What is checked for a format-12-only font, beyond the tables ParseOtherTables reads (each of
    // which must be present and long enough): for glyf outlines, every glyph's loca range lies
    // inside the glyf table (GlyfSubsetter reads exactly this, for each glyph it keeps); for CFF
    // outlines, the CFF table parses; and no composite glyph names a component at or past numGlyphs
    // and no composite glyph reaches itself through its components. Not checked: the contents of
    // any glyph or charstring, table checksums, and the values (as opposed to the presence) of the
    // name, OS/2 and post fields; and head.unitsPerEm other than 0 is the only value check (0 makes
    // every metric non-finite and the save throw). A font that is wrong only in the unchecked
    // places is accepted.
    private static void ValidateOutlines(SfntFont sfnt)
    {
        if (sfnt.IsCff)
        {
            _ = CffFont.Parse(sfnt.GetTableBytes(new Tag("CFF ")));
            return;
        }

        var subsetter = new GlyfSubsetter(sfnt);
        var numGlyphs = MaxpTable.Parse(sfnt).NumGlyphs;
        var components = new int[numGlyphs][];
        for (var gid = 0; gid < numGlyphs; gid++)
            components[gid] = ReadComponents(gid, subsetter.GetGlyphData(gid), numGlyphs);
        RequireNoCompositeCycle(components);
    }

    // The glyf chapter: "a composite glyph description is a directed graph. This graph must be
    // acyclic, with every path through the graph leading to a simple glyph as a leaf node." A
    // depth-first walk with an explicit stack, so a chain of 65,535 composites cannot overflow the
    // call stack, and each glyph is entered once (state 1 while on the stack, 2 when finished), so
    // the work is bounded by the glyphs and components the font holds. A glyph reached while it is
    // still on the stack closes a cycle; a glyph that is its own component is the shortest one.
    private static void RequireNoCompositeCycle(int[][] components)
    {
        var state = new byte[components.Length];
        var stack = new Stack<(int Gid, int Next)>();
        for (var root = 0; root < components.Length; root++)
        {
            if (state[root] != 0)
                continue;
            state[root] = 1;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (gid, next) = stack.Pop();
                var children = components[gid];
                if (next == children.Length)
                {
                    state[gid] = 2;
                    continue;
                }

                stack.Push((gid, next + 1));
                var child = children[next];
                if (state[child] == 1)
                    throw new InvalidDataException(
                        $"composite glyph {gid} names component glyph {child}, which is already being expanded above it, so the composite glyphs form a cycle (OpenType glyf chapter: the graph of composite glyphs must be acyclic).");
                if (state[child] == 0)
                {
                    state[child] = 1;
                    stack.Push((child, 0));
                }
            }
        }
    }

    // OpenType glyf chapter (OpenType 1.9.1, learn.microsoft.com/typography/opentype/spec/glyf,
    // read 2026-11-19): a glyph whose numberOfContours is negative is composite; after the 10-byte
    // header it holds component records of "uint16 flags; uint16 glyphIndex", two arguments of 2 or
    // 4 bytes in all (ARG_1_AND_2_ARE_WORDS, 0x0001), then 2 (WE_HAVE_A_SCALE, 0x0008), 4
    // (WE_HAVE_AN_X_AND_Y_SCALE, 0x0040) or 8 (WE_HAVE_A_TWO_BY_TWO, 0x0080) bytes of transform,
    // repeated "while ( flags & MORE_COMPONENTS )" (0x0020). "glyphIndex" is "glyph index of
    // component", and glyph IDs run from 0 to numGlyphs - 1, so an index at or past numGlyphs names
    // no glyph. The records are walked here without decoding any outline. A record that runs past
    // the glyph's data ends the walk, as it does in GlyfSubsetter.ExpandComposites.
    private static int[] ReadComponents(int gid, ReadOnlySpan<byte> data, int numGlyphs)
    {
        if (data.Length < 10 || (short)((data[0] << 8) | data[1]) >= 0)
            return [];

        var components = new List<int>();
        var offset = 10;
        int flags;
        do
        {
            if (offset + 4 > data.Length)
                return [.. components];
            flags = (data[offset] << 8) | data[offset + 1];
            var component = (data[offset + 2] << 8) | data[offset + 3];
            if (component >= numGlyphs)
                throw new InvalidDataException(
                    $"composite glyph {gid} names component glyph {component}, but the font has {numGlyphs} glyph(s) (OpenType glyf chapter: glyphIndex is the glyph index of the component).");
            components.Add(component);
            offset += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            offset += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
        }
        while ((flags & 0x0020) != 0);
        return [.. components];
    }

    /// <summary>
    /// Takes over every glyph another embedder of the same font program registered, so a page that
    /// was drawn with that embedder's glyph ids finds them in this subset. Only meaningful
    /// when both embed the same font program, which the caller establishes by its hash.
    /// </summary>
    internal void AddUsageFrom(TrueTypeFontEmbedder other)
    {
        foreach (var gid in other._usedGids) _usedGids.Add(gid);
        foreach (var (codePoint, gid) in other._unicodeToGid) _unicodeToGid.TryAdd(codePoint, gid);
        foreach (var codePoint in other._fallbackCodePoints) _fallbackCodePoints.Add(codePoint);
    }

    /// <summary>Returns the glyph ID for a Unicode code point, registering it for subsetting.</summary>
    public ushort GetGlyphId(int codePoint)
    {
        if (_unicodeToGid.TryGetValue(codePoint, out var cachedGid)) return cachedGid;
        // A narrow no-break or figure space the font has no glyph for is drawn as its no-break space
        // or its space rather than as .notdef (#562). Measuring and drawing both come through here,
        // so they agree.
        var gid = NoBreakSpaces.ResolveGlyph(codePoint, _cmap.GetGlyphId);
        if (gid != 0 && NoBreakSpaces.NeedsSpaceFallback(codePoint) && _cmap.GetGlyphId(codePoint) == 0)
            _fallbackCodePoints.Add(codePoint);
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
    /// <remarks>
    /// <b>A font whose <c>head.unitsPerEm</c> is 0 is refused.</b> The call throws
    /// <see cref="ArgumentException"/> when it writes /Ascent, because every metric divides by the
    /// unit size and PDF has no token for the NaN or infinity that gives.
    /// <para>/FontBBox is the <c>head</c> table's xMin, yMin, xMax and yMax scaled to 1000 units
    /// per em, each rounded to a whole number: down for xMin and yMin, up for xMax and yMax, so the
    /// rectangle of a well-formed font still encloses what it draws. The box is the font's own, for
    /// a glyf and a CFF program alike, and a subset keeps the whole font's box. A variable font's
    /// box is the default instance's, as the font states it; other instances may exceed it.</para>
    /// <para>A box that <c>head</c> states with xMin above xMax, or yMin above yMax, is written as
    /// read: ISO 32000-2 7.9.5 defines a rectangle by two diagonally opposite corners, so the file
    /// is valid. The rounding direction follows the position in the array, not which value is
    /// larger, so the corners of such a box are not rounded away from each other.
    /// <b>Do not rely on</b> a reader treating such a box as the glyph extent, or on this library
    /// accepting an inverted <c>head</c> box: 3.0 will refuse it.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The font's <c>head.unitsPerEm</c> is 0.</exception>
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
    /// <remarks>
    /// <b>A font with no outline table is refused.</b> The call throws
    /// <see cref="InvalidOperationException"/> naming the missing table when a glyf font has no
    /// <c>glyf</c> or <c>loca</c> table, and when an OpenType font has no <c>CFF </c> table, which
    /// includes a variable font whose outlines are in a <c>CFF2</c> table. Malformed glyf data,
    /// such as a <c>loca</c> range past the end of <c>glyf</c>, throws <see
    /// cref="InvalidDataException"/>. A CID-keyed or unparseable <c>CFF </c> table does not throw:
    /// the font is embedded whole.
    /// <para><b>Do not pair</b> this program with a simple <c>/TrueType</c> font dictionary. It is
    /// written for the CIDFontType2 descendant that <see cref="BuildCidFontDictionary"/> returns:
    /// a glyf program has no <c>cmap</c> table, which a simple font needs (ISO 32000-2,
    /// 9.9.1).</para>
    /// <para>A glyf program is written without its <c>cmap</c> table, because a CIDFont maps codes
    /// to glyphs itself and 9.9.1 says that with a CIDFont dictionary the table is not needed and
    /// shall not be present. An OpenType CFF program keeps its <c>cmap</c>, because Table 124
    /// requires it there. The glyf stream carries <c>/Length1</c>, the length of the whole decoded
    /// program, as Table 125 requires for a TrueType program; the CFF stream carries none. A source
    /// font with no <c>cmap</c> table is refused when the embedder is created, not here.</para>
    /// <para><b>A variable font is embedded as its default instance when its outlines are glyf, or
    /// CFF that this library can subset.</b> When the source font has an <c>fvar</c> table, the
    /// program omits the eight tables avar, cvar, fvar, gvar, HVAR, MVAR, VVAR and STAT, so it
    /// holds the default instance alone; every other table is kept. A font without <c>fvar</c>
    /// keeps all its tables, a stray <c>gvar</c> or STAT included. The outlines, advance widths and
    /// descriptor metrics are the default instance's in either case, and no other instance can be
    /// drawn. A CID-keyed or unparseable <c>CFF </c> font is embedded whole instead, and a variable
    /// one of those keeps its variation tables.</para>
    /// <para><b>Do not rely on</b> GSUB, GPOS or GDEF being free of variation data: they are kept
    /// as they are, and a variable font's own variation data inside them still names axes that
    /// the program no longer has. A PDF processor does not use those tables to draw.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A required outline table is missing (<c>glyf</c>, <c>loca</c> or <c>CFF </c>); the message
    /// names it.
    /// </exception>
    /// <exception cref="InvalidDataException">The glyf data is malformed.</exception>
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
        var glyfStream = new Core.PdfStream(glyf);
        // ISO 32000-2, Table 125: /Length1 is required for a TrueType program and is "the entire
        // TrueType font program" after decoding, which is the byte count before the stream filter.
        glyfStream.Dictionary.Set(new Core.PdfName("Length1"), new Core.PdfInteger(glyf.Length));
        return glyfStream;
    }

    /// <summary>Builds the /ToUnicode CMap stream mapping glyph IDs back to Unicode so text stays searchable and copyable.</summary>
    /// <remarks>
    /// Every line of the CMap ends with one line feed (U+000A) on every operating system, so the
    /// inflated stream is the same on Windows, Linux and macOS for the same glyph usage. A CMap is
    /// the body of a stream, which the end-of-line rules of ISO 32000-2, 7.2.3 exclude. It is
    /// governed by 9.10.3 and 9.7.5.4, which point to Adobe Technical Note #5014; that note calls
    /// a CMap file an ASCII text file in a subset and extension of PostScript. The PostScript
    /// language reference that defines its line ends is not among the standards held for this
    /// project, so this member does not claim that a carriage return and line feed pair is
    /// invalid; the single form keeps the output reproducible. The compressed bytes can still
    /// differ between .NET runtimes. This member refuses no input and throws nothing.
    /// </remarks>
    public Core.PdfStream BuildToUnicodeCMap()
    {
        var cmap = BuildToUnicodeCMapContent();
        return new Core.PdfStream(Encoding.ASCII.GetBytes(cmap));
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    // Registry/Ordering/Supplement is the character-collection identifier Adobe TN5014 §4.2
    // ("CIDFont Resource Keys") makes required for CIDSystemInfo, per §3.2's "Registry,
    // Ordering, and Supplement entries are required in every CIDFont." This embedder never draws
    // CIDs from a registered collection — every subset assigns its own CIDs 1:1 with GIDs — so
    // "Identity" names that fact rather than any collection TN5014 actually lists.
    private Core.PdfDictionary BuildCidSystemInfo() => new Core.PdfDictionary()
        .Set(new Core.PdfName("Registry"), new Core.PdfLiteralString(Encoding.ASCII.GetBytes("Adobe")))
        .Set(new Core.PdfName("Ordering"), new Core.PdfLiteralString(Encoding.ASCII.GetBytes("Identity")))
        .Set(new Core.PdfName("Supplement"), new Core.PdfInteger(0));

    // The head table's own box (OpenType head: xMin, yMin, xMax, yMax in font units, which that
    // chapter says contains every glyph), scaled to 1000 units per em and rounded outward. ISO
    // 32000-2 Table 120 asks for the smallest rectangle enclosing the glyphs, so each edge moves
    // away from the glyphs, never into them: rounding to nearest would pull DejaVu's xMax (1793.46)
    // inside the outline. The scaling is v * 1000.0 / unitsPerEm, not v * (1000.0 / unitsPerEm), so
    // a value that scales to a whole number does so exactly. Nothing here casts a double to an
    // integer: a unitsPerEm of 0 gives NaN or infinity, which PdfReal refuses, and
    // BuildFontDescriptor has already refused that font at /Ascent, before this runs.
    private Core.PdfArray BuildFontBBox()
    {
        double Edge(double v) => v * 1000.0 / _head.UnitsPerEm;
        // + 0.0 turns the negative zero that Ceiling gives for a value in (-1, 0) into 0.
        return new Core.PdfArray([
            new Core.PdfReal(Math.Floor(Edge(_head.XMin)) + 0.0),
            new Core.PdfReal(Math.Floor(Edge(_head.YMin)) + 0.0),
            new Core.PdfReal(Math.Ceiling(Edge(_head.XMax)) + 0.0),
            new Core.PdfReal(Math.Ceiling(Edge(_head.YMax)) + 0.0)
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
            // Malformed CFF — fall back to whole-font embedding. Mark the flag so the
            // untagged PostScript name is used (a subset tag would wrongly claim subsetting).
            _cffFellBackToWholeFont = true;
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

        // Tables to keep verbatim: every table except CFF  and those ShouldCopyTable drops.
        var tablesToKeep = _sfnt.Tables.Keys
            .Where(t => t != _cffTag && ShouldCopyTable(t, _sfnt.HasTable(FvarTag), glyfPath: false))
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

    private static readonly Tag FvarTag = new("fvar");

    // The tables that make a font variable (OpenType Font Variations Overview: avar, cvar, fvar,
    // gvar, HVAR, MVAR, VVAR; "if they were to be removed from the font or ignored, the remaining
    // data would comprise a complete font for the default instance") plus STAT, which only names
    // the axes. Adding a later variation table is one entry here.
    private static readonly HashSet<Tag> VariationTables =
    [
        new("avar"), new("cvar"), new("fvar"), new("gvar"), new("HVAR"), new("MVAR"), new("VVAR"), new("STAT"),
    ];

    private static readonly Tag CmapTag = new("cmap");

    /// <summary>
    /// Whether a table of the source font is copied into the embedded program, the one place that
    /// decides it for both assemblers (<paramref name="glyfPath"/> is true for the glyf path). The
    /// outline and location tables, which the callers replace with the subset's, are not asked
    /// about here. Two rules apply.
    /// <para>A font that has an <c>fvar</c> table is variable, and its variation tables are not
    /// copied: the glyf, hmtx, OS/2 and hhea tables it keeps are the default instance's, and a
    /// gvar table left beside a glyf table whose unused glyphs are emptied would describe outlines
    /// that are no longer there. A font without <c>fvar</c> keeps every table, whatever it
    /// carries.</para>
    /// <para>A glyf program never carries <c>cmap</c>. Every glyf font is written as a CIDFontType2
    /// descendant, whose /CIDToGIDMap gives the code-to-glyph mapping, and ISO 32000-2, 9.9.1 says
    /// that for a CIDFont the table is not needed and shall not be present. An OpenType CFF program
    /// keeps it, because Table 124 requires it there.</para>
    /// </summary>
    internal static bool ShouldCopyTable(Tag tag, bool sourceHasFvar, bool glyfPath) =>
        !(sourceHasFvar && VariationTables.Contains(tag)) && !(glyfPath && tag == CmapTag);

    private byte[] AssembleFont(byte[] newGlyf, byte[] newLoca)
    {
        // Collect tables from the original, replacing glyf, loca; patching head.
        var glyf = new Tag("glyf");
        var loca = new Tag("loca");
        var head = new Tag("head");

        var tablesToKeep = _sfnt.Tables.Keys
            .Where(t => t != glyf && t != loca && ShouldCopyTable(t, _sfnt.HasTable(FvarTag), glyfPath: true))
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

    private string BuildToUnicodeCMapContent() => BuildToUnicodeCMapContent(_unicodeToGid, _fallbackCodePoints);

    /// <summary>
    /// The /ToUnicode CMap for the given code point to glyph assignments, where
    /// <paramref name="fallbackCodePoints"/> are the ones that got a stand-in glyph. A static seam so
    /// a test can pass assignments no bundled font produces.
    /// </summary>
    internal static string BuildToUnicodeCMapContent(
        IReadOnlyDictionary<int, ushort> unicodeToGid, IReadOnlySet<int> fallbackCodePoints)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin").Append('\n');
        sb.Append("12 dict begin").Append('\n');
        sb.Append("begincmap").Append('\n');
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def").Append('\n');
        sb.Append("/CMapName /Adobe-Identity-UCS def").Append('\n');
        sb.Append("/CMapType 2 def").Append('\n');
        sb.Append("1 begincodespacerange").Append('\n');
        sb.Append("<0000> <FFFF>").Append('\n');
        sb.Append("endcodespacerange").Append('\n');

        // A code point that got a stand-in glyph (U+202F or U+2007 in a font without it) shares that
        // glyph with U+00A0 or U+0020. A ToUnicode CMap maps a glyph to one string, and the stand-in
        // must not take over a glyph that ordinary spaces use, or copying a space out of the PDF
        // gives a narrow no-break space (#562). So a stand-in is listed only when no code point that
        // owns the glyph is in use, and when several stand-ins share it the lowest code point is
        // listed. Glyph 0 and duplicates between code points the font really maps are left alone.
        var owned = new HashSet<ushort>();
        foreach (var (cp, gid) in unicodeToGid)
            if (!fallbackCodePoints.Contains(cp)) owned.Add(gid);

        var listedStandIn = new Dictionary<ushort, int>();
        foreach (var cp in fallbackCodePoints)
        {
            var gid = unicodeToGid[cp];
            if (owned.Contains(gid)) continue;
            if (!listedStandIn.TryGetValue(gid, out var listed) || cp < listed) listedStandIn[gid] = cp;
        }

        var pairs = unicodeToGid
            .Where(kv => !fallbackCodePoints.Contains(kv.Key)
                || (listedStandIn.TryGetValue(kv.Value, out var listed) && listed == kv.Key))
            .Select(kv => (gid: kv.Value, cp: kv.Key))
            .OrderBy(p => p.gid)
            .ToList();

        // Write in chunks of 100: Adobe TN5014 §7.4 caps beginbfchar/endbfchar at "a maximum of
        // 100 lines in each ~bfchar set" — a limit ISO 32000-2 §9.10.3 carries over unchanged by
        // pointing ToUnicode CMap syntax at this same technical note.
        const int chunk = 100;
        for (var start = 0; start < pairs.Count; start += chunk)
        {
            var end = Math.Min(start + chunk, pairs.Count);
            sb.Append($"{end - start} beginbfchar").Append('\n');
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
                sb.Append($"<{pairs[i].gid:X4}> <{cpHex}>").Append('\n');
            }
            sb.Append("endbfchar").Append('\n');
        }

        sb.Append("endcmap").Append('\n');
        sb.Append("CMapName currentdict /CMap defineresource pop").Append('\n');
        sb.Append("end end").Append('\n');
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
