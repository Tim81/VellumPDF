// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// A minimal, defensive reader for the metrics of an embedded sfnt (TrueType) font program: the
/// glyph count (<c>maxp</c>), the design units per em (<c>head</c>), and per-glyph advance widths
/// (<c>hhea</c> + <c>hmtx</c>). It parses only those tables and never throws — any malformation
/// surfaces as a <see langword="null"/> parse result or a null per-glyph width.
/// </summary>
/// <remarks>
/// Authored from ISO/IEC 14496-22 (OpenType): the sfnt wrapper (§5) and the maxp/head/hhea/hmtx
/// tables. Clean-room and AOT-safe: pure big-endian byte reads, no reflection. A CFF-outline font
/// (<c>'OTTO'</c>) returns null and is left to a later slice.
/// </remarks>
internal sealed class SfntMetrics
{
    private const uint OttoTag = 0x4F54544F; // 'OTTO' — CFF outlines.

    private readonly byte[] _font;
    private readonly int _hmtxOffset;
    private readonly int _numberOfHMetrics;

    private SfntMetrics(
        byte[] font, int numGlyphs, int unitsPerEm, int hmtxOffset, int numberOfHMetrics,
        int cmapSubtableCount, bool hasSymbolCmap)
    {
        _font = font;
        NumGlyphs = numGlyphs;
        UnitsPerEm = unitsPerEm;
        _hmtxOffset = hmtxOffset;
        _numberOfHMetrics = numberOfHMetrics;
        CmapSubtableCount = cmapSubtableCount;
        HasSymbolCmap = hasSymbolCmap;
    }

    public int NumGlyphs { get; }

    public int UnitsPerEm { get; }

    /// <summary>The number of encoding subtables in the font program's <c>cmap</c> table (0 if absent).</summary>
    public int CmapSubtableCount { get; }

    /// <summary>True when the <c>cmap</c> table has a Microsoft Symbol (platform 3, encoding 0) subtable.</summary>
    public bool HasSymbolCmap { get; }

    /// <summary>The advance width of <paramref name="gid"/> scaled to the 1000-unit PDF glyph space,
    /// or <see langword="null"/> when it cannot be read.</summary>
    public int? AdvanceWidth1000(int gid)
    {
        if (gid < 0 || gid >= NumGlyphs || UnitsPerEm <= 0 || _numberOfHMetrics <= 0)
            return null;
        // For gid >= numberOfHMetrics the advance is that of the last long metric (monospaced tail).
        var index = Math.Min(gid, _numberOfHMetrics - 1);
        var at = _hmtxOffset + index * 4;
        if (at < 0 || at + 2 > _font.Length)
            return null;
        var advance = (_font[at] << 8) | _font[at + 1];
        return (int)Math.Round(advance * 1000.0 / UnitsPerEm);
    }

    public static SfntMetrics? TryParse(byte[] font)
    {
        if (font.Length < 12)
            return null;
        if (ReadU32(font, 0) == OttoTag)
            return null;

        var numTables = ReadU16(font, 4);
        int maxp = -1, head = -1, hhea = -1, hmtx = -1, cmap = -1;
        for (var i = 0; i < numTables; i++)
        {
            var record = 12 + i * 16;
            if (record + 16 > font.Length)
                return null;
            var offset = (int)ReadU32(font, record + 8);
            if (Tag(font, record, 'm', 'a', 'x', 'p')) maxp = offset;
            else if (Tag(font, record, 'h', 'e', 'a', 'd')) head = offset;
            else if (Tag(font, record, 'h', 'h', 'e', 'a')) hhea = offset;
            else if (Tag(font, record, 'h', 'm', 't', 'x')) hmtx = offset;
            else if (Tag(font, record, 'c', 'm', 'a', 'p')) cmap = offset;
        }
        if (maxp < 0 || head < 0 || hhea < 0 || hmtx < 0)
            return null;
        // Bounds are compared in 64-bit arithmetic: a table offset near int.MaxValue (read from the
        // untrusted font) would otherwise overflow `offset + len` to a negative int and slip past the
        // check, leading to an out-of-range read. TryParse must never throw (it returns null instead).
        if ((long)maxp + 6 > font.Length || (long)head + 20 > font.Length || (long)hhea + 36 > font.Length)
            return null;

        var numGlyphs = ReadU16(font, maxp + 4);
        var unitsPerEm = ReadU16(font, head + 18);
        var numberOfHMetrics = ReadU16(font, hhea + 34);
        var (cmapCount, hasSymbol) = ParseCmap(font, cmap);
        return new SfntMetrics(font, numGlyphs, unitsPerEm, hmtx, numberOfHMetrics, cmapCount, hasSymbol);
    }

    // Reads the cmap header: the count of encoding subtables and whether a Microsoft Symbol (3,0)
    // subtable is present. A missing or truncated cmap yields (0, false) rather than failing the parse.
    private static (int Count, bool HasSymbol) ParseCmap(byte[] font, int cmap)
    {
        if (cmap < 0 || (long)cmap + 4 > font.Length)
            return (0, false);
        var numSubtables = ReadU16(font, cmap + 2);
        var count = 0;
        var hasSymbol = false;
        for (var i = 0; i < numSubtables; i++)
        {
            var record = (long)cmap + 4 + i * 8;
            if (record + 8 > font.Length)
                break;
            var r = (int)record;
            count++;
            if (ReadU16(font, r) == 3 && ReadU16(font, r + 2) == 0)
                hasSymbol = true;
        }
        return (count, hasSymbol);
    }

    private static bool Tag(byte[] b, int o, char a, char c, char d, char e)
        => b[o] == (byte)a && b[o + 1] == (byte)c && b[o + 2] == (byte)d && b[o + 3] == (byte)e;

    private static ushort ReadU16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

    private static uint ReadU32(byte[] b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
}
