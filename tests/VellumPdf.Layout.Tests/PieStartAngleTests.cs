// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #546: a start angle of 1e12 or more is reduced to a turn; a smaller one is used as given, so it
/// writes the bytes 2.3.3 wrote. The expected lines below were printed by 2.3.3 (1c61fa6) for three
/// slices (1, 2, 3) with colours of red 0, 0.3 and 0.6 on a default page.
/// </summary>
public sealed class PieStartAngleTests
{
    private static string[] PathLines(double angle)
    {
        using var doc = new Document();
        doc.Add(new PieChart
        {
            StartAngle = angle,
            Slices = new[] { 1.0, 2, 3 }.Select((v, i) => new PieSlice(v, new ColorRgb(i * 0.3, 0, 0))).ToList(),
        });
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()).Split('\n')
            .Where(l => l.EndsWith(" m", StringComparison.Ordinal) || l.EndsWith(" c", StringComparison.Ordinal) || l.EndsWith(" l", StringComparison.Ordinal))
            .Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Theory]
    [InlineData(65.3, "297.64 663.89 m", "219.47227 726.25831 l")]
    [InlineData(12345.678, "297.64 663.89 m", "368.65194 593.48187 l")]
    [InlineData(1e11, "297.64 663.89 m", "334.72478 756.75937 l")]
    public void AnAngleBelowTheThreshold_writesTheBytes2_3_3Wrote(double angle, string first, string second)
    {
        var lines = PathLines(angle);

        Assert.Equal(first, lines[0]);
        Assert.Equal(second, lines[1]);
    }

    [Theory]
    [InlineData(1e12)]
    [InlineData(1e16)]
    [InlineData(1e17)]
    [InlineData(-1e16)]
    public void AnAngleFromTheThreshold_isDrawnAsItsRemainderOfATurn(double angle)
    {
        Assert.Equal(PathLines(angle % (2 * Math.PI)), PathLines(angle));
    }
}
