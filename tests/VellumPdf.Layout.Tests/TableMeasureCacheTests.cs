// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The caches of the decision that reads some table cells unbroken. A cache answers what measuring
/// again would, so these tests count what it answered and compare the outcome with the one the
/// documentation states; the differential against the build without the caches is run apart from
/// the suite.
/// </summary>
public sealed class TableMeasureCacheTests
{
    private static TextStyle Helvetica(double size = 10) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = size,
    };

    private static Document WidePage() => new()
    {
        PageSize = new PdfRectangle(0, 0, 2000, 600),
        Margins = new EdgeInsets(10),
    };

    private static string Words(int n, string separator = "\n") =>
        string.Join(separator, Enumerable.Repeat("ab", n));

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string[] Dropped(Document doc) =>
        [.. doc.Diagnostics
            .Where(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped)
            .Select(d => Regex.Match(d.Message, @"row \d+, cell \d+").Value)
            .OrderBy(m => int.Parse(Regex.Match(m, @"\d+").Value, CultureInfo.InvariantCulture))
            .ThenBy(m => m, StringComparer.Ordinal)];

    /// <summary>Runs <paramref name="save"/> with a probe on this thread, and returns the probe.</summary>
    private static TableMeasureProbe Probed(Action save)
    {
        var probe = new TableMeasureProbe();
        TableMeasureProbe.Current = probe;
        try { save(); }
        finally { TableMeasureProbe.Current = null; }
        return probe;
    }

    /// <summary>
    /// Columns of 36pt, 36pt and one sized by its content, then <paramref name="groups"/> groups
    /// that each give up the breaks of a cell of 60 lines in the automatic column.
    /// </summary>
    private static TableElement FillerTable(int groups)
    {
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(36, 36, 0);
        for (var i = 0; i < groups; i++)
        {
            t.AddRow().AddCell("x").AddCell("x").AddCell(new Cell(Words(60)) { RowSpan = 2 });
            t.AddRow().AddCell("y");
        }
        return t;
    }

    /// <summary>
    /// A cell's longest word and widest line are measured once per mode. Each of the 6 groups ends
    /// a pass, so the table is resolved more than 6 times, and a cell is measured once as it is
    /// and, for the cell that gives up its breaks, once as it is read unbroken.
    /// </summary>
    [Fact]
    public void ACellIsMeasuredForItsColumnWidthOncePerMode()
    {
        const int groups = 6;
        using var doc = WidePage();
        doc.Add(FillerTable(groups));

        var probe = Probed(() => Save(doc));

        Assert.Equal(Enumerable.Range(0, groups).Select(i => $"row {2 * i}, cell 2").ToArray(), Dropped(doc));
        const int cells = 4 * groups;
        Assert.Equal(cells + groups, probe.WidthMisses);
        Assert.True(probe.WidthHits > probe.WidthMisses, $"{probe.WidthHits} hits, {probe.WidthMisses} misses");
    }

    private static readonly string[] Alphabet =
        ["a", "i", "W", "ab", "word", "m", " ", " ", " ", "  ", "\n", "\t", "\r\n", "x\u00A0y", "\u0001", "WWWWWWWW"];

    private static string RandomText(Random rng)
    {
        var parts = rng.Next(0, 24);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < parts; i++) sb.Append(Alphabet[rng.Next(Alphabet.Length)]);
        return sb.ToString();
    }

    /// <summary>
    /// The count a certificate returns is the count of the wrap it stands for, and it holds across
    /// the interval it names: at the width it was made for, at the low end, and just under the
    /// high end. A word wider than the width is broken at its characters, which pins the count to
    /// that width. The texts include empty words, doubled spaces, line breaks and tabs, in both
    /// modes, and widths down to one point.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACertificateCountsLikeTheWrapItStandsForAcrossItsInterval(bool honourBreaks)
    {
        var style = Helvetica();
        var rng = new Random(honourBreaks ? 1 : 2);
        var pinned = 0;
        var intervals = 0;
        for (var n = 0; n < 4000; n++)
        {
            var text = RandomText(rng);
            var width = Math.Max(1, rng.NextDouble() * 120);
            var lines = TableRenderer.CountLines(text, style, width, honourBreaks, out var from, out var below, out var isPinned);

            Assert.Equal(TableRenderer.WordWrapCount(text, style, width, honourBreaks), lines);
            if (isPinned)
            {
                pinned++;
                Assert.Equal(width, from);
                continue;
            }

            intervals++;
            Assert.True(from <= width && width < below, $"{from} <= {width} < {below}");
            if (double.IsFinite(from) && from >= 1)
                Assert.Equal(lines, TableRenderer.WordWrapCount(text, style, from, honourBreaks));
            if (double.IsFinite(below))
                Assert.Equal(lines, TableRenderer.WordWrapCount(text, style, Math.BitDecrement(below), honourBreaks));
        }

        Assert.True(pinned > 100 && intervals > 100, $"{pinned} pinned, {intervals} intervals");
    }

    /// <summary>
    /// A wrap whose words all fit holds over a range of widths, and one that broke a word holds
    /// at the width alone. One more point of width moves no break in "ab ab ab ab" at 36pt, and
    /// moves the break of "abcdefghijklmnop" in a 20pt column.
    /// </summary>
    [Fact]
    public void ACertificateHoldsOverARangeUnlessAWordWasBroken()
    {
        var style = Helvetica();

        var lines = TableRenderer.CountLines("ab ab ab ab", style, 36, true, out var from, out var below, out var pinned);
        Assert.Equal(TableRenderer.WordWrapCount("ab ab ab ab", style, 36, true), lines);
        Assert.False(pinned);
        Assert.True(from < 36 && 37 < below, $"[{from}, {below})");

        TableRenderer.CountLines("abcdefghijklmnop", style, 20, true, out from, out _, out pinned);
        Assert.True(pinned);
        Assert.Equal(20.0, from);
    }

    /// <summary>
    /// The line counts the caches answer are the counts of a fresh wrap: the probe measures again
    /// on every hit and throws on a difference. Widths that shrink as groups give up their breaks
    /// move across the intervals, so both hits and misses are exercised.
    /// </summary>
    [Fact]
    public void EveryCachedLineCountEqualsAFreshWrap()
    {
        using var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica() };
        for (var i = 0; i < 5; i++)
        {
            t.AddRow().AddCell("x").AddCell(new Cell(Words(48 + i)) { RowSpan = 2 })
                .AddCell(string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 3 + i)));
            t.AddRow().AddCell("y");
        }
        doc.Add(t);

        var probe = new TableMeasureProbe { Verify = true };
        TableMeasureProbe.Current = probe;
        try { Save(doc); }
        finally { TableMeasureProbe.Current = null; }

        Assert.True(probe.WrapHits > 0, $"{probe.WrapHits} hits");
        Assert.True(probe.WrapMisses > 0, $"{probe.WrapMisses} misses");
        Assert.Equal(5, Dropped(doc).Length);
    }

    /// <summary>
    /// A cell read unbroken and the same cell with its breaks kept are two entries: the count of
    /// one is not the count of the other. The header of 60 lines is 728pt and the page holds 580pt,
    /// so it is read unbroken; the next table keeps its breaks at 60 lines in a row that can be cut.
    /// </summary>
    [Fact]
    public void TheModeOfACellIsPartOfItsEntry()
    {
        using var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(500, 500);
        t.AddRow(isHeader: true).AddCell(Words(60)).AddCell("h");
        t.AddRow().AddCell(Words(60)).AddCell("y");
        doc.Add(t);

        var probe = new TableMeasureProbe { Verify = true };
        TableMeasureProbe.Current = probe;
        try { Save(doc); }
        finally { TableMeasureProbe.Current = null; }

        Assert.Equal(["row 0, cell 0"], Dropped(doc));
    }

    /// <summary>
    /// A group that gives up its breaks in an automatic column does not end the pass when no
    /// column changes width: the unbroken text of its cell is narrower than a word of another row
    /// in the same column, so the table is measured once for all six groups, not once for each.
    /// </summary>
    [Fact]
    public void AStepThatLeavesEveryColumnWidthAsItWasDoesNotEndThePass()
    {
        const int groups = 6;
        using var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica() };
        t.AddRow().AddCell("x").AddCell(new string('W', 100)).AddCell("x");
        for (var i = 0; i < groups; i++)
        {
            t.AddRow().AddCell("x").AddCell(new Cell(Words(60)) { RowSpan = 2 }).AddCell("x");
            t.AddRow().AddCell("y").AddCell("y");
        }
        doc.Add(t);

        var probe = Probed(() => Save(doc));

        Assert.Equal(Enumerable.Range(0, groups).Select(i => $"row {2 * i + 1}, cell 1").ToArray(), Dropped(doc));
        Assert.Equal(groups, probe.StepsKeptPassGoing);
        Assert.True(probe.Passes <= 3, $"{probe.Passes} resolutions");
    }

    /// <summary>
    /// A step that moves a column's width still ends the pass: the next group is judged on the
    /// widths it leaves. Each group's cell is longer than the one before it, so each step widens
    /// the automatic column, no step keeps a pass going, and the table is resolved once for each
    /// group and once more.
    /// </summary>
    [Fact]
    public void AStepThatMovesAColumnWidthStillEndsThePass()
    {
        const int groups = 4;
        using var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica() };
        for (var i = 0; i < groups; i++)
        {
            t.AddRow().AddCell("x").AddCell(new Cell(Words(60 + (20 * i))) { RowSpan = 2 });
            t.AddRow().AddCell("y");
        }
        doc.Add(t);

        var probe = Probed(() => Save(doc));

        Assert.Equal(groups, Dropped(doc).Length);
        Assert.Equal(0, probe.StepsKeptPassGoing);
        Assert.True(probe.Passes > groups, $"{probe.Passes} resolutions");
    }

    /// <summary>
    /// A step in a row outside the group does not keep the pass going even when no column moves,
    /// because that row is measured again: a tab in the header, which the group's third step
    /// takes, changes the header's height.
    /// </summary>
    [Fact]
    public void AStepInAHeaderRowStillEndsThePass()
    {
        using var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica() };
        t.AddRow(isHeader: true).AddCell(new string('W', 100)).AddCell("a" + (char)9 + "b");
        t.AddRow().AddCell(new Cell("a") { RowSpan = 2 }).AddCell("b" + (char)9 + "b");
        t.AddRow().AddCell(Words(47, " "));
        doc.Add(t);

        var probe = Probed(() => Save(doc));

        Assert.Equal(0, probe.StepsKeptPassGoing);
    }

    /// <summary>
    /// A table that runs over many pages resolves its grid once: the renderer that carries it to
    /// the next page, laid out at the same width and fresh page, keeps the grid and the heights.
    /// </summary>
    [Fact]
    public void ATableOverManyPagesResolvesItsGridOnce()
    {
        using var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(500, 500);
        for (var i = 0; i < 400; i++) t.AddRow().AddCell("row " + i).AddCell("x");
        doc.Add(t);

        var probe = Probed(() => Save(doc));

        Assert.Equal(1, probe.Passes);
        Assert.True(probe.GridsReused >= 5, $"{probe.GridsReused} layouts kept the grid");
    }

    /// <summary>
    /// A page of another width resolves the grid again: the cached one was made for the width the
    /// first page had.
    /// </summary>
    [Fact]
    public void ALaterPageOfAnotherWidthResolvesTheGridAgain()
    {
        var t = new TableElement { DefaultCellStyle = Helvetica() };
        for (var i = 0; i < 100; i++) t.AddRow().AddCell("row " + i).AddCell(Words(3, " "));
        var tall = new LayoutBox(10, 10, 1980, 600);
        var narrow = new LayoutBox(10, 10, 300, 600);

        var probe = new TableMeasureProbe();
        TableMeasureProbe.Current = probe;
        try
        {
            var first = new TableRenderer(t).Layout(new LayoutContext(new LayoutBox(10, 10, 1980, 100)) { FreshPageArea = tall });
            var same = first.OverflowRenderer!.Layout(new LayoutContext(new LayoutBox(10, 10, 1980, 100)) { FreshPageArea = tall });
            Assert.Equal(1, probe.GridsReused);
            Assert.NotNull(same.OverflowRenderer);

            same.OverflowRenderer!.Layout(new LayoutContext(narrow) { FreshPageArea = narrow });
        }
        finally { TableMeasureProbe.Current = null; }

        Assert.Equal(1, probe.GridsReused);
        Assert.Equal(2, probe.Passes);
    }

    /// <summary>
    /// A cell that is refused is refused when the table is first laid out, whichever page it is
    /// on, so keeping the grid for the later pages hides no refusal: the save throws from the
    /// first page, naming the cell, as it does when nothing is kept.
    /// </summary>
    [Theory]
    [InlineData("fontSize")]
    [InlineData("colSpan")]
    public void ACellOnALaterPageIsRefusedFromTheFirstPage(string fault)
    {
        using var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(500, 500);
        for (var i = 0; i < 200; i++) t.AddRow().AddCell("row " + i).AddCell("x");
        var last = t.AddRow().AddCell("last");
        last.AddCell(fault == "fontSize"
            ? new Cell("bad") { Style = Helvetica(double.NaN) }
            : new Cell("bad") { ColSpan = 0 });
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("row 200, cell 1", ex.Message);
    }
}
