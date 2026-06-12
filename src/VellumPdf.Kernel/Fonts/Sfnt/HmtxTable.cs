// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>Parses the 'hmtx' table to retrieve advance widths by glyph ID.</summary>
internal sealed class HmtxTable
{
    private readonly ushort[] _advanceWidths;
    private readonly int _numGlyphs;

    private HmtxTable(ushort[] advances, int numGlyphs)
    {
        _advanceWidths = advances;
        _numGlyphs = numGlyphs;
    }

    /// <summary>Returns the advance width in design units for glyph <paramref name="gid"/>.</summary>
    public int GetAdvanceWidth(int gid)
    {
        if (_advanceWidths.Length == 0) return 0;
        if (gid < 0 || gid >= _numGlyphs) return 0;
        // Last entry repeats for glyphs beyond the hMetrics count
        var idx = Math.Min(gid, _advanceWidths.Length - 1);
        return _advanceWidths[idx];
    }

    public static HmtxTable Parse(SfntFont font, HheaTable hhea, int numGlyphs)
    {
        var r = font.GetTableReader(new Tag("hmtx"));
        var n = hhea.NumHMetrics;
        if (n == 0)
            throw new InvalidDataException("hmtx: numberOfHMetrics must be >= 1.");
        var advances = new ushort[n];
        for (var i = 0; i < n; i++)
            advances[i] = r.ReadU16(i * 4);
        return new HmtxTable(advances, numGlyphs);
    }
}
