// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// A minimal, defensive reader for the glyph count of an embedded sfnt (TrueType) font program.
/// It parses only the table directory and the <c>maxp</c> table's <c>numGlyphs</c> field — the one
/// fact the glyph-presence rule needs — and never throws: any malformation returns
/// <see langword="null"/>.
/// </summary>
/// <remarks>
/// Authored from ISO/IEC 14496-22 (OpenType) §5 (the sfnt wrapper) and the <c>maxp</c> table layout.
/// Clean-room and AOT-safe: pure big-endian byte reads, no reflection. A CFF-outline font
/// (<c>'OTTO'</c>, whose glyph count lives in the CFF CharStrings INDEX) returns null and is left to
/// a later slice.
/// </remarks>
internal static class SfntGlyphCount
{
    private const uint OttoTag = 0x4F54544F; // 'OTTO' — CFF outlines.

    public static int? TryGetNumGlyphs(ReadOnlySpan<byte> font)
    {
        if (font.Length < 12)
            return null;

        var sfntVersion = ReadU32(font, 0);
        if (sfntVersion == OttoTag)
            return null; // CFF glyph count is in the CFF table, not maxp.

        var numTables = ReadU16(font, 4);
        for (var i = 0; i < numTables; i++)
        {
            var record = 12 + i * 16;
            if (record + 16 > font.Length)
                return null;

            // Table tag 'maxp'.
            if (font[record] == (byte)'m' && font[record + 1] == (byte)'a'
                && font[record + 2] == (byte)'x' && font[record + 3] == (byte)'p')
            {
                var offset = (long)ReadU32(font, record + 8);
                if (offset + 6 > font.Length)
                    return null;
                // maxp: version (4 bytes) then numGlyphs (uint16).
                return ReadU16(font, (int)offset + 4);
            }
        }
        return null;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

    private static uint ReadU32(ReadOnlySpan<byte> b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
}
