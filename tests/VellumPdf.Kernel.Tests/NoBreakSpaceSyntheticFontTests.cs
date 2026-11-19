// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Fonts;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// The no-break-space fallback of <see cref="TrueTypeFontEmbedder"/> on fonts built here, so each case has the glyphs
/// and advances it needs. No font in the tree can stand in: the only one that lacks U+202F is Liberation Mono, whose
/// .notdef and U+00A0 advances are both 600, so a measurement of the fallback cannot tell it from .notdef there; the
/// others (Liberation Sans and Serif, DejaVu Sans, Source Sans 3) all have U+202F and U+2007 of their own.
/// </summary>
public sealed class NoBreakSpaceSyntheticFontTests
{
    private const int Space = 0x0020;
    private const int NoBreakSpace = 0x00A0;
    private const int FigureSpace = 0x2007;
    private const int NarrowNoBreakSpace = 0x202F;

    // Glyph ids, with their advances in 1000 units per em: .notdef 700, space 250, no-break space 300,
    // narrow no-break space 120, figure space 500. Every advance differs, so a fallback to the wrong glyph, or to
    // .notdef, changes a measurement.
    private const ushort Notdef = 0;
    private const ushort SpaceGlyph = 1;
    private const ushort NoBreakGlyph = 2;
    private const ushort NarrowGlyph = 3;
    private const ushort FigureGlyph = 4;
    private static readonly ushort[] _advances = [700, 250, 300, 120, 500];

    private static TrueTypeFontEmbedder Embedder(params (int CodePoint, ushort Glyph)[] cmap) =>
        new(FontWith(cmap), "TT1");

    // ── Measurement ──────────────────────────────────────────────────────────

    /// <summary>
    /// A font with a no-break space but no narrow one measures the narrow one as the no-break space: 300 units, and
    /// not .notdef's 700 or the space's 250. Drawing and measuring come through the same lookup.
    /// </summary>
    [Fact]
    public void AFontWithoutTheNarrowSpace_measuresItAsTheNoBreakSpace_notAsNotdefOrTheSpace()
    {
        var font = Embedder((Space, SpaceGlyph), (NoBreakSpace, NoBreakGlyph));

        var narrow = font.MeasureString(((char)NarrowNoBreakSpace).ToString(), 12);

        Assert.Equal(300 / 1000.0 * 12, narrow, 9);
        Assert.NotEqual(700 / 1000.0 * 12, narrow);
        Assert.NotEqual(250 / 1000.0 * 12, narrow);
        Assert.Equal(NoBreakGlyph, font.GetGlyphId(NarrowNoBreakSpace));
    }

    /// <summary>The same for the figure space.</summary>
    [Fact]
    public void AFontWithoutTheFigureSpace_measuresItAsTheNoBreakSpace()
    {
        var font = Embedder((Space, SpaceGlyph), (NoBreakSpace, NoBreakGlyph));

        Assert.Equal(300 / 1000.0 * 12, font.MeasureString(((char)FigureSpace).ToString(), 12), 9);
    }

    /// <summary>With no no-break space either, the fallback ends at the space, and at .notdef only without that.</summary>
    [Fact]
    public void AFontWithoutANoBreakSpace_fallsBackToTheSpace_andThenToNotdef()
    {
        var withSpace = Embedder((Space, SpaceGlyph));
        var withNothing = Embedder();

        Assert.Equal(SpaceGlyph, withSpace.GetGlyphId(NarrowNoBreakSpace));
        Assert.Equal(250 / 1000.0 * 12, withSpace.MeasureString(((char)NarrowNoBreakSpace).ToString(), 12), 9);
        Assert.Equal(Notdef, withNothing.GetGlyphId(NarrowNoBreakSpace));
    }

    /// <summary>A character that is not one of the two spaces gets no fallback: it stays .notdef, and measures as it.</summary>
    [Fact]
    public void AnotherMissingCharacter_stillMeasuresAsNotdef()
    {
        var font = Embedder((Space, SpaceGlyph), (NoBreakSpace, NoBreakGlyph));

        Assert.Equal(Notdef, font.GetGlyphId(0x4E00));
        Assert.Equal(700 / 1000.0 * 12, font.MeasureString("一", 12), 9);
    }

    /// <summary>A narrow or figure space the font has keeps its own glyph and its own advance.</summary>
    [Fact]
    public void ASpaceTheFontHas_keepsItsOwnGlyph_notTheNoBreakSpacesOrNotdef()
    {
        var font = Embedder(
            (Space, SpaceGlyph), (NoBreakSpace, NoBreakGlyph), (NarrowNoBreakSpace, NarrowGlyph), (FigureSpace, FigureGlyph));

        Assert.Equal(NarrowGlyph, font.GetGlyphId(NarrowNoBreakSpace));
        Assert.Equal(FigureGlyph, font.GetGlyphId(FigureSpace));
        Assert.Equal(120 / 1000.0 * 12, font.MeasureString(((char)NarrowNoBreakSpace).ToString(), 12), 9);
        Assert.Equal(500 / 1000.0 * 12, font.MeasureString(((char)FigureSpace).ToString(), 12), 9);
    }

    // ── The ToUnicode CMap ───────────────────────────────────────────────────

    /// <summary>
    /// A font that maps U+202F to the very glyph it maps U+00A0 to is not using a stand-in, so both code points are
    /// listed for it, as the font's own cmap has them. Only a code point that got a stand-in glyph is held back.
    /// </summary>
    [Fact]
    public void ANarrowSpaceTheFontMapsToTheNoBreakSpacesGlyph_isNotAStandIn_andIsListedBesideIt()
    {
        var font = Embedder((Space, SpaceGlyph), (NoBreakSpace, NoBreakGlyph), (NarrowNoBreakSpace, NoBreakGlyph));
        font.GetGlyphId(NoBreakSpace);
        font.GetGlyphId(NarrowNoBreakSpace);

        var entries = ToUnicodeEntries(font);

        Assert.Contains((NoBreakGlyph, NoBreakSpace), entries);
        Assert.Contains((NoBreakGlyph, NarrowNoBreakSpace), entries);
    }

    /// <summary>The contrast: with no U+202F of its own, the narrow space is a stand-in on the no-break space's glyph and is not listed.</summary>
    [Fact]
    public void ANarrowSpaceTheFontLacks_isAStandIn_andIsNotListedOnTheNoBreakSpacesGlyph()
    {
        var font = Embedder((Space, SpaceGlyph), (NoBreakSpace, NoBreakGlyph));
        font.GetGlyphId(NoBreakSpace);
        font.GetGlyphId(NarrowNoBreakSpace);

        var entries = ToUnicodeEntries(font);

        Assert.Contains((NoBreakGlyph, NoBreakSpace), entries);
        Assert.DoesNotContain(entries, e => e.CodePoint == NarrowNoBreakSpace);
    }

    /// <summary>
    /// Another embedder's glyphs are merged into this one when the page that used them is saved in a document that
    /// embeds the same font program. The merge carries the code points that got a stand-in with it: it takes over
    /// U+202F's glyph, so it must also take over that U+202F is not a code point that owns it.
    /// </summary>
    [Fact]
    public void AddUsageFrom_carriesTheStandInCodePointsOver_soTheMergedToUnicodeDoesNotListThem()
    {
        var target = Embedder((Space, SpaceGlyph), (NoBreakSpace, NoBreakGlyph));
        var source = Embedder((Space, SpaceGlyph), (NoBreakSpace, NoBreakGlyph));
        target.GetGlyphId(NoBreakSpace);
        source.GetGlyphId(NoBreakSpace);
        source.GetGlyphId(NarrowNoBreakSpace);

        target.AddUsageFrom(source);
        var entries = ToUnicodeEntries(target);

        Assert.Contains((NoBreakGlyph, NoBreakSpace), entries);
        Assert.DoesNotContain(entries, e => e.CodePoint == NarrowNoBreakSpace);
    }

    // ── A font built for the case ────────────────────────────────────────────

    /// <summary>
    /// The glyph-to-text entries of the font's /ToUnicode CMap as written: each <c>&lt;gid&gt; &lt;utf16be&gt;</c> line
    /// of its beginbfchar sets, as (glyph id, code point).
    /// </summary>
    private static List<(int Gid, int CodePoint)> ToUnicodeEntries(TrueTypeFontEmbedder font)
    {
        using var ms = new MemoryStream();
        font.BuildToUnicodeCMap().WriteTo(new PdfWriter(ms));
        var raw = ms.ToArray();

        var latin = Encoding.Latin1.GetString(raw);
        var start = latin.IndexOf("\nstream\n", StringComparison.Ordinal) + "\nstream\n".Length;
        var end = latin.LastIndexOf("\nendstream", StringComparison.Ordinal);
        using var inflated = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(new MemoryStream(raw, start, end - start), System.IO.Compression.CompressionMode.Decompress))
            z.CopyTo(inflated);
        var text = Encoding.Latin1.GetString(inflated.ToArray());

        // \r?: the CMap is built with AppendLine, so its lines end in CRLF on Windows.
        return Regex.Matches(text, @"^<([0-9A-F]{4})> <([0-9A-F]{4})>\r?$", RegexOptions.Multiline)
            .Select(m => (Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16)))
            .ToList();
    }

    /// <summary>
    /// A TrueType font of five glyphs with the advances above and a format 6 cmap from U+0020 to U+202F that maps
    /// just the given code points. It has no outlines, which the lookups and the CMap do not read.
    /// </summary>
    private static byte[] FontWith(params (int CodePoint, ushort Glyph)[] cmap)
    {
        var head = new byte[54];
        U16(head, 18, 1000);               // unitsPerEm
        var hhea = new byte[36];
        U16(hhea, 4, 800);
        U16(hhea, 6, 0xFCE8);
        U16(hhea, 34, (ushort)_advances.Length);
        var maxp = new byte[6];
        U16(maxp, 0, 0x0005);
        U16(maxp, 4, (ushort)_advances.Length);
        var hmtx = new byte[_advances.Length * 4];
        for (var i = 0; i < _advances.Length; i++) U16(hmtx, i * 4, _advances[i]);
        var os2 = new byte[96];
        U16(os2, 0, 4);
        U16(os2, 68, 800);
        U16(os2, 70, 0xFCE8);
        var post = new byte[32];
        U32(post, 0, 0x00020000);

        return Sfnt(
            ("head", head), ("hhea", hhea), ("maxp", maxp), ("hmtx", hmtx), ("OS/2", os2), ("post", post),
            ("name", Name()), ("cmap", Cmap(cmap)));
    }

    private static byte[] Cmap((int CodePoint, ushort Glyph)[] mapped)
    {
        const int first = Space;
        const int last = NarrowNoBreakSpace;
        var count = last - first + 1;
        var subtable = new byte[10 + (count * 2)];
        U16(subtable, 0, 6);
        U16(subtable, 2, (ushort)subtable.Length);
        U16(subtable, 6, first);
        U16(subtable, 8, (ushort)count);
        foreach (var (codePoint, glyph) in mapped) U16(subtable, 10 + ((codePoint - first) * 2), glyph);

        const int subtableOffset = 4 + 8;
        var cmap = new byte[subtableOffset + subtable.Length];
        U16(cmap, 2, 1);                   // one encoding record
        U16(cmap, 4, 3);                   // Windows
        U16(cmap, 6, 1);                   // Unicode BMP
        U32(cmap, 8, subtableOffset);
        subtable.CopyTo(cmap, subtableOffset);
        return cmap;
    }

    private static byte[] Name()
    {
        var text = Encoding.BigEndianUnicode.GetBytes("Test");
        const int stringsOffset = 6 + 12;
        var table = new byte[stringsOffset + text.Length];
        U16(table, 2, 1);
        U16(table, 4, stringsOffset);
        U16(table, 6, 3);
        U16(table, 8, 1);
        U16(table, 10, 0x0409);
        U16(table, 12, 6);
        U16(table, 14, (ushort)text.Length);
        text.CopyTo(table, stringsOffset);
        return table;
    }

    private static byte[] Sfnt(params (string Tag, byte[] Data)[] tables)
    {
        var pos = 12 + (tables.Length * 16);
        var offsets = new int[tables.Length];
        for (var i = 0; i < tables.Length; i++)
        {
            offsets[i] = pos;
            pos += (tables[i].Data.Length + 3) & ~3;
        }

        var font = new byte[pos];
        U32(font, 0, 0x00010000);
        U16(font, 4, (ushort)tables.Length);
        for (var i = 0; i < tables.Length; i++)
        {
            var record = 12 + (i * 16);
            Encoding.ASCII.GetBytes(tables[i].Tag.PadRight(4)[..4]).CopyTo(font, record);
            U32(font, record + 8, (uint)offsets[i]);
            U32(font, record + 12, (uint)tables[i].Data.Length);
            tables[i].Data.CopyTo(font, offsets[i]);
        }

        return font;
    }

    private static void U16(byte[] b, int offset, ushort value)
    {
        b[offset] = (byte)(value >> 8);
        b[offset + 1] = (byte)value;
    }

    private static void U32(byte[] b, int offset, uint value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }
}
