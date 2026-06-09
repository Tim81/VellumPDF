// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

public sealed class Standard14MetricsTests
{
    [Fact]
    public void Helvetica_space_is_278()
    {
        // Space (U+0020) in Helvetica: 278/1000 per AFM spec
        var w = Standard14Metrics.GetWidth(Standard14.Helvetica, ' ');
        Assert.Equal(278, w);
    }

    [Fact]
    public void Courier_allChars_are_600()
    {
        // Courier is monospaced — every glyph is 600 units
        foreach (var c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
        {
            var w = Standard14Metrics.GetWidth(Standard14.Courier, c);
            Assert.Equal(600, w);
        }
    }

    [Fact]
    public void MeasureString_returnsScaledWidth()
    {
        // "A" in Helvetica = 667/1000 pt at 1pt → at 12pt = 12*667/1000 = 8.004
        var w = Standard14Metrics.MeasureString(Standard14.Helvetica, "A", 12);
        Assert.Equal(12.0 * 667.0 / 1000.0, w, precision: 6);
    }
}
