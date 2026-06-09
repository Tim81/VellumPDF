// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>
/// Builds a subset of a TrueType glyf font using the keep-GID / null-unused strategy:
///   • The original GID space is preserved (CIDToGIDMap /Identity stays valid).
///   • Unused glyphs are replaced with a zero-length glyph (4-byte empty outline).
///   • Composite glyphs pull in their component GIDs recursively.
/// Produces byte arrays for the subset tables that can be patched into a copy of
/// the original font to form a valid embedded TrueType stream.
/// </summary>
internal sealed class GlyfSubsetter
{
    private readonly SfntFont _font;
    private readonly HeadTable _head;
    private readonly MaxpTable _maxp;

    private readonly int[] _locaOffsets;  // long-format loca (absolute file offsets)
    private readonly byte[] _glyfData;

    public GlyfSubsetter(SfntFont font)
    {
        _font = font;
        _head = HeadTable.Parse(font);
        _maxp = MaxpTable.Parse(font);
        _locaOffsets = ReadLoca();
        _glyfData = font.GetTableBytes(new Tag("glyf")).ToArray();
    }

    private int[] ReadLoca()
    {
        var n = _maxp.NumGlyphs + 1;
        var r = _font.GetTableReader(new Tag("loca"));
        var arr = new int[n];
        if (_head.IndexToLocFormat == 0)
            for (var i = 0; i < n; i++) arr[i] = r.ReadU16(i * 2) * 2;
        else
            for (var i = 0; i < n; i++) arr[i] = (int)r.ReadU32(i * 4);
        return arr;
    }

    /// <summary>
    /// Returns the glyph data byte range for <paramref name="gid"/>.
    /// Returns empty span for zero-length glyphs (space, .notdef variants, etc.).
    /// </summary>
    public ReadOnlySpan<byte> GetGlyphData(int gid)
    {
        if (gid < 0 || gid >= _maxp.NumGlyphs) return default;
        var start = _locaOffsets[gid];
        var end = _locaOffsets[gid + 1];
        if (start < 0 || end < start || end > _glyfData.Length)
            throw new InvalidDataException(
                $"Malformed font: glyph {gid} loca range [{start}, {end}) is outside the {_glyfData.Length}-byte glyf table.");
        return start == end ? default : _glyfData.AsSpan(start, end - start);
    }

    /// <summary>
    /// Expands <paramref name="keepGids"/> to include all composite component GIDs recursively.
    /// </summary>
    public void ExpandComposites(HashSet<int> keepGids)
    {
        var queue = new Queue<int>(keepGids);
        while (queue.TryDequeue(out var gid))
        {
            var data = GetGlyphData(gid);
            if (data.Length < 2) continue;
            short numContours = (short)((data[0] << 8) | data[1]);
            if (numContours >= 0) continue; // simple glyph

            // Composite glyph — parse component GIDs (OpenType spec §Table - Composite glyph description)
            var offset = 10;
            ushort flags;
            do
            {
                if (offset + 4 > data.Length) break;
                flags = (ushort)((data[offset] << 8) | data[offset + 1]);
                var componentGid = (int)((data[offset + 2] << 8) | data[offset + 3]);
                offset += 4;

                if (keepGids.Add(componentGid))
                    queue.Enqueue(componentGid);

                // Skip arg1/arg2 based on flags
                offset += (flags & 0x0001) != 0 ? 4 : 2; // ARG_1_AND_2_ARE_WORDS
                if ((flags & 0x0008) != 0) offset += 2;   // WE_HAVE_A_SCALE
                else if ((flags & 0x0040) != 0) offset += 4; // WE_HAVE_AN_X_AND_Y_SCALE
                else if ((flags & 0x0080) != 0) offset += 8; // WE_HAVE_A_TWO_BY_TWO
            } while ((flags & 0x0020) != 0); // MORE_COMPONENTS
        }
    }

    /// <summary>
    /// Builds the subset glyf and loca table bytes for embedding.
    /// Unused GIDs are zero-length (loca[gid] == loca[gid+1], nothing written).
    /// Kept glyphs with actual outlines are padded to 4-byte alignment.
    /// </summary>
    public (byte[] GlyfBytes, byte[] LocaBytes) BuildSubset(IReadOnlySet<int> keepGids)
    {
        var newGlyf = new MemoryStream();
        var locaEntries = new int[_maxp.NumGlyphs + 1];

        for (var gid = 0; gid < _maxp.NumGlyphs; gid++)
        {
            locaEntries[gid] = (int)newGlyf.Position;
            if (!keepGids.Contains(gid))
            {
                // Unused glyph: zero-length entry (loca[gid] == loca[gid+1]).
                // Nothing is written; the next GID picks up at the same offset.
                continue;
            }
            var data = GetGlyphData(gid);
            if (data.IsEmpty)
            {
                // Zero-length kept glyph (e.g. space) — still write nothing.
                continue;
            }
            newGlyf.Write(data);
            // Pad to 4-byte boundary for kept glyphs with outline data
            var rem = (int)(newGlyf.Position % 4);
            if (rem != 0) for (var i = 0; i < 4 - rem; i++) newGlyf.WriteByte(0);
        }
        locaEntries[_maxp.NumGlyphs] = (int)newGlyf.Position;

        var locaBytes = new byte[(_maxp.NumGlyphs + 1) * 4]; // always use long loca in subset
        for (var i = 0; i <= _maxp.NumGlyphs; i++)
        {
            var v = locaEntries[i];
            locaBytes[i * 4] = (byte)(v >> 24);
            locaBytes[i * 4 + 1] = (byte)(v >> 16);
            locaBytes[i * 4 + 2] = (byte)(v >> 8);
            locaBytes[i * 4 + 3] = (byte)(v);
        }

        return (newGlyf.ToArray(), locaBytes);
    }
}
