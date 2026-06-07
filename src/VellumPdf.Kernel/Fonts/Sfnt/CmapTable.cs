// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>
/// Parses the 'cmap' table to map Unicode code points → glyph IDs.
/// Prefers platform 3 (Windows) subtable format 4 (BMP), falls back to
/// platform 0 (Unicode) format 4.
/// </summary>
internal sealed class CmapTable
{
    private readonly Dictionary<int, ushort> _map;

    private CmapTable(Dictionary<int, ushort> map) => _map = map;

    public bool TryGetGlyphId(int codePoint, out ushort glyphId) =>
        _map.TryGetValue(codePoint, out glyphId);

    public ushort GetGlyphId(int codePoint) =>
        _map.TryGetValue(codePoint, out var gid) ? gid : (ushort)0;

    public static CmapTable Parse(SfntFont font)
    {
        var r = font.GetTableReader(new Tag("cmap"));
        var version = r.ReadU16(0);
        var numTabs = r.ReadU16(2);

        // Find preferred subtable (Windows Unicode BMP = platform 3, encoding 1)
        // Fallback: Unicode platform (0), any encoding with format 4
        int best = -1; int bestPriority = -1;
        for (var i = 0; i < numTabs; i++)
        {
            var platform = r.ReadU16(4 + i * 8);
            var encoding = r.ReadU16(4 + i * 8 + 2);
            var offset = (int)r.ReadU32(4 + i * 8 + 4);
            var fmt = r.ReadU16(offset);
            if (fmt != 4) continue;

            int priority = platform == 3 && encoding == 1 ? 2 :
                           platform == 0 ? 1 : 0;
            if (priority > bestPriority) { bestPriority = priority; best = offset; }
        }

        if (best < 0)
            // Only format-4 Unicode cmap parsing is implemented. Format 12 (full Unicode)
            // and format 6/0 (older/symbol) are not yet supported.
            throw new NotSupportedException(
                "font has no supported (format 4) Unicode cmap subtable");

        return new CmapTable(ParseFormat4(r, best));
    }

    private static Dictionary<int, ushort> ParseFormat4(SfntReader r, int offset)
    {
        // Format 4: segmented mapping (ISO 32000-2 is not relevant here; see OpenType spec).
        var segCount = r.ReadU16(offset + 6) / 2;
        var endCodes = new int[segCount];
        var startCodes = new int[segCount];
        var idDeltas = new short[segCount];
        var idRangeOffsets = new ushort[segCount];
        var idRangeOffsetsPos = offset + 14 + segCount * 6 + 2; // +2 for the reservedPad field

        for (var i = 0; i < segCount; i++)
        {
            endCodes[i] = r.ReadU16(offset + 14 + i * 2);
            startCodes[i] = r.ReadU16(offset + 14 + segCount * 2 + 2 + i * 2);
            idDeltas[i] = r.ReadI16(offset + 14 + segCount * 4 + 2 + i * 2);
            idRangeOffsets[i] = r.ReadU16(idRangeOffsetsPos + i * 2);
        }

        var map = new Dictionary<int, ushort>(segCount * 16);
        for (var i = 0; i < segCount; i++)
        {
            var start = startCodes[i];
            var end = endCodes[i];
            if (start == 0xFFFF && end == 0xFFFF) break;

            for (var cp = start; cp <= end; cp++)
            {
                ushort gid;
                if (idRangeOffsets[i] == 0)
                {
                    gid = (ushort)((cp + idDeltas[i]) & 0xFFFF);
                }
                else
                {
                    var glyphIndexArrayPos =
                        idRangeOffsetsPos + i * 2 + idRangeOffsets[i] + (cp - start) * 2;
                    gid = r.ReadU16(glyphIndexArrayPos);
                    if (gid != 0) gid = (ushort)((gid + idDeltas[i]) & 0xFFFF);
                }
                if (gid != 0) map[cp] = gid;
            }
        }
        return map;
    }
}
