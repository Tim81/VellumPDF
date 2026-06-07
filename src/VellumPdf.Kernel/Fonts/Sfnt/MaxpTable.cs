// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

internal sealed class MaxpTable
{
    public int NumGlyphs { get; }

    private MaxpTable(int numGlyphs) => NumGlyphs = numGlyphs;

    public static MaxpTable Parse(SfntFont font)
    {
        var r = font.GetTableReader(new Tag("maxp"));
        return new MaxpTable(r.ReadU16(4));
    }
}
