// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Fonts;
using Xunit;

namespace VellumPdf.Conformance.Tests;

public sealed class SfntMetricsTests
{
    private static void WriteRecord(byte[] font, int at, string tag, uint offset)
    {
        for (var i = 0; i < 4; i++)
            font[at + i] = (byte)tag[i];
        // checksum (4 bytes) left zero; offset (4 bytes); length (4 bytes) left zero.
        font[at + 8] = (byte)(offset >> 24);
        font[at + 9] = (byte)(offset >> 16);
        font[at + 10] = (byte)(offset >> 8);
        font[at + 11] = (byte)offset;
    }

    [Fact]
    public void TryParse_TableOffsetNearIntMaxValue_ReturnsNullWithoutThrowing()
    {
        // A crafted sfnt whose maxp table offset is just below int.MaxValue. With 32-bit bounds
        // arithmetic, `maxp + 6` overflowed to a negative int and slipped past the length guard,
        // causing an out-of-range read; TryParse must instead return null (it never throws).
        var font = new byte[12 + 4 * 16];
        font[1] = 1; // sfnt version 0x00010000 (TrueType outlines)
        font[5] = 4; // numTables = 4
        WriteRecord(font, 12, "maxp", 0x7FFFFFFD); // int.MaxValue - 2
        WriteRecord(font, 28, "head", 12);
        WriteRecord(font, 44, "hhea", 16);
        WriteRecord(font, 60, "hmtx", 20);

        var metrics = SfntMetrics.TryParse(font); // must not throw

        Assert.Null(metrics);
    }

    [Fact]
    public void TryParse_TooShortFont_ReturnsNull()
    {
        Assert.Null(SfntMetrics.TryParse(new byte[8]));
    }
}
