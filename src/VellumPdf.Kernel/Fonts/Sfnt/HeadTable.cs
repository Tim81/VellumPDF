// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

internal sealed class HeadTable
{
    public int   UnitsPerEm      { get; }
    public short IndexToLocFormat { get; } // 0 = short offsets, 1 = long offsets

    private HeadTable(int unitsPerEm, short indexToLocFormat)
    {
        UnitsPerEm       = unitsPerEm;
        IndexToLocFormat = indexToLocFormat;
    }

    public static HeadTable Parse(SfntFont font)
    {
        var r = font.GetTableReader(new Tag("head"));
        return new HeadTable(r.ReadU16(18), r.ReadI16(50));
    }
}
