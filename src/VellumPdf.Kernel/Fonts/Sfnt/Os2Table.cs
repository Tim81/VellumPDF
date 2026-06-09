// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>Reads selected fields from the OS/2 table needed for PDF FontDescriptor.</summary>
internal sealed class Os2Table
{
    public short TypoAscender { get; }
    public short TypoDescender { get; }
    public short TypoLineGap { get; }
    public short CapHeight { get; }
    public short XHeight { get; }
    public uint FsType { get; }  // embedding flags (bits 1-3)

    private Os2Table(short asc, short desc, short gap, short cap, short x, uint fs)
    {
        TypoAscender = asc; TypoDescender = desc; TypoLineGap = gap;
        CapHeight = cap; XHeight = x; FsType = fs;
    }

    public static Os2Table Parse(SfntFont font)
    {
        var r = font.GetTableReader(new Tag("OS/2"));

        // version (uint16) is at offset 0.
        var version = r.ReadU16(0);

        short typoAsc = r.ReadI16(68);
        short typoDesc = r.ReadI16(70);
        short typoGap = r.ReadI16(72);
        uint fsType = r.ReadU16(8);

        // sCapHeight (offset 88) and sXHeight (offset 86) only exist in OS/2 version >= 2.
        short capHeight;
        short xHeight;
        if (version >= 2)
        {
            capHeight = r.ReadI16(88);
            xHeight = r.ReadI16(86);
        }
        else
        {
            // Fall back: use TypoAscender as CapHeight approximation; XHeight = 0.
            capHeight = typoAsc;
            xHeight = 0;
        }

        return new Os2Table(typoAsc, typoDesc, typoGap, capHeight, xHeight, fsType);
    }
}
