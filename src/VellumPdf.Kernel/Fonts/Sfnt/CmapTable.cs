// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>
/// Parses the 'cmap' table to map Unicode code points → glyph IDs.
/// Supports subtable formats 0 (byte encoding), 4 (segmented BMP), and 6 (trimmed array).
/// Format 4 is strictly preferred over formats 0 and 6.
/// </summary>
internal sealed class CmapTable
{
    private readonly Dictionary<int, ushort> _map;

    // Format-4 covers the Basic Multilingual Plane (0x0000-0xFFFF), so a well-formed subtable
    // maps at most 0x10000 code points. Budget 2x that and reject hostile subtables whose
    // overlapping segments would otherwise span billions of iterations.
    private const int Format4CodePointBudget = 0x20000;

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

        // Find preferred subtable. Format 4 beats formats 0 and 6; within each tier the
        // Windows Unicode BMP (platform 3, encoding 1) record is preferred.
        //
        // Priority table:
        //   5 — format 4 + platform 3 / encoding 1 (Windows Unicode BMP)
        //   4 — format 4 + platform 0 (Unicode platform, any encoding)
        //   3 — format 4 + anything else
        //   2 — format 0 or 6 + platform 3 / encoding 1 (Windows Unicode BMP)
        //   1 — format 0 or 6 + platform 3 / encoding 0 (Windows Symbol) OR platform 0
        //   0 — format 0 or 6 + anything else
        //  skip — any other format
        int best = -1; int bestPriority = -1; int bestFormat = -1;
        for (var i = 0; i < numTabs; i++)
        {
            var platform = r.ReadU16(4 + i * 8);
            var encoding = r.ReadU16(4 + i * 8 + 2);
            var offset = (int)r.ReadU32(4 + i * 8 + 4);
            var fmt = r.ReadU16(offset);

            int priority;
            if (fmt == 4)
            {
                priority = platform == 3 && encoding == 1 ? 5 :
                           platform == 0 ? 4 : 3;
            }
            else if (fmt == 0 || fmt == 6)
            {
                priority = platform == 3 && encoding == 1 ? 2 :
                           (platform == 3 && encoding == 0) || platform == 0 ? 1 : 0;
            }
            else
            {
                continue;
            }

            if (priority > bestPriority) { bestPriority = priority; best = offset; bestFormat = fmt; }
        }

        if (best < 0)
            // Formats 0, 4, and 6 are supported. Format 12 (full Unicode) is not yet supported.
            throw new NotSupportedException(
                "font has no supported (format 0, 4, or 6) Unicode cmap subtable");

        return bestFormat switch
        {
            0 => new CmapTable(ParseFormat0(r, best)),
            4 => new CmapTable(ParseFormat4(r, best)),
            6 => new CmapTable(ParseFormat6(r, best)),
            _ => throw new NotSupportedException($"cmap subtable format {bestFormat} is not supported"),
        };
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

        // Budget the total code-point iterations (see Format4CodePointBudget) so hostile
        // overlapping segments fail cleanly instead of hanging.
        var map = new Dictionary<int, ushort>();
        var budget = Format4CodePointBudget;
        for (var i = 0; i < segCount; i++)
        {
            var start = startCodes[i];
            var end = endCodes[i];
            if (start == 0xFFFF && end == 0xFFFF) break;

            for (var cp = start; cp <= end; cp++)
            {
                if (--budget < 0)
                    throw new InvalidDataException(
                        "Malformed font: cmap format-4 subtable describes more code points than the BMP allows.");

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

    private static Dictionary<int, ushort> ParseFormat0(SfntReader r, int offset)
    {
        // Format 0: byte-encoding table — 256 single-byte glyph IDs at offset+6.
        // Header: format(2) length(2) language(2) then glyphIdArray[256].
        var map = new Dictionary<int, ushort>();
        for (var cp = 0; cp < 256; cp++)
        {
            var gid = r.ReadU8(offset + 6 + cp);
            if (gid != 0) map[cp] = gid;
        }
        return map;
    }

    private static Dictionary<int, ushort> ParseFormat6(SfntReader r, int offset)
    {
        // Format 6: trimmed table mapping — contiguous range of code points.
        // Header: format(2) length(2) language(2) firstCode(2) entryCount(2) then glyphIdArray[entryCount].
        var firstCode = r.ReadU16(offset + 6);
        var entryCount = r.ReadU16(offset + 8);
        var map = new Dictionary<int, ushort>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var gid = r.ReadU16(offset + 10 + i * 2);
            if (gid != 0) map[firstCode + i] = gid;
        }
        return map;
    }
}
