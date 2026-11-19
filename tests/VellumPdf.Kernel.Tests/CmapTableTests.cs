// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Fonts.Sfnt;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Unit tests for cmap subtable formats 0 (byte encoding), 6 (trimmed array) and 12 (segmented
/// coverage), and for the selection-priority logic that keeps format 4 strictly preferred over
/// formats 0 and 6.
/// All inputs are synthesised in-memory — no real font required.
/// </summary>
public sealed class CmapTableTests
{
    // ── Format 0 ────────────────────────────────────────────────────────────────

    [Fact]
    public void CmapFormat0_mapsCodePointsCorrectly()
    {
        var glyphIds = new byte[256];
        glyphIds[65] = 5;
        glyphIds[66] = 6;
        var cmap = BuildCmapFormat0(glyphIds, platform: 3, encoding: 1);
        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));
        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)5, table.GetGlyphId(65));
        Assert.Equal((ushort)6, table.GetGlyphId(66));
        Assert.Equal((ushort)0, table.GetGlyphId(99));
    }

    [Fact]
    public void CmapFormat0_zeroGlyphId_notMapped()
    {
        var glyphIds = new byte[256]; // all zero
        var cmap = BuildCmapFormat0(glyphIds, platform: 3, encoding: 1);
        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));
        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)0, table.GetGlyphId(0));
        Assert.False(table.TryGetGlyphId(0, out _));
    }

    // ── Format 6 ────────────────────────────────────────────────────────────────

    [Fact]
    public void CmapFormat6_mapsContiguousRangeCorrectly()
    {
        var cmap = BuildCmapFormat6(firstCode: 0x41, glyphIds: [10, 11, 12], platform: 3, encoding: 1);
        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));
        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)10, table.GetGlyphId(0x41));
        Assert.Equal((ushort)11, table.GetGlyphId(0x42));
        Assert.Equal((ushort)12, table.GetGlyphId(0x43));
    }

    [Fact]
    public void CmapFormat6_codePointsOutsideRange_returnZero()
    {
        var cmap = BuildCmapFormat6(firstCode: 0x41, glyphIds: [10, 11, 12], platform: 3, encoding: 1);
        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));
        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)0, table.GetGlyphId(0x40)); // just before range
        Assert.Equal((ushort)0, table.GetGlyphId(0x44)); // just after range
    }

    [Fact]
    public void CmapFormat6_zeroGlyphId_notMapped()
    {
        var cmap = BuildCmapFormat6(firstCode: 0x41, glyphIds: [10, 0, 12], platform: 3, encoding: 1);
        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));
        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)10, table.GetGlyphId(0x41));
        Assert.Equal((ushort)0, table.GetGlyphId(0x42)); // GID 0 stored
        Assert.False(table.TryGetGlyphId(0x42, out _));
        Assert.Equal((ushort)12, table.GetGlyphId(0x43));
    }

    // ── Selection priority: format 4 beats format 6 ─────────────────────────────

    [Fact]
    public void CmapSelection_format4BeatsFormat6_format4IsUsed()
    {
        // Two encoding records in the same cmap table:
        //   record 0 — format 4 (platform 3, encoding 1): 'A' → GID 1
        //   record 1 — format 6 (platform 3, encoding 0): 'A' → GID 99
        // Format 4 must win.
        var subtable4 = BuildSubtableFormat4(
        [
            (0x0041, 0x0041, 0, 0),  // 'A'→'A', delta 0, idRangeOffset 0 → GID = 'A' + delta = 65; use delta=1-65
            (0xFFFF, 0xFFFF, 1, 0),  // sentinel
        ], deltaOverride: (segIdx: 0, delta: (short)(1 - 'A')));

        var subtable6 = BuildSubtableFormat6(firstCode: 0x41, glyphIds: [99]);

        // cmap: version(0), numTables(2), then two encoding records, then both subtables.
        // The subtable offsets are relative to the start of the cmap table.
        // Layout: 4 (header) + 2*8 (records) = 20 bytes before first subtable.
        var subtable4Offset = 4 + 2 * 8; // 20
        var subtable6Offset = subtable4Offset + subtable4.Length;

        var cmap = new byte[subtable6Offset + subtable6.Length];
        WriteU16Be(cmap, 0, 0);       // version
        WriteU16Be(cmap, 2, 2);       // numTables

        // Record 0: platform 3, encoding 1 → format-4 subtable
        WriteU16Be(cmap, 4, 3);
        WriteU16Be(cmap, 6, 1);
        WriteU32Be(cmap, 8, (uint)subtable4Offset);

        // Record 1: platform 3, encoding 0 → format-6 subtable
        WriteU16Be(cmap, 12, 3);
        WriteU16Be(cmap, 14, 0);
        WriteU32Be(cmap, 16, (uint)subtable6Offset);

        subtable4.CopyTo(cmap, subtable4Offset);
        subtable6.CopyTo(cmap, subtable6Offset);

        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));
        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)1, table.GetGlyphId('A'));
    }

    // ── Symbol font (platform 3, encoding 0) with format 0 ─────────────────────

    [Fact]
    public void CmapFormat0_symbolFont_platform3Encoding0_parsesCorrectly()
    {
        // Formerly would throw NotSupportedException; now accepted at priority 1.
        var glyphIds = new byte[256];
        glyphIds[0x20] = 3; // space-like glyph
        glyphIds[0x41] = 7;
        var cmap = BuildCmapFormat0(glyphIds, platform: 3, encoding: 0);
        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));
        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)3, table.GetGlyphId(0x20));
        Assert.Equal((ushort)7, table.GetGlyphId(0x41));
    }

    // ── Truncation / malformed input ────────────────────────────────────────────

    [Fact]
    public void CmapFormat0_truncatedGlyphArray_throwsInvalidDataException()
    {
        // A format-0 subtable needs 6 (header) + 256 = 262 bytes. Truncate it to 100.
        var glyphIds = new byte[256];
        glyphIds[65] = 5;
        var full = BuildCmapFormat0(glyphIds, platform: 3, encoding: 1);

        // Truncate the table data itself to 100 bytes — the cmap header is correct but the
        // subtable is short.
        var truncated = full[..100];
        var font = SfntFont.Parse(BuildFont(("cmap", truncated)));
        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
    }

    [Fact]
    public void CmapFormat6_truncatedEntries_throwsInvalidDataException()
    {
        // Build a format-6 cmap that claims entryCount=50 but provides only a few bytes.
        // The bounds-checked reader will throw when it tries to read past the buffer.
        var firstCode = (ushort)0x41;
        ushort entryCount = 50;

        // Subtable has only 12 bytes (header + 1 entry) even though entryCount says 50.
        var shortSubtable = new byte[12];
        WriteU16Be(shortSubtable, 0, 6);            // format
        WriteU16Be(shortSubtable, 2, (ushort)shortSubtable.Length); // length (misleading — reader goes by buffer)
        WriteU16Be(shortSubtable, 4, 0);            // language
        WriteU16Be(shortSubtable, 6, firstCode);    // firstCode
        WriteU16Be(shortSubtable, 8, entryCount);   // entryCount — claims 50 but only 1 entry follows
        WriteU16Be(shortSubtable, 10, 42);           // one entry (not enough)

        var subtableOffset = 4 + 1 * 8; // 12
        var cmap = new byte[subtableOffset + shortSubtable.Length];
        WriteU16Be(cmap, 0, 0);
        WriteU16Be(cmap, 2, 1);         // numTables
        WriteU16Be(cmap, 4, 3);         // platform 3
        WriteU16Be(cmap, 6, 1);         // encoding 1
        WriteU32Be(cmap, 8, (uint)subtableOffset);
        shortSubtable.CopyTo(cmap, subtableOffset);

        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));
        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
    }

    // ── Format 12 (#595) ─────────────────────────────────────────────────────────
    //
    // Layout and ordering rules are those of the OpenType cmap chapter's format 12: a 16-byte
    // header (format, reserved, 32-bit length, 32-bit language, 32-bit numGroups) then 12-byte
    // groups sorted by startCharCode, each group's end below the next group's start.

    [Fact]
    public void CmapFormat12_onlySubtable_mapsGroupsIncludingSupplementaryPlanes()
    {
        var sub12 = BuildSubtableFormat12([(0x41, 0x43, 10), (0x1F600, 0x1F601, 100)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, sub12)))));

        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)10, table.GetGlyphId(0x41));
        Assert.Equal((ushort)12, table.GetGlyphId(0x43));
        Assert.Equal((ushort)100, table.GetGlyphId(0x1F600));
        Assert.Equal((ushort)101, table.GetGlyphId(0x1F601));
        Assert.Equal((ushort)0, table.GetGlyphId(0x44));
        Assert.Equal((ushort)0, table.GetGlyphId(0x1F602));
    }

    [Fact]
    public void CmapFormat12_gidZeroInAGroup_isNotMapped()
    {
        // A group that starts at glyph 0 maps its first code point to .notdef, which the other
        // formats leave unmapped; U+0041 must read as missing, not as an entry for glyph 0.
        var sub12 = BuildSubtableFormat12([(0x41, 0x42, 0)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, sub12)))));

        var table = CmapTable.Parse(font);

        Assert.False(table.TryGetGlyphId(0x41, out _));
        Assert.Equal((ushort)1, table.GetGlyphId(0x42));
    }

    /// <summary>
    /// Where the two subtables disagree on a BMP code point the 16-bit one wins, because 2.3.3 read
    /// only that one and a document drawn from the font must keep its glyphs. 'A' is 99 in format 4
    /// and 5 in format 12; the code point format 4 lacks still comes from format 12.
    /// </summary>
    [Fact]
    public void CmapFormat12_bothSubtables_formatFourWinsWhereTheyDisagree()
    {
        var sub4 = BuildSubtableFormat4(
            [(0x0041, 0x0041, (short)(99 - 0x41), 0), (0xFFFF, 0xFFFF, 1, 0)]);   // 'A' -> 99
        var sub12 = BuildSubtableFormat12([(0x41, 0x42, 5)]);                      // 'A' -> 5, 'B' -> 6
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 1, sub4), (3, 10, sub12)))));

        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)99, table.GetGlyphId(0x41));
        Assert.Equal((ushort)6, table.GetGlyphId(0x42));
    }

    /// <summary>
    /// The OpenType chapter says a font's 32-bit subtable should be a superset of its 16-bit one.
    /// A font that breaks that keeps what 2.3.3 mapped for it: 'B' is in format 4 only here, and
    /// still resolves.
    /// </summary>
    [Fact]
    public void CmapFormat12_codePointOnlyFormatFourMaps_isKept()
    {
        var sub4 = BuildSubtableFormat4(
            [(0x0042, 0x0042, (short)(7 - 0x42), 0), (0xFFFF, 0xFFFF, 1, 0)]);     // 'B' -> 7
        var sub12 = BuildSubtableFormat12([(0x41, 0x41, 5), (0x1F600, 0x1F600, 9)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 1, sub4), (3, 10, sub12)))));

        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)7, table.GetGlyphId(0x42));
        Assert.Equal((ushort)5, table.GetGlyphId(0x41));
        Assert.Equal((ushort)9, table.GetGlyphId(0x1F600));
    }

    [Fact]
    public void CmapFormat12_windowsFullRepertoireBeatsUnicodePlatform()
    {
        var a = BuildSubtableFormat12([(0x41, 0x41, 5)]);
        var b = BuildSubtableFormat12([(0x41, 0x41, 6)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((0, 4, b), (3, 10, a)))));

        Assert.Equal((ushort)5, CmapTable.Parse(font).GetGlyphId(0x41));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(3, 10)]
    public void CmapFormat12_acceptedRecords_load(int platform, int encoding)
    {
        var sub12 = BuildSubtableFormat12([(0x41, 0x41, 5)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap(((ushort)platform, (ushort)encoding, sub12)))));

        Assert.Equal((ushort)5, CmapTable.Parse(font).GetGlyphId(0x41));
    }

    /// <summary>
    /// A format 12 subtable under a record that is not a Unicode full-repertoire one (here the
    /// Windows BMP record) is not read, and with nothing else to use the font is refused as it
    /// was: NotSupportedException.
    /// </summary>
    [Fact]
    public void CmapFormat12_underAnUnrelatedRecord_isNotUsed()
    {
        var sub12 = BuildSubtableFormat12([(0x41, 0x41, 5)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 1, sub12)))));

        Assert.Throws<NotSupportedException>(() => CmapTable.Parse(font));
    }

    // Malformed format 12, alone in the font: refused with NotSupportedException, the type 2.3.3
    // threw for every such font, and never read past the table or expanded. Each case is built so
    // that it passes every guard but the one it names, and asserts that guard's message, so
    // removing the guard fails the test instead of another guard catching the file.

    /// <summary>The refusal a sole malformed format 12 subtable gets, and the rule it names.</summary>
    private static void AssertRefusedAs(byte[] fontBytes, string rule)
    {
        var ex = Assert.Throws<NotSupportedException>(() => CmapTable.Parse(SfntFont.Parse(fontBytes)));
        Assert.Contains("only Unicode cmap subtable is format 12 and it is malformed", ex.Message);
        Assert.Contains(rule, ex.Message);
        Assert.IsType<InvalidDataException>(ex.InnerException);
    }

    /// <summary>
    /// The declared length is what makes numGroups a count the file backs. Here the groups are all
    /// in the buffer (two real groups, passing every other guard) but the subtable declares a
    /// length that holds one: only the length check can refuse it.
    /// </summary>
    [Fact]
    public void CmapFormat12_groupCountBeyondTheDeclaredLength_isRefused()
    {
        var sub12 = BuildSubtableFormat12([(0x41, 0x41, 5), (0x43, 0x43, 6)], lengthOverride: 16 + 12);
        var font = BuildFont(("cmap", BuildCmap((3, 10, sub12))));

        AssertRefusedAs(font, "declares 2 groups but only 28 bytes");
    }

    [Fact]
    public void CmapFormat12_hugeGroupCount_isRefusedWithoutAllocatingForIt()
    {
        var sub12 = BuildSubtableFormat12([(0x41, 0x41, 5)], numGroupsOverride: 0xFFFF_FFFF);
        var font = BuildFont(("cmap", BuildCmap((3, 10, sub12))));
        AssertRefusedAs(font, "declares 4294967295 groups");   // also warms the parser up

        // The count is checked against the declared length before anything is allocated for it.
        // A 4-billion-entry dictionary or group array would be tens of gigabytes; the bound is far
        // above the few kilobytes the refusal needs and far below what allocating for the count costs.
        var before = GC.GetAllocatedBytesForCurrentThread();
        AssertRefusedAs(font, "declares 4294967295 groups");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1_000_000, $"allocated {allocated} bytes refusing a {font.Length}-byte font");
    }

    [Fact]
    public void CmapFormat12_groupCountBackedByLengthButNotByTheTable_isRefused()
    {
        // The declared length matches the group count but claims far more than the font
        // holds; the first read past the buffer is the refusal.
        var sub12 = BuildSubtableFormat12([(0x41, 0x41, 5)], numGroupsOverride: 1_000_000, lengthOverride: 16 + 12_000_000);
        var font = BuildFont(("cmap", BuildCmap((3, 10, sub12))));

        var ex = Assert.Throws<NotSupportedException>(() => CmapTable.Parse(SfntFont.Parse(font)));
        Assert.IsType<InvalidDataException>(ex.InnerException);
    }

    /// <summary>
    /// A group that ends above U+10FFFF. It starts at the last code point and maps glyph 1, so
    /// the glyph guard and the order guard both pass it: only the Unicode-range check refuses it.
    /// </summary>
    [Fact]
    public void CmapFormat12_groupEndingAboveU10FFFF_isRefused()
    {
        var sub12 = BuildSubtableFormat12([(0x10FFFF, 0x110000, 1)]);

        AssertRefusedAs(BuildFont(("cmap", BuildCmap((3, 10, sub12)))), "group 0 covers U+10FFFF..U+110000");
    }

    /// <summary>
    /// An inverted group. Taken at its word, the unsigned span end - start wraps to a huge value
    /// and the glyph guard would refuse it with a different message, so the message pins that the
    /// inversion check ran first.
    /// </summary>
    [Fact]
    public void CmapFormat12_invertedGroup_isRefused()
    {
        var sub12 = BuildSubtableFormat12([(0x1F600, 0x1F5FF, 1)]);

        AssertRefusedAs(BuildFont(("cmap", BuildCmap((3, 10, sub12)))), "group 0 covers U+1F600..U+1F5FF");
    }

    [Fact]
    public void CmapFormat12_groupSpanningAllOfUnicodeAndBeyond_isRefusedByTheRangeCheck()
    {
        var sub12 = BuildSubtableFormat12([(0u, 0xFFFF_FFFFu, 1)]);

        AssertRefusedAs(BuildFont(("cmap", BuildCmap((3, 10, sub12)))), "covers U+0..U+FFFFFFFF");
    }

    /// <summary>
    /// Overlapping or unsorted groups. Every group here is small and maps glyphs the font holds,
    /// so the glyph guard passes them and the order rule is the only thing refusing the second
    /// group, before it is expanded.
    /// </summary>
    [Theory]
    [InlineData(0x100u, 0x110u, 0x100u, 0x120u)]   // second starts at the first's start
    [InlineData(0x100u, 0x200u, 0x200u, 0x210u)]   // second starts at the first's end
    [InlineData(0x300u, 0x310u, 0x100u, 0x110u)]   // second is before the first
    public void CmapFormat12_overlappingOrUnsortedGroups_areRefused(uint s1, uint e1, uint s2, uint e2)
    {
        var sub12 = BuildSubtableFormat12([(s1, e1, 1), (s2, e2, 1)]);

        AssertRefusedAs(BuildFont(("cmap", BuildCmap((3, 10, sub12)))), "group 1 is not after the group before it");
    }

    /// <summary>
    /// With no maxp the only bound on a glyph ID is the 16-bit field: a group whose last glyph is
    /// 65535 loads, one whose last glyph is 65536 is refused. The first group has its last glyph
    /// exactly at the boundary, so a bound off by one fails one of the two cases.
    /// </summary>
    [Fact]
    public void CmapFormat12_glyph65535Boundary_withoutMaxp()
    {
        var atLimit = BuildSubtableFormat12([(0x41, 0x50, 0xFFF0)]);       // last glyph 0xFFFF
        var pastLimit = BuildSubtableFormat12([(0x41, 0x50, 0xFFF1)]);     // last glyph 0x10000

        var table = CmapTable.Parse(SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, atLimit))))));

        Assert.Equal((ushort)0xFFFF, table.GetGlyphId(0x50));
        AssertRefusedAs(BuildFont(("cmap", BuildCmap((3, 10, pastLimit)))), "maps glyph 65536, past the largest glyph ID 65535 (the font has no readable maxp to bound it)");
    }

    /// <summary>
    /// A group whose glyph IDs reach maxp.numGlyphs is malformed: the OpenType chapter says
    /// character codes without a glyph map to glyph 0, so a glyph the font does not hold is not a
    /// mapping. numGlyphs is 100 here: a group ending at glyph 99 loads, one ending at glyph 100
    /// does not.
    /// </summary>
    [Fact]
    public void CmapFormat12_glyphReachingNumGlyphs_isMalformed()
    {
        var atLimit = BuildSubtableFormat12([(0x41, 0x44, 96)]);       // glyphs 96..99
        var reaches = BuildSubtableFormat12([(0x41, 0x44, 97)]);       // glyphs 97..100

        var table = CmapTable.Parse(SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, atLimit))), ("maxp", BuildMaxp(100)))));

        Assert.Equal((ushort)99, table.GetGlyphId(0x44));
        AssertRefusedAs(
            BuildFont(("cmap", BuildCmap((3, 10, reaches))), ("maxp", BuildMaxp(100))),
            "maps glyph 100, but the font has 100 glyph(s)");
    }

    /// <summary>
    /// Beside a usable 16-bit subtable the same group is ignored, which is what 2.3.3 did with
    /// every format 12 subtable: the code points above U+FFFF stay unmapped and the font loads.
    /// </summary>
    [Fact]
    public void CmapFormat12_glyphReachingNumGlyphs_besideFormat4_isIgnored()
    {
        var sub4 = BuildSubtableFormat4(
            [(0x0042, 0x0042, (short)(7 - 0x42), 0), (0xFFFF, 0xFFFF, 1, 0)]);
        var sub12 = BuildSubtableFormat12([(0x1F600, 0x1F603, 97)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 1, sub4), (3, 10, sub12))), ("maxp", BuildMaxp(100))));

        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)7, table.GetGlyphId(0x42));
        Assert.Equal((ushort)0, table.GetGlyphId(0x1F600));
        Assert.False(table.OnlyFormat12);
    }

    [Fact]
    public void CmapFormat12_soleSubtable_isReportedAsOnlyFormat12()
    {
        var sub12 = BuildSubtableFormat12([(0x41, 0x41, 5)]);

        Assert.True(CmapTable.Parse(SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, sub12)))))).OnlyFormat12);
    }

    /// <summary>
    /// The cost the class remarks state: 17 groups in a 220-byte subtable cover 1,114,095 code points.
    /// Each group maps 65,535 glyphs from glyph 1, the most a 16-bit glyph ID allows, so the
    /// subtable is well-formed and the parser builds every entry.
    /// </summary>
    [Fact]
    public void CmapFormat12_seventeenGroups_expandToOverAMillionEntries()
    {
        var groups = new List<(uint, uint, uint)>();
        for (uint i = 0; i < 17; i++)
            groups.Add((i * 0xFFFF, i * 0xFFFF + 0xFFFE, 1));
        var subtable = BuildSubtableFormat12([.. groups]);
        var cmap = BuildCmap((3, 10, subtable));
        Assert.Equal(220, subtable.Length);

        var table = CmapTable.Parse(SfntFont.Parse(BuildFont(("cmap", cmap))));

        var mapped = 0;
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (table.TryGetGlyphId(cp, out _)) mapped++;
        }
        Assert.Equal(1_114_095, mapped);
    }

    /// <summary>
    /// A font 2.3.3 loaded through its format 4 subtable keeps loading when its format 12 subtable
    /// is malformed: it costs the supplementary planes and nothing else. Without this, a font that
    /// worked would start throwing.
    /// </summary>
    [Fact]
    public void CmapFormat12_malformedButFormatFourPresent_fallsBackToFormatFour()
    {
        var sub4 = BuildSubtableFormat4(
            [(0x0042, 0x0042, (short)(7 - 0x42), 0), (0xFFFF, 0xFFFF, 1, 0)]);
        var sub12 = BuildSubtableFormat12([(0, 0xFFFF_FFFF, 1)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 1, sub4), (3, 10, sub12)))));

        var table = CmapTable.Parse(font);

        Assert.Equal((ushort)7, table.GetGlyphId(0x42));
        Assert.Equal((ushort)0, table.GetGlyphId(0x1F600));
    }

    // A font whose only Unicode subtable is format 12 is accepted on the strength of a subtable
    // 2.3.3 refused, so the chapter's rules on the encoding records hold it too. Each refusal below
    // is paired with the same defect beside a 16-bit subtable, which loaded on 2.3.3 and still does.

    private static byte[] Sub12(uint language = 0)
    {
        var sub = BuildSubtableFormat12([(0x41, 0x41, 1)]);
        WriteU32Be(sub, 8, language);
        return sub;
    }

    private static void AssertSoleFormat12Refused(byte[] cmapTable, string rule)
    {
        var ex = Assert.Throws<NotSupportedException>(() => CmapTable.Parse(SfntFont.Parse(BuildFont(("cmap", cmapTable)))));
        Assert.Contains("only Unicode cmap subtable is format 12", ex.Message);
        Assert.Contains(rule, ex.Message);
    }

    private static CmapTable ParseLoaded(byte[] cmapTable)
    {
        var table = CmapTable.Parse(SfntFont.Parse(BuildFont(("cmap", cmapTable))));
        Assert.Equal((ushort)1, table.GetGlyphId(0x41));
        return table;
    }

    [Fact]
    public void CmapFormat12Only_duplicateEncodingRecords_isRefused()
    {
        AssertSoleFormat12Refused(BuildCmap((3, 10, Sub12()), (3, 10, Sub12())), "platform 3 / encoding 10 / language 0 appears twice");
    }

    [Fact]
    public void CmapFormat12Only_unsortedEncodingRecords_isRefused()
    {
        AssertSoleFormat12Refused(BuildCmap((3, 10, Sub12()), (0, 4, Sub12())), "encoding records are not sorted");
        Assert.True(ParseLoaded(BuildCmap((0, 4, Sub12()), (3, 10, Sub12()))).OnlyFormat12);   // the same records in order
    }

    /// <summary>The order is platform, then encoding, then language: two Macintosh records that differ only by language must be in language order.</summary>
    [Fact]
    public void CmapFormat12Only_recordsOutOfLanguageOrder_areRefused()
    {
        static byte[] Mac(ushort language)
        {
            var mac = new byte[8];
            WriteU16Be(mac, 0, 2);   // a format this class does not read, so the font stays format-12-only
            WriteU16Be(mac, 2, 8);
            WriteU16Be(mac, 4, language);
            return mac;
        }

        AssertSoleFormat12Refused(BuildCmap((1, 0, Mac(20)), (1, 0, Mac(19)), (3, 10, Sub12())), "encoding records are not sorted");
        Assert.True(ParseLoaded(BuildCmap((1, 0, Mac(19)), (1, 0, Mac(20)), (3, 10, Sub12()))).OnlyFormat12);
    }

    [Fact]
    public void CmapFormat12Only_nonZeroReservedField_isRefusedButBesideA16BitSubtableItLoads()
    {
        var sub = Sub12();
        WriteU16Be(sub, 2, 7);
        AssertSoleFormat12Refused(BuildCmap((3, 10, sub)), "format 12 subtable for platform 3 / encoding 10 has a non-zero reserved field");
        Assert.False(ParseLoaded(BuildCmap((3, 1, BuildSubtableFormat6(0x41, [1])), (3, 10, sub))).OnlyFormat12);
    }

    /// <summary>
    /// The OpenType cmap chapter gives formats 8, 10, 12 and 13 a "uint16 reserved" field, "Reserved;
    /// set to 0". A format 8, 10 or 13 record is one this class does not read, so the font stays
    /// format-12-only and the record is only looked at. The same record with the field at 0 loads,
    /// and beside a 16-bit subtable the defect is not refused.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(13)]
    public void CmapFormat12Only_nonZeroReservedFieldInAnotherFormat_isRefusedButBesideA16BitSubtableItLoads(int format)
    {
        static byte[] Stub(int format, ushort reserved)
        {
            var stub = new byte[16];
            WriteU16Be(stub, 0, (ushort)format);
            WriteU16Be(stub, 2, reserved);
            WriteU32Be(stub, 4, 16);
            return stub;
        }

        AssertSoleFormat12Refused(
            BuildCmap((0, 3, Stub(format, 7)), (3, 10, Sub12())),
            $"format {format} subtable for platform 0 / encoding 3 has a non-zero reserved field");
        Assert.True(ParseLoaded(BuildCmap((0, 3, Stub(format, 0)), (3, 10, Sub12()))).OnlyFormat12);
        Assert.False(ParseLoaded(BuildCmap((0, 3, Stub(format, 7)), (3, 1, BuildSubtableFormat6(0x41, [1])), (3, 10, Sub12()))).OnlyFormat12);
    }

    [Fact]
    public void CmapFormat12Only_nonZeroLanguageOffMacintosh_isRefused()
    {
        AssertSoleFormat12Refused(BuildCmap((3, 10, Sub12(language: 1))), "has language 1; the OpenType cmap chapter requires 0 on every platform but Macintosh");
    }

    [Fact]
    public void CmapFormat12Only_nonZeroLanguageOnMacintosh_isAccepted()
    {
        // A Macintosh record may carry a language. Format 2 is not a subtable this class reads, so
        // the font is still format-12-only; the record is only looked at.
        var mac = new byte[8];
        WriteU16Be(mac, 0, 2);
        WriteU16Be(mac, 2, 8);
        WriteU16Be(mac, 4, 19);
        Assert.True(ParseLoaded(BuildCmap((1, 0, mac), (3, 10, Sub12()))).OnlyFormat12);
    }

    [Fact]
    public void CmapFormat12Only_platform0Encoding10Alone_isRefusedButEncoding4IsNot()
    {
        AssertSoleFormat12Refused(BuildCmap((0, 10, Sub12())), "platform 0 / encoding 10, which the OpenType cmap chapter does not define");
        Assert.True(ParseLoaded(BuildCmap((0, 4, Sub12()))).OnlyFormat12);
        Assert.True(ParseLoaded(BuildCmap((0, 4, Sub12()), (0, 10, Sub12()))).OnlyFormat12);   // (0,4) is a record the chapter defines
    }

    [Fact]
    public void CmapFormat12_sameRecordDefectsBesideA16BitSubtable_areNotRefused()
    {
        var narrow = BuildSubtableFormat6(0x41, [1]);
        // Duplicate records, records out of order, a language on a Windows record and platform 0 /
        // encoding 10 as the only format 12 record: each loaded on 2.3.3 through the 16-bit
        // subtable and must keep loading.
        Assert.False(ParseLoaded(BuildCmap((3, 1, narrow), (3, 10, Sub12()), (3, 10, Sub12()))).OnlyFormat12);
        Assert.False(ParseLoaded(BuildCmap((3, 10, Sub12()), (3, 1, narrow))).OnlyFormat12);
        Assert.False(ParseLoaded(BuildCmap((3, 1, narrow), (3, 10, Sub12(language: 1)))).OnlyFormat12);
        Assert.False(ParseLoaded(BuildCmap((0, 10, Sub12()), (3, 1, narrow))).OnlyFormat12);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Build a minimal sfnt with the given tables (mirrors MalformedInputTests.BuildFont).</summary>
    private static byte[] BuildFont(params (string Tag, byte[] Data)[] tables)
    {
        var offsets = new int[tables.Length];
        var pos = 12 + tables.Length * 16;
        for (var i = 0; i < tables.Length; i++)
        {
            offsets[i] = pos;
            pos += (tables[i].Data.Length + 3) & ~3;
        }

        var font = new byte[pos];
        WriteU32Be(font, 0, 0x00010000);
        WriteU16Be(font, 4, (ushort)tables.Length);
        for (var i = 0; i < tables.Length; i++)
        {
            var rec = 12 + i * 16;
            Encoding.ASCII.GetBytes(tables[i].Tag).CopyTo(font, rec);
            WriteU32Be(font, rec + 8, (uint)offsets[i]);
            WriteU32Be(font, rec + 12, (uint)tables[i].Data.Length);
            tables[i].Data.CopyTo(font, offsets[i]);
        }
        return font;
    }

    /// <summary>Build a cmap with a single format-0 encoding record.</summary>
    private static byte[] BuildCmapFormat0(byte[] glyphIds256, ushort platform, ushort encoding)
    {
        var subtable = new byte[6 + 256];
        WriteU16Be(subtable, 0, 0);                          // format 0
        WriteU16Be(subtable, 2, (ushort)subtable.Length);    // length
        WriteU16Be(subtable, 4, 0);                          // language
        glyphIds256.CopyTo(subtable, 6);

        var subtableOffset = 4 + 1 * 8; // cmap header(4) + one 8-byte encoding record
        var cmap = new byte[subtableOffset + subtable.Length];
        WriteU16Be(cmap, 0, 0);               // version
        WriteU16Be(cmap, 2, 1);               // numTables
        WriteU16Be(cmap, 4, platform);
        WriteU16Be(cmap, 6, encoding);
        WriteU32Be(cmap, 8, (uint)subtableOffset);
        subtable.CopyTo(cmap, subtableOffset);
        return cmap;
    }

    /// <summary>Build a cmap with a single format-6 encoding record.</summary>
    private static byte[] BuildCmapFormat6(ushort firstCode, ushort[] glyphIds, ushort platform, ushort encoding)
    {
        var subtable = new byte[10 + glyphIds.Length * 2];
        WriteU16Be(subtable, 0, 6);                          // format 6
        WriteU16Be(subtable, 2, (ushort)subtable.Length);    // length
        WriteU16Be(subtable, 4, 0);                          // language
        WriteU16Be(subtable, 6, firstCode);
        WriteU16Be(subtable, 8, (ushort)glyphIds.Length);
        for (var i = 0; i < glyphIds.Length; i++)
            WriteU16Be(subtable, 10 + i * 2, glyphIds[i]);

        var subtableOffset = 4 + 1 * 8;
        var cmap = new byte[subtableOffset + subtable.Length];
        WriteU16Be(cmap, 0, 0);
        WriteU16Be(cmap, 2, 1);
        WriteU16Be(cmap, 4, platform);
        WriteU16Be(cmap, 6, encoding);
        WriteU32Be(cmap, 8, (uint)subtableOffset);
        subtable.CopyTo(cmap, subtableOffset);
        return cmap;
    }

    /// <summary>
    /// Build a raw format-4 subtable (no cmap header). Supports an optional delta override
    /// for a specific segment so we can give 'A' a GID of 1 (delta = 1 - 65 = -64).
    /// </summary>
    private static byte[] BuildSubtableFormat4(
        (ushort End, ushort Start, short Delta, ushort RangeOffset)[] segs,
        (int segIdx, short delta)? deltaOverride = null)
    {
        var segCount = segs.Length;
        var subtable = new byte[16 + segCount * 8];
        WriteU16Be(subtable, 0, 4);
        WriteU16Be(subtable, 2, (ushort)subtable.Length);
        WriteU16Be(subtable, 6, (ushort)(segCount * 2));

        var endBase = 14;
        var startBase = endBase + segCount * 2 + 2;
        var deltaBase = startBase + segCount * 2;
        var rangeBase = deltaBase + segCount * 2;
        for (var i = 0; i < segCount; i++)
        {
            var delta = deltaOverride.HasValue && deltaOverride.Value.segIdx == i
                ? deltaOverride.Value.delta
                : segs[i].Delta;
            WriteU16Be(subtable, endBase + i * 2, segs[i].End);
            WriteU16Be(subtable, startBase + i * 2, segs[i].Start);
            WriteU16Be(subtable, deltaBase + i * 2, (ushort)delta);
            WriteU16Be(subtable, rangeBase + i * 2, segs[i].RangeOffset);
        }
        return subtable;
    }

    /// <summary>Build a raw format-6 subtable (no cmap header).</summary>
    private static byte[] BuildSubtableFormat6(ushort firstCode, ushort[] glyphIds)
    {
        var subtable = new byte[10 + glyphIds.Length * 2];
        WriteU16Be(subtable, 0, 6);
        WriteU16Be(subtable, 2, (ushort)subtable.Length);
        WriteU16Be(subtable, 4, 0);
        WriteU16Be(subtable, 6, firstCode);
        WriteU16Be(subtable, 8, (ushort)glyphIds.Length);
        for (var i = 0; i < glyphIds.Length; i++)
            WriteU16Be(subtable, 10 + i * 2, glyphIds[i]);
        return subtable;
    }

    /// <summary>
    /// Build a raw format-12 subtable (no cmap header). The overrides let a test claim a group
    /// count or a length the bytes do not back.
    /// </summary>
    private static byte[] BuildSubtableFormat12(
        (uint Start, uint End, uint StartGlyph)[] groups, uint? numGroupsOverride = null, uint? lengthOverride = null)
    {
        var subtable = new byte[16 + groups.Length * 12];
        WriteU16Be(subtable, 0, 12);
        WriteU32Be(subtable, 4, lengthOverride ?? (uint)subtable.Length);
        WriteU32Be(subtable, 12, numGroupsOverride ?? (uint)groups.Length);
        for (var i = 0; i < groups.Length; i++)
        {
            WriteU32Be(subtable, 16 + i * 12, groups[i].Start);
            WriteU32Be(subtable, 20 + i * 12, groups[i].End);
            WriteU32Be(subtable, 24 + i * 12, groups[i].StartGlyph);
        }
        return subtable;
    }

    /// <summary>A maxp table of the given glyph count (version, then numGlyphs at offset 4).</summary>
    private static byte[] BuildMaxp(ushort numGlyphs)
    {
        var maxp = new byte[6];
        WriteU32Be(maxp, 0, 0x00005000);
        WriteU16Be(maxp, 4, numGlyphs);
        return maxp;
    }

    /// <summary>A cmap table holding the given subtables, one encoding record each, in order.</summary>
    private static byte[] BuildCmap(params (ushort Platform, ushort Encoding, byte[] Subtable)[] subtables)
    {
        var headerSize = 4 + 8 * subtables.Length;
        var total = headerSize + subtables.Sum(s => s.Subtable.Length);
        var cmap = new byte[total];
        WriteU16Be(cmap, 2, (ushort)subtables.Length);
        var at = headerSize;
        for (var i = 0; i < subtables.Length; i++)
        {
            WriteU16Be(cmap, 4 + i * 8, subtables[i].Platform);
            WriteU16Be(cmap, 6 + i * 8, subtables[i].Encoding);
            WriteU32Be(cmap, 8 + i * 8, (uint)at);
            subtables[i].Subtable.CopyTo(cmap, at);
            at += subtables[i].Subtable.Length;
        }
        return cmap;
    }

    private static void WriteU16Be(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void WriteU32Be(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }
}
