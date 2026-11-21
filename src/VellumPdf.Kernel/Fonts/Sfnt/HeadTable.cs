// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

internal sealed class HeadTable
{
    public int UnitsPerEm { get; }
    public short IndexToLocFormat { get; } // 0 = short offsets, 1 = long offsets

    // The font's bounding box in font units (head offsets 36, 38, 40 and 42), as the file states
    // it.
    public short XMin { get; }
    public short YMin { get; }
    public short XMax { get; }
    public short YMax { get; }

    private HeadTable(int unitsPerEm, short indexToLocFormat, short xMin, short yMin, short xMax, short yMax)
    {
        UnitsPerEm = unitsPerEm;
        IndexToLocFormat = indexToLocFormat;
        XMin = xMin;
        YMin = yMin;
        XMax = xMax;
        YMax = yMax;
    }

    public static HeadTable Parse(SfntFont font)
    {
        var r = font.GetTableReader(new Tag("head"));
        return new HeadTable(r.ReadU16(18), r.ReadI16(50), r.ReadI16(36), r.ReadI16(38), r.ReadI16(40), r.ReadI16(42));
    }
}
