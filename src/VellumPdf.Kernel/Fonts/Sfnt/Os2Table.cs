// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>Reads selected fields from the OS/2 table needed for PDF FontDescriptor.</summary>
internal sealed class Os2Table
{
    public short TypoAscender  { get; }
    public short TypoDescender { get; }
    public short TypoLineGap   { get; }
    public short CapHeight     { get; }
    public short XHeight       { get; }
    public uint  FsType        { get; }  // embedding flags (bits 1-3)

    private Os2Table(short asc, short desc, short gap, short cap, short x, uint fs)
    {
        TypoAscender  = asc; TypoDescender = desc; TypoLineGap = gap;
        CapHeight = cap; XHeight = x; FsType = fs;
    }

    public static Os2Table Parse(SfntFont font)
    {
        var r = font.GetTableReader(new Tag("OS/2"));
        return new Os2Table(
            r.ReadI16(68), r.ReadI16(70), r.ReadI16(72),
            r.ReadI16(88), r.ReadI16(86),
            r.ReadU16(8));
    }
}
