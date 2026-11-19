// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>
/// Parses the 'cmap' table to map Unicode code points → glyph IDs.
/// Supports subtable formats 0 (byte encoding), 4 (segmented BMP), 6 (trimmed array), and
/// 12 (segmented coverage, the full Unicode range).
/// </summary>
/// <remarks>
/// A format 12 subtable on platform 3 / encoding 10, or platform 0 / encoding 4 (or 10 beside a 16-bit subtable), adds the
/// code points format 4 cannot hold, those above U+FFFF. The OpenType cmap chapter (OpenType 1.9.1,
/// learn.microsoft.com, read 2026-11-19; not held under docs/iso) names platform 3 / encoding 10
/// and platform 0 / encoding 4 for format 12. Platform 0 / encoding 10 is not in its list of
/// Unicode-platform encoding IDs (0 to 6); it is read because real fonts carry it (DejaVu Sans
/// does, with a format 12 subtable), not because the chapter lists it.
/// <para><b>The 16-bit subtable keeps every code point it maps.</b> The OpenType cmap chapter says
/// the 32-bit subtable of a font that carries both should be a superset of the 16-bit one, so for
/// a well-formed font the two agree on the BMP. A font whose tables disagree keeps the BMP mapping
/// the 16-bit subtable gives it, because releases through 2.3.3 read only that subtable and a
/// document drawn from that font must not change glyphs. Format 12 contributes the code points the
/// 16-bit subtable leaves unmapped: everything above U+FFFF, and any BMP code point the 16-bit
/// subtable lacks, so a font whose format 4 or format 6 subtable covers less of the BMP than its
/// format 12 one now draws the extra characters. The 16-bit subtable is the one 2.3.3 selected
/// (format 4 over formats 0 and 6, platform 3 / encoding 1 first).</para>
/// <para><b>A format 12 subtable is malformed</b> when a group is inverted, ends above U+10FFFF,
/// is not after the group before it, maps a glyph ID at or beyond the font's <c>maxp</c>
/// <c>numGlyphs</c> (or past 65535 when the font has no readable <c>maxp</c>), or when the group
/// count does not fit the length the subtable declares. A malformed subtable beside a 16-bit one
/// is ignored, which is how 2.3.3 loaded such a font. A malformed subtable that is the font's only
/// Unicode subtable makes <see cref="Parse"/> throw <see cref="NotSupportedException"/>, the type
/// 2.3.3 threw for every font in that position; the message names the rule, and the
/// <see cref="InvalidDataException"/> that found it is the inner exception.</para>
/// <para><b>A font whose only Unicode subtable is format 12 is held to the chapter's other
/// rules too</b>, because accepting it is new. <see cref="Parse"/> throws
/// <see cref="NotSupportedException"/> naming the rule when two encoding records share a platform,
/// encoding and language, when the records are not sorted by platform, encoding and language, when
/// a subtable on a platform other than Macintosh has a non-zero language field, when a format 8, 10,
/// 12 or 13 subtable has a non-zero reserved field, or when the only
/// format 12 record is platform 0 / encoding 10 (the chapter defines platform 0 encodings 0 to 6
/// only). A font that also has a 16-bit subtable is not held to these, as on 2.3.3.</para>
/// <para><b>Cost is bounded by the file.</b> Groups are sorted and disjoint, so a font maps at
/// most 1,114,112 code points however it is built, and a group count must fit the declared length,
/// so a subtable of 12 bytes a group costs at most what its groups hold. A group maps at most as
/// many code points as the font has glyphs, so the worst case needs a font with close to 65,535
/// of them: seventeen groups (a 220-byte subtable) over a font with no <c>maxp</c> to bound the
/// glyph IDs cover 1,114,095 code points, which builds a dictionary of that many entries,
/// 53,889,264 bytes allocated by <see cref="Parse"/>, measured with
/// <c>GC.GetAllocatedBytesForCurrentThread</c> on net10.0. The 1,114,112 code points Unicode has
/// are 17 more entries than that, and the cost is paid once per font load.</para>
/// </remarks>
internal sealed class CmapTable
{
    private readonly Dictionary<int, ushort> _map;
    private readonly Dictionary<int, ushort>? _sixteenBit;

    // Format-4 covers the Basic Multilingual Plane (0x0000-0xFFFF), so a well-formed subtable
    // maps at most 0x10000 code points. Budget 2x that and reject hostile subtables whose
    // overlapping segments would otherwise span billions of iterations.
    private const int Format4CodePointBudget = 0x20000;

    // Format 12 groups are sorted by start code and may not overlap (OpenType cmap, format 12),
    // so the codes they name are distinct and at most 0x110000 of them exist. The parser
    // enforces both rules before it expands a group, which bounds the work at that count and
    // at the number of groups the subtable's declared length has room for.
    private const int MaxCodePoint = 0x10FFFF;

    private CmapTable(Dictionary<int, ushort> map, Dictionary<int, ushort>? sixteenBit, bool onlyFormat12 = false)
    {
        _map = map;
        _sixteenBit = sixteenBit;
        OnlyFormat12 = onlyFormat12;
    }

    /// <summary>
    /// <see langword="true"/> when format 12 is the font's only Unicode subtable, so the font was
    /// accepted on the strength of a subtable 2.3.3 refused. The embedder uses it to hold such a
    /// font to a stricter standard on its other tables.
    /// </summary>
    public bool OnlyFormat12 { get; }

    public bool TryGetGlyphId(int codePoint, out ushort glyphId) =>
        _map.TryGetValue(codePoint, out glyphId);

    /// <summary>
    /// Looks the code point up in the 16-bit subtable alone, which is all releases through 2.3.3
    /// read. ISO 32000-2 9.6.5.4 resolves a non-symbolic TrueType character through the (3,1)
    /// subtable, so a check that follows that clause uses this rather than <see cref="TryGetGlyphId"/>,
    /// which also sees format 12. A font with no 16-bit subtable maps nothing here.
    /// </summary>
    public bool TryGetSixteenBitGlyphId(int codePoint, out ushort glyphId)
    {
        glyphId = 0;
        return _sixteenBit is not null && _sixteenBit.TryGetValue(codePoint, out glyphId);
    }

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
        // C3: a crafted font could claim tens of thousands of subtable records, blowing
        // through the loop and reading past the cmap header before reaching any subtable.
        // 0x4000 (16384) is far above any real font's count.
        if (numTabs > 0x4000)
            throw new InvalidDataException("cmap: implausible subtable count.");

        int best = -1; int bestPriority = -1; int bestFormat = -1;
        int wide = -1; int widePriority = -1;
        var chapterWide = false;
        for (var i = 0; i < numTabs; i++)
        {
            var platform = r.ReadU16(4 + i * 8);
            var encoding = r.ReadU16(4 + i * 8 + 2);
            var offset = (int)r.ReadU32(4 + i * 8 + 4);
            var fmt = r.ReadU16(offset);

            if (fmt == 12)
            {
                // Windows full repertoire, then Unicode platform full repertoire (encoding 4 as the
                // chapter lists it; encoding 10 because fonts in the wild use it). Format 12 under
                // any other record is not a Unicode subtable this class reads.
                var widePrio = platform == 3 && encoding == 10 ? 2 :
                               platform == 0 && (encoding == 4 || encoding == 10) ? 1 : 0;
                if (widePrio > widePriority) { widePriority = widePrio; wide = offset; }
                if (platform == 3 && encoding == 10 || platform == 0 && encoding == 4)
                    chapterWide = true;
                continue;
            }

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

        if (best < 0 && widePriority < 1)
            throw new NotSupportedException(
                "font has no supported (format 0, 4, 6, or 12) Unicode cmap subtable");

        Dictionary<int, ushort>? narrow = null;
        if (best >= 0)
        {
            narrow = bestFormat switch
            {
                0 => ParseFormat0(r, best),
                4 => ParseFormat4(r, best),
                6 => ParseFormat6(r, best),
                _ => throw new NotSupportedException($"cmap subtable format {bestFormat} is not supported"),
            };
        }

        if (widePriority < 1)
            return new CmapTable(narrow!, narrow);

        Dictionary<int, ushort> full;
        try
        {
            full = ParseFormat12(r, wide, ReadNumGlyphs(font));
        }
        catch (InvalidDataException) when (narrow is not null)
        {
            // The font loaded without this subtable before it was read, so a bad one costs the
            // supplementary planes and nothing else.
            return new CmapTable(narrow, narrow);
        }
        catch (InvalidDataException ex)
        {
            // Nothing else to read: this is a font 2.3.3 refused with NotSupportedException, and
            // it is still refused, with the same type and a message that names the rule.
            throw new NotSupportedException(
                "font's only Unicode cmap subtable is format 12 and it is malformed: " + ex.Message, ex);
        }

        if (narrow is null)
            RequireSoundRecordsForSoleFormat12(r, numTabs, chapterWide);

        if (narrow is not null)
        {
            // The 16-bit subtable wins where it maps a code point; see the class remarks.
            foreach (var (cp, gid) in narrow)
                full[cp] = gid;
        }
        return new CmapTable(full, narrow, onlyFormat12: narrow is null);
    }

    // A font whose only Unicode subtable is format 12 was refused before format 12 was read, so
    // accepting it is new, and what is new is held to the chapter's own rules (R2: input that does
    // not comply never gets the exception for bugs). Each failure is NotSupportedException, the type
    // 2.3.3 threw for every such font. Fonts that also carry a 16-bit subtable loaded on 2.3.3 and
    // are not held to these.
    //
    // OpenType cmap chapter (OpenType 1.9.1, learn.microsoft.com/typography/opentype/spec/cmap, read
    // 2026-11-19): "The encoding record entries in the 'cmap' header must be sorted first by
    // platform ID, then by platform-specific encoding ID, and then by the language field in the
    // corresponding subtable. Each platform ID, platform-specific encoding ID, and subtable
    // language combination may appear only once in the 'cmap' table." and "The language field must
    // be set to zero for all 'cmap' subtables whose platform IDs are other than Macintosh
    // (platform ID 1)." Its Unicode-platform encoding IDs are 0 to 6, so platform 0 / encoding 10
    // is not one the chapter defines; platform 3 / encoding 10 and platform 0 / encoding 4 are the
    // chapter's full-repertoire records. The format 8, 10, 12 and 13 headers have a "uint16
    // reserved" field, "Reserved; set to 0".
    private static void RequireSoundRecordsForSoleFormat12(SfntReader r, int numTabs, bool chapterWide)
    {
        if (!chapterWide)
            throw new NotSupportedException(
                "font's only Unicode cmap subtable is format 12 on platform 0 / encoding 10, which the OpenType cmap chapter does not define (its Unicode-platform encoding IDs run 0 to 6)");

        try
        {
            ulong previous = 0;
            var seen = new HashSet<ulong>();
            for (var i = 0; i < numTabs; i++)
            {
                var platform = r.ReadU16(4 + i * 8);
                var encoding = r.ReadU16(4 + i * 8 + 2);
                var offset = (int)r.ReadU32(4 + i * 8 + 4);
                var format = r.ReadU16(offset);
                // Formats 8, 10, 12 and 13 have a uint16 "reserved" field after the format, "set to 0".
                if (format is 8 or 10 or 12 or 13 && r.ReadU16(offset + 2) != 0)
                    throw new NotSupportedException(
                        $"font's only Unicode cmap subtable is format 12 and the format {format} subtable for platform {platform} / encoding {encoding} has a non-zero reserved field; the OpenType cmap chapter says to set it to 0");

                var language = format switch
                {
                    0 or 2 or 4 or 6 => r.ReadU16(offset + 4),
                    8 or 10 or 12 or 13 => r.ReadU32(offset + 8),
                    _ => 0u,   // format 14 has no language field
                };

                if (platform != 1 && language != 0)
                    throw new NotSupportedException(
                        $"font's only Unicode cmap subtable is format 12 and the record for platform {platform} / encoding {encoding} has language {language}; the OpenType cmap chapter requires 0 on every platform but Macintosh");

                var key = ((ulong)platform << 48) | ((ulong)encoding << 32) | language;
                if (!seen.Add(key))
                    throw new NotSupportedException(
                        $"font's only Unicode cmap subtable is format 12 and platform {platform} / encoding {encoding} / language {language} appears twice; the OpenType cmap chapter allows each combination once");
                if (i > 0 && key < previous)
                    throw new NotSupportedException(
                        $"font's only Unicode cmap subtable is format 12 and its encoding records are not sorted by platform, encoding and language (record {i}: platform {platform} / encoding {encoding}); the OpenType cmap chapter requires that order");
                previous = key;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new NotSupportedException(
                "font's only Unicode cmap subtable is format 12 and its encoding records cannot be read: " + ex.Message, ex);
        }
    }

    // maxp numGlyphs bounds every glyph ID a cmap may name. A font with no readable maxp has no
    // bound but the 16-bit glyph ID itself; the embedder refuses such a font at its own maxp read.
    private static int ReadNumGlyphs(SfntFont font)
    {
        try
        {
            return MaxpTable.Parse(font).NumGlyphs;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            return 0x10000;
        }
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
                    // C1: compute in long to prevent int overflow on hostile idRangeOffsets values.
                    // If the position falls outside the table SfntReader.Require will throw
                    // InvalidDataException; if it overflows int we treat the entry as missing (gid 0).
                    var glyphIndexArrayPosLong =
                        (long)idRangeOffsetsPos + i * 2 + idRangeOffsets[i] + (cp - start) * 2;
                    if (glyphIndexArrayPosLong < 0 || glyphIndexArrayPosLong + 2 > r.Length)
                    {
                        gid = 0;
                    }
                    else
                    {
                        gid = r.ReadU16((int)glyphIndexArrayPosLong);
                        if (gid != 0) gid = (ushort)((gid + idDeltas[i]) & 0xFFFF);
                    }
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
        // C2: firstCode + entryCount must not exceed the BMP (0x10000 code points).
        if (firstCode + entryCount > 0x10000)
            throw new InvalidDataException("cmap format 6: range exceeds BMP.");
        var map = new Dictionary<int, ushort>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var gid = r.ReadU16(offset + 10 + i * 2);
            if (gid != 0) map[firstCode + i] = gid;
        }
        return map;
    }

    private static Dictionary<int, ushort> ParseFormat12(SfntReader r, int offset, int numGlyphs)
    {
        // Format 12: format(2) reserved(2) length(4) language(4) numGroups(4), then numGroups
        // groups of startCharCode(4) endCharCode(4) startGlyphID(4).
        const int headerSize = 16;
        const int groupSize = 12;
        var length = r.ReadU32(offset + 4);
        var numGroups = r.ReadU32(offset + 12);

        // The groups have to fit in the length the subtable declares, which is what makes
        // numGroups a count the file can back rather than a number to allocate for.
        if ((long)numGroups * groupSize + headerSize > length)
            throw new InvalidDataException(
                $"Malformed font: cmap format-12 subtable declares {numGroups} groups but only {length} bytes.");

        var map = new Dictionary<int, ushort>();
        long previousEnd = -1;
        for (var i = 0; i < numGroups; i++)
        {
            var at = offset + headerSize + i * groupSize;
            var start = r.ReadU32(at);
            var end = r.ReadU32(at + 4);
            var startGlyph = r.ReadU32(at + 8);

            if (start > end || end > MaxCodePoint)
                throw new InvalidDataException(
                    $"Malformed font: cmap format-12 group {i} covers U+{start:X}..U+{end:X}.");
            if (start <= previousEnd)
                throw new InvalidDataException(
                    $"Malformed font: cmap format-12 group {i} is not after the group before it.");
            // The OpenType cmap chapter (OpenType 1.9.1, learn.microsoft.com, read 2026-11-19): "character
            // codes that do not correspond to any glyph in the font should be mapped to glyph index 0", so a glyph ID the font does not hold is not
            // a mapping. The last glyph of the group is the largest, and `end - start` cannot
            // wrap because the inverted-group check above has already run.
            if ((long)startGlyph + (end - start) >= numGlyphs)
                throw new InvalidDataException(
                    numGlyphs > ushort.MaxValue
                        ? $"Malformed font: cmap format-12 group {i} maps glyph {(long)startGlyph + (end - start)}, past the largest glyph ID 65535 (the font has no readable maxp to bound it)."
                        : $"Malformed font: cmap format-12 group {i} maps glyph {(long)startGlyph + (end - start)}, but the font has {numGlyphs} glyph(s).");
            previousEnd = end;

            for (var cp = start; cp <= end; cp++)
            {
                var gid = (ushort)(startGlyph + (cp - start));
                if (gid != 0) map[(int)cp] = gid;
            }
        }
        return map;
    }
}
