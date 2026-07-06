// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Code128;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Self-check invariants for the hand-transcribed <see cref="Code128Tables"/> width table: every
/// symbol is 11 modules (six widths of 1-4 each), and the stop pattern is 13.
/// </summary>
public sealed class Code128TablesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    [InlineData(102)]
    [InlineData(106)]
    public void GetWidths_everySymbol_hasSixWidthsSummingToEleven(int value)
    {
        var widths = Code128Tables.GetWidths(value);
        Assert.Equal(6, widths.Length);
        var total = 0;
        foreach (var w in widths)
        {
            Assert.InRange(w, 1, 4);
            total += w;
        }

        Assert.Equal(11, total);
    }

    [Fact]
    public void GetWidths_allValues_sumToEleven()
    {
        for (var value = 0; value <= 106; value++)
        {
            var widths = Code128Tables.GetWidths(value);
            var total = 0;
            foreach (var w in widths) total += w;
            Assert.True(total == 11, $"Symbol {value} has widths summing to {total}, expected 11.");
        }
    }

    [Fact]
    public void GetWidths_outOfRange_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Code128Tables.GetWidths(107));
        Assert.Throws<ArgumentOutOfRangeException>(() => Code128Tables.GetWidths(-1));
    }

    [Fact]
    public void StopWidths_sumToThirteen_andEndOnATerminalBar()
    {
        Assert.Equal(7, Code128Tables.StopWidths.Length);
        var total = 0;
        foreach (var w in Code128Tables.StopWidths) total += w;
        Assert.Equal(13, total);
    }
}
