// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Fonts.Sfnt;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Unit tests for cmap subtable formats 0 (byte encoding) and 6 (trimmed array),
/// and for the selection-priority logic that keeps format 4 strictly preferred.
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
