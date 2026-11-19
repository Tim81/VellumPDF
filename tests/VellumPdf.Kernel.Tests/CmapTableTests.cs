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
    [InlineData(0, 10)]
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

    // Malformed format 12, alone in the font: refused with InvalidDataException, never read past
    // the table and never expanded. Every case names the rule it breaks.
    [Fact]
    public void CmapFormat12_groupCountBeyondTheDeclaredLength_isRefused()
    {
        var sub12 = BuildSubtableFormat12([(0x41, 0x41, 5)], numGroupsOverride: 0xFFFF_FFFF);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, sub12)))));

        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
    }

    [Fact]
    public void CmapFormat12_groupCountBackedByLengthButNotByTheTable_isRefused()
    {
        // The declared length matches the group count but claims far more than the font
        // holds; the first read past the buffer is the refusal.
        var sub12 = BuildSubtableFormat12([(0x41, 0x41, 5)], numGroupsOverride: 1_000_000, lengthOverride: 16 + 12_000_000);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, sub12)))));

        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
    }

    [Theory]
    [InlineData(0u, 0xFFFF_FFFFu)]      // billions of code points in one group
    [InlineData(0u, 0x110000u)]         // one past U+10FFFF
    [InlineData(0x1F600u, 0x1F5FFu)]    // end before start
    public void CmapFormat12_groupOutsideUnicodeOrInverted_isRefused(uint start, uint end)
    {
        var sub12 = BuildSubtableFormat12([(start, end, 1)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, sub12)))));

        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
    }

    /// <summary>
    /// Overlapping or unsorted groups: every group covers all of Unicode, so taking them at their
    /// word is hundreds of millions of lookups from 52 bytes of subtable. The order rule refuses
    /// the second group before it is expanded; this is the bound.
    /// </summary>
    [Theory]
    [InlineData(0u, 0u, 1u, 0x10FFFFu)]            // second starts at the first's start
    [InlineData(0x100u, 0x200u, 0x200u, 0x300u)]   // second starts at the first's end
    [InlineData(0x300u, 0x400u, 0x100u, 0x200u)]   // second is before the first
    public void CmapFormat12_overlappingOrUnsortedGroups_areRefused(uint s1, uint e1, uint s2, uint e2)
    {
        var sub12 = BuildSubtableFormat12([(s1, e1, 1), (s2, e2, 1)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, sub12)))));

        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
    }

    [Fact]
    public void CmapFormat12_groupMappingPastGlyph65535_isRefused()
    {
        var sub12 = BuildSubtableFormat12([(0x41, 0x50, 0xFFF8)]);
        var font = SfntFont.Parse(BuildFont(("cmap", BuildCmap((3, 10, sub12)))));

        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
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
