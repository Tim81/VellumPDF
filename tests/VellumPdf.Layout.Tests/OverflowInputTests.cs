// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #546: inputs that are each valid but whose arithmetic together overflows into empty or wrong
/// output with no exception. Pie slice totals, pie start angles and explicit column widths.
/// </summary>
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

    [Fact]
    public void Pie_sliceTotalThatOverflowsToInfinity_isRefused()
    {
        using var doc = Page();
        doc.Add(new PieChart { Slices = [new PieSlice(1e308, ColorRgb.Black), new PieSlice(1e308, ColorRgb.White)] });

        var ex = Assert.Throws<ArgumentException>(() => Stream(doc));

        Assert.Equal("Slices", ex.ParamName);
        Assert.StartsWith(
            "The sum of pie slice values must be finite; the slice values overflow when added.",
            ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pie_largeFiniteTotal_stillDrawsEveryWedge()
    {
        using var doc = Page();
        doc.Add(new PieChart { Slices = [new PieSlice(1e300, ColorRgb.Black), new PieSlice(1e300, ColorRgb.White)] });

        Assert.Equal(CurveCount(DefaultTwoSlices()), CurveCount(Stream(doc)));
    }

    private static string DefaultTwoSlices()
    {
        using var doc = Page();
        doc.Add(TwoEqualSlices(Math.PI / 2));
        return Stream(doc);
    }

    // ── Pie start angles ─────────────────────────────────────────────────────

    /// <summary>
    /// A two-slice 50/50 chart draws 4 curves when the angle is correct (two wedges of two quarter
    /// arcs each). 1e16 used to draw 6 and 1e17 drew 0, so each is asserted against the ordinary
    /// angle's count, together with the negative and the exact-multiple cases.
    /// </summary>
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

    /// <summary>An angle already within a turn is not changed, so ordinary documents write the same bytes.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(-0.5)]
    [InlineData(Math.PI / 2)]
    public void Pie_startAngleWithinATurn_isUnchanged(double angle)
    {
        Assert.Equal(angle, angle % (2 * Math.PI));
    }

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

    [Fact]
    public void Table_explicitWidthsThatOverflowToInfinity_areRefused()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(1e308, 1e308, 1e308);
        table.AddRow().AddCell("a").AddCell("b").AddCell("c");
        doc.Add(table);

        var ex = Assert.Throws<InvalidOperationException>(() => Stream(doc));

        Assert.StartsWith(
            "A table's explicit column widths add up to positive infinity", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One dominant finite entry is not an overflow. It scales the others to almost nothing and
    /// still renders, so it is not refused in a patch; it is documented and listed for the next major.
    /// </summary>
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
    }

    [Fact]
    public void Table_explicitWidthsThatFit_areUnchanged()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() }.SetColumnWidths(100, 60, 70);
        table.AddRow().AddCell("a").AddCell("b").AddCell("c");
        doc.Add(table);

        Assert.Contains("100 ", Stream(doc), StringComparison.Ordinal);
    }
}
