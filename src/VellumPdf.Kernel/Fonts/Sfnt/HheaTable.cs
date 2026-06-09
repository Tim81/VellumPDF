// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

internal sealed class HheaTable
{
    public int Ascender { get; }
    public int Descender { get; }
    public int LineGap { get; }
    public int NumHMetrics { get; }

    private HheaTable(int ascender, int descender, int lineGap, int numHMetrics)
    {
        Ascender = ascender; Descender = descender;
        LineGap = lineGap; NumHMetrics = numHMetrics;
    }

    public static HheaTable Parse(SfntFont font)
    {
        var r = font.GetTableReader(new Tag("hhea"));
        return new HheaTable(r.ReadI16(4), r.ReadI16(6), r.ReadI16(8), r.ReadU16(34));
    }
}
