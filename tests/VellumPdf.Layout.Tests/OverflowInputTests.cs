// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>Inputs that are each valid but whose arithmetic together overflows: pie slice totals, pie start angles and explicit column widths.</summary>
public sealed class OverflowInputTests
{
    private static TextStyle Style() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    private static Document Page() => new()
    {
        PageSize = new PdfRectangle(0, 0, 400, 500),
        Margins = new EdgeInsets(10),
    };

    private static string Stream(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    private static int CurveCount(string stream) =>
        Regex.Matches(stream, @"(?m)^[-\d. ]+ c$").Count;

    private static PieChart TwoEqualSlices(double startAngle) => new()
    {
        Slices = [new PieSlice(1, new ColorRgb(1, 0, 0)), new PieSlice(1, new ColorRgb(0, 0, 1))],
        StartAngle = startAngle,
    };

    // ── Pie slice totals ─────────────────────────────────────────────────────

    /// <summary>Two slices of 1e308 sum to infinity and the chart has no curves; the file saves, as in 2.3.3.</summary>
    [Fact]
    public void Pie_sliceTotalThatOverflowsToInfinity_stillSaves_withNoCurvesAndNoInvalidToken()
    {
        using var doc = Page();
        doc.Add(new PieChart { Slices = [new PieSlice(1e308, ColorRgb.Black), new PieSlice(1e308, ColorRgb.White)] });

        var stream = Stream(doc);

        Assert.Equal(0, CurveCount(stream));
        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
    }

    [Fact]
    public void Pie_largeFiniteTotal_stillDrawsEveryWedge()
    {
        using var doc = Page();
        doc.Add(new PieChart { Slices = [new PieSlice(1e300, ColorRgb.Black), new PieSlice(1e300, ColorRgb.White)] });

        Assert.Equal(CurveCount(DefaultTwoSlices()), CurveCount(Stream(doc)));
    }

    /// <summary>
    /// Two slices of 8e307 total 1.6e308, which is finite, but 2 * pi * 8e307 is already infinity, so a sweep
    /// computed as <c>2 * pi * value / total</c> instead of <c>2 * pi * (value / total)</c> loses every wedge.
    /// </summary>
    [Fact]
    public void Pie_totalBelowTheLargestFinite_stillDrawsEveryWedge_whereTwoPiTimesAValueOverflows()
    {
        Assert.True(double.IsFinite(8e307 + 8e307));
        Assert.True(double.IsInfinity(2 * Math.PI * 8e307));

        using var doc = Page();
        doc.Add(new PieChart { Slices = [new PieSlice(8e307, ColorRgb.Black), new PieSlice(8e307, ColorRgb.White)] });

        var stream = Stream(doc);

        Assert.Equal(4, CurveCount(stream));
        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
    }

    private static string DefaultTwoSlices()
    {
        using var doc = Page();
        doc.Add(TwoEqualSlices(Math.PI / 2));
        return Stream(doc);
    }

    // ── Pie start angles ─────────────────────────────────────────────────────

    /// <summary>A two-slice 50/50 chart draws 4 curves (two wedges of two quarter arcs); 1e16 and 1e17 are each asserted against the ordinary angle's count, with the negative and exact-multiple cases.</summary>
    [Theory]
    [InlineData(1e15)]
    [InlineData(1e16)]
    [InlineData(1e17)]
    [InlineData(-1e16)]
    [InlineData(4 * Math.PI)]
    [InlineData(double.MaxValue)]
    public void Pie_largeStartAngle_drawsTheSameNumberOfCurvesAsAnOrdinaryOne(double angle)
    {
        var expected = CurveCount(DefaultTwoSlices());
        Assert.Equal(4, expected);

        using var doc = Page();
        doc.Add(TwoEqualSlices(angle));

        Assert.Equal(expected, CurveCount(Stream(doc)));
    }

    // An angle already within a turn is not changed, so ordinary documents write the same bytes. The
    // check that pins the bytes 2.3.3 wrote for 0, 0.5, -0.5 and pi/2 is
    // PieStartAngleTests.AnAngleBelowTheThreshold_writesTheBytes2_3_3Wrote: it draws the chart, which a
    // comparison of the angle with itself cannot do.

    [Fact]
    public void Pie_startAngleAnExactTurnApart_startsAtTheSameVertex()
    {
        using var a = Page();
        a.Add(TwoEqualSlices(0.5));
        using var b = Page();
        b.Add(TwoEqualSlices(0.5 + (2 * Math.PI)));

        var first = Regex.Match(Stream(a), @"(?m)^([-\d. ]+) l$").Groups[1].Value.Split(' ');
        var second = Regex.Match(Stream(b), @"(?m)^([-\d. ]+) l$").Groups[1].Value.Split(' ');

        Assert.Equal(double.Parse(first[0]), double.Parse(second[0]), 3);
        Assert.Equal(double.Parse(first[1]), double.Parse(second[1]), 3);
    }

    // ── Explicit table column widths ─────────────────────────────────────────

    private static string[] CellRectangles(string stream) =>
        Regex.Matches(stream, @"(?m)^[-\d.]+ [-\d.]+ [-\d.]+ [-\d.]+ re$").Select(m => m.Value).ToArray();

    private static string[] TextPositions(string stream) =>
        Regex.Matches(stream, @"(?m)^1 0 0 1 [-\d.]+ [-\d.]+ Tm$").Select(m => m.Value).ToArray();

    /// <summary>Three widths of 1e308 add up to positive infinity, so the scale ratio is zero: every column is written at width 0, and the three cells' text is drawn at the table's left edge on top of one another. The file saves with no non-finite token.</summary>
    [Fact]
    public void Table_explicitWidthsThatOverflowToInfinity_stillSave_withNoNonFiniteToken()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(1e308, 1e308, 1e308);
        table.AddRow().AddCell("a").AddCell("b").AddCell("c");
        doc.Add(table);

        var stream = Stream(doc);

        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
        Assert.Equal(["10 470 0 20 re", "10 470 0 20 re", "10 470 0 20 re"], CellRectangles(stream));
        Assert.Equal(["1 0 0 1 16 476 Tm", "1 0 0 1 16 476 Tm", "1 0 0 1 16 476 Tm"], TextPositions(stream));
    }

    /// <summary>With an auto column left, only the explicit columns are written at width 0; the auto column keeps its content width.</summary>
    [Fact]
    public void Table_explicitWidthsThatOverflowToInfinity_withAnAutoColumn_keepTheAutoColumnsContentWidth()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(1e308, 1e308);
        table.AddRow().AddCell("a").AddCell("b").AddCell("c");
        doc.Add(table);

        var stream = Stream(doc);

        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
        Assert.Equal(["10 470 0 20 re", "10 470 0 20 re", "10 470 17 20 re"], CellRectangles(stream));
        Assert.Equal(["1 0 0 1 16 476 Tm", "1 0 0 1 16 476 Tm", "1 0 0 1 16 476 Tm"], TextPositions(stream));
    }

    /// <summary>One dominant finite entry is not an overflow: it scales the others to almost nothing and still renders.</summary>
    [Fact]
    public void Table_oneDominantFiniteWidth_stillRenders()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(1e200, 60, 70);
        table.AddRow().AddCell("a").AddCell("b").AddCell("c");
        doc.Add(table);

        var stream = Stream(doc);

        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
        // The first column takes the whole 380pt of content width; the other two are written at width 0 at its right edge.
        Assert.Equal(["10 470 380 20 re", "390 470 0 20 re", "390 470 0 20 re"], CellRectangles(stream));
        Assert.Equal(["1 0 0 1 16 476 Tm", "1 0 0 1 396 476 Tm", "1 0 0 1 396 476 Tm"], TextPositions(stream));
    }

    [Fact]
    public void Table_explicitWidthsThatFit_areUnchanged()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(100, 60, 70);
        table.AddRow().AddCell("a").AddCell("b").AddCell("c");
        doc.Add(table);

        var widths = Regex.Matches(Stream(doc), @"(?m)^[-\d.]+ [-\d.]+ ([-\d.]+) [-\d.]+ re\s*$")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        // One cell rectangle per column, at the width given: 2.3.3 drew 100, 60 and 70 here.
        Assert.Equal([100.0, 60.0, 70.0], widths);
    }
}
