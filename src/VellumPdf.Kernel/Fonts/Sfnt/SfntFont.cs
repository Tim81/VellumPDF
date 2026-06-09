// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>
/// Parses the sfnt table directory and provides typed access to individual tables.
/// Covers TrueType (0x00010000) and OpenType CFF ('OTTO') flavours.
/// </summary>
internal sealed class SfntFont
{
    private readonly SfntReader _reader;
    private readonly Dictionary<Tag, SfntTableRecord> _tables;

    public uint SfVersion { get; }
    public bool IsCff { get; } // 'OTTO' → CFF outlines; else TrueType glyf

    private SfntFont(SfntReader reader, uint sfVersion, Dictionary<Tag, SfntTableRecord> tables)
    {
        _reader = reader;
        SfVersion = sfVersion;
        _tables = tables;
        IsCff = sfVersion == 0x4F54544F; // 'OTTO'
    }

    public static SfntFont Parse(ReadOnlyMemory<byte> fontData)
    {
        var r = new SfntReader(fontData);
        var sfVersion = r.ReadU32(0);
        var numTables = r.ReadU16(4);

        // The table directory (12-byte header + 16 bytes per record) must fit within the
        // font. This bounds numTables by the actual data length, so a truncated/hostile file
        // cannot drive a huge allocation or read past the buffer.
        if (12 + numTables * 16 > r.Length)
            throw new InvalidDataException(
                $"Malformed font: table directory of {numTables} record(s) exceeds the {r.Length}-byte font.");

        var tables = new Dictionary<Tag, SfntTableRecord>(numTables);
        for (var i = 0; i < numTables; i++)
        {
            var rec = ParseRecord(r, 12 + i * 16);

            // Reject records whose data range falls outside the font (negative values arise
            // when a UInt32 offset/length exceeds Int32.MaxValue and wraps).
            if (rec.Offset < 0 || rec.Length < 0 || (long)rec.Offset + rec.Length > r.Length)
                throw new InvalidDataException(
                    $"Malformed font: table '{rec.TagValue}' range [{rec.Offset}, {(long)rec.Offset + rec.Length}) " +
                    $"is outside the {r.Length}-byte font.");

            tables[rec.TagValue] = rec;
        }

        return new SfntFont(r, sfVersion, tables);
    }

    private static SfntTableRecord ParseRecord(SfntReader r, int offset) => new(
        r.ReadTag(offset),
        r.ReadU32(offset + 4),
        (int)r.ReadU32(offset + 8),
        (int)r.ReadU32(offset + 12));

    public bool HasTable(Tag tag) => _tables.ContainsKey(tag);

    public SfntTableRecord GetTable(Tag tag)
    {
        if (!_tables.TryGetValue(tag, out var rec))
            throw new InvalidOperationException($"Font is missing required table '{tag}'.");
        return rec;
    }

    public ReadOnlyMemory<byte> GetTableBytes(Tag tag)
    {
        var rec = GetTable(tag);
        return _reader.Slice(rec.Offset, rec.Length);
    }

    public SfntReader GetTableReader(Tag tag)
    {
        var rec = GetTable(tag);
        return new SfntReader(_reader.Slice(rec.Offset, rec.Length));
    }

    public SfntReader Reader => _reader;
    public IReadOnlyDictionary<Tag, SfntTableRecord> Tables => _tables;
}
