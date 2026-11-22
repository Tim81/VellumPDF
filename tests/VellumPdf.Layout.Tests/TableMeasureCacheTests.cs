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

    /// <summary>
    /// Runs <paramref name="save"/> with a probe on this thread, and returns the probe.
    /// </summary>
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

    /// <summary>
    /// A step that moves no column takes the column widths from the ones kept for the pass, raised
    /// by what its cells contribute unbroken, and measures no cell of the table. The probe checks
    /// every such step against the table measured again and throws on a difference.
    /// </summary>
    [Fact]
    public void AStepRaisesTheKeptColumnWidthsAndMeasuresNoOtherCell()
    {
        const int groups = 6;
        using var doc = WidePage();
        doc.Add(FillerTable(groups));

        var probe = new TableMeasureProbe { Verify = true };
        TableMeasureProbe.Current = probe;
        try { Save(doc); }
        finally { TableMeasureProbe.Current = null; }

        Assert.Equal(groups, probe.WidthsRaised);
        Assert.Equal(0, probe.WidthsMeasuredAgain);
    }

    /// <summary>
    /// A cell that contributes less to a column read unbroken than it did as it was is not a raise:
    /// the table is measured again. "ab", a tab, "ab" and a line feed is two words and a space wide
    /// with its breaks and two words wide without, because the tab and the line feed measure zero
    /// in Helvetica, so its column's maximum falls.
    /// </summary>
    [Fact]
    public void AStepThatNarrowsAColumnMeasuresTheTableAgain()
    {
        using var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(36, 0);
        t.AddRow().AddCell(new Cell(Words(60)) { RowSpan = 2 }).AddCell("ab" + (char)9 + "ab" + (char)10);
        t.AddRow().AddCell("c");
        doc.Add(t);

        var probe = new TableMeasureProbe { Verify = true };
        TableMeasureProbe.Current = probe;
        try { Save(doc); }
        finally { TableMeasureProbe.Current = null; }

        Assert.Equal(["row 0, cell 0", "row 0, cell 1"], Dropped(doc));
        Assert.Equal(1, probe.WidthsMeasuredAgain);
        Assert.Equal(0, probe.WidthsRaised);
    }

    /// <summary>
    /// Counting the lines of a word broken at its characters measures each character without
    /// allocating a string for it: 3,000 Latin-1 characters allocate under 4KB, where a string per
    /// character is about 90KB. A cell that gives up its breaks is one long word, and the decision
    /// counts it again at every width the columns take.
    /// </summary>
    [Fact]
    public void CountingABrokenWordAllocatesNothingPerCharacter()
    {
        var style = Helvetica();
        var word = string.Concat(Enumerable.Repeat("abc", 1000));
        TableRenderer.CountLines(word, style, 100, true, out _, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var lines = TableRenderer.CountLines(word, style, 100, true, out _, out _);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(lines > 100, $"{lines} lines");
        Assert.True(bytes < 4096, $"{bytes} bytes");
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
    /// high end. A word wider than the width is broken at its characters, and the comparisons that
    /// decide where bound the interval as the others do. The texts include empty words, doubled
    /// spaces, line breaks and tabs, in both modes, and widths down to one point.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACertificateCountsLikeTheWrapItStandsForAcrossItsInterval(bool honourBreaks)
    {
        var style = Helvetica();
        var rng = new Random(honourBreaks ? 1 : 2);
        var broken = 0;
        for (var n = 0; n < 4000; n++)
        {
            var text = RandomText(rng);
            var width = Math.Max(1, rng.NextDouble() * 120);
            var lines = TableRenderer.CountLines(text, style, width, honourBreaks, out var from, out var below);

            Assert.Equal(TableRenderer.WordWrapCount(text, style, width, honourBreaks), lines);
            if (text.Split([' ', '\n', '\r', '\t']).Any(w => style.MeasureString(w) > width))
                broken++;
            Assert.True(from <= width && width < below, $"{from} <= {width} < {below}");
            if (double.IsFinite(from) && from >= 1)
                Assert.Equal(lines, TableRenderer.WordWrapCount(text, style, from, honourBreaks));
            if (double.IsFinite(below))
                Assert.Equal(lines, TableRenderer.WordWrapCount(text, style, Math.BitDecrement(below), honourBreaks));
        }

        Assert.True(broken > 100, $"{broken} texts broke a word");
    }

    /// <summary>
    /// A wrap that broke a word holds over a range of widths too: "abcdefghijklmnop" in a 20pt
    /// column is cut at the character where the next one no longer fits, and every width that
    /// leaves each cut where it is wraps into the same lines.
    /// </summary>
    [Fact]
    public void ACertificateHoldsOverARangeWhenAWordWasBroken()
    {
        var style = Helvetica();
        const string word = "abcdefghijklmnop";

        var lines = TableRenderer.CountLines(word, style, 20, true, out var from, out var below);

        Assert.True(lines > 1);
        Assert.True(double.IsFinite(from) && double.IsFinite(below) && from < below, $"[{from}, {below})");
        Assert.True(from <= 20 && 20 < below, $"[{from}, {below})");
        Assert.Equal(TableRenderer.WordWrapLines(word, style, 20, true), TableRenderer.WordWrapLines(word, style, from, true));
        Assert.Equal(
            TableRenderer.WordWrapLines(word, style, 20, true),
            TableRenderer.WordWrapLines(word, style, Math.BitDecrement(below), true));
        Assert.NotEqual(
            TableRenderer.WordWrapLines(word, style, 20, true),
            TableRenderer.WordWrapLines(word, style, below, true));
    }

    /// <summary>
    /// The interval a count holds over is closed at its low end and open at its high end: the
    /// width the count was found at and the lowest width of the interval hold it, the width the
    /// comparisons first flip at does not, and neither does the one just under the low end.
    /// </summary>
    [Fact]
    public void AnIntervalIsClosedBelowAndOpenAbove()
    {
        var style = Helvetica();
        var text = "ab ab ab ab";
        var lines = TableRenderer.CountLines(text, style, 36, true, out var from, out var below);
        Assert.True(double.IsFinite(from) && double.IsFinite(below), $"[{from}, {below})");
        var entry = new CellMeasure { HasLines = true, Lines = lines, From = from, Below = below };

        Assert.True(entry.Holds(36));
        Assert.True(entry.Holds(from));
        Assert.True(entry.Holds(Math.BitDecrement(below)));
        Assert.False(entry.Holds(below));
        Assert.False(entry.Holds(Math.BitDecrement(from)));
        Assert.False(new CellMeasure().Holds(36));
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
    /// so it is read unbroken; the next table keeps its breaks at 60 lines in a row that can be
    /// cut.
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

    private static (long Bytes, int Reported) Allocated(Func<Document> build)
    {
        using var doc = build();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Save(doc);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return (bytes, doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped).Sum(d => d.Count));
    }

    private static Document FillerDocument(int groups, string separator)
    {
        var doc = WidePage();
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(36, 36, 0);
        for (var i = 0; i < groups; i++)
        {
            t.AddRow().AddCell("x").AddCell("x").AddCell(new Cell(Words(60, separator)) { RowSpan = 2 });
            t.AddRow().AddCell("y");
        }
        doc.Add(t);
        return doc;
    }

    /// <summary>
    /// Fillers in an automatic column: each group is a cell of 60 lines in the column other rows
    /// share, beside two short cells in 36pt columns. Every step keeps the column widths, so the
    /// whole decision is one pass. The allocations of the table are a fixed multiple of those of
    /// the same table with the lines separated by spaces, which has nothing to give up, at 400
    /// groups and at 1,600: a cost that grew with the square of the groups would pass the
    /// smaller table and fail the larger. Allocations are counted, not time, because the same
    /// run on a loaded machine takes several times as long.
    /// </summary>
    [Fact]
    public void TheCostOfGivingUpBreaksInASharedAutoColumnStaysAFixedMultipleOfNotGivingThemUp()
    {
        Allocated(() => FillerDocument(10, "\n"));
        Allocated(() => FillerDocument(10, " "));
        foreach (var groups in new[] { 400, 1600 })
        {
            var withBreaks = Allocated(() => FillerDocument(groups, "\n"));
            var withSpaces = Allocated(() => FillerDocument(groups, " "));

            Assert.Equal(Math.Min(groups, 1000), withBreaks.Reported);
            Assert.Equal(0, withSpaces.Reported);
            Assert.True(
                withBreaks.Bytes < 1.5 * withSpaces.Bytes,
                $"{groups} groups: {withBreaks.Bytes} bytes against {withSpaces.Bytes} without breaks");
        }
    }

    /// <summary>
    /// Groups that each widen the automatic column, and so change what the next group is judged
    /// by: the cell of group i has 48 + i lines. Every step moves a column width, so none keeps
    /// the pass going and every group is judged on the widths the one before it leaves. The
    /// save of 300 of them allocates under 60MB, which is a bound on the
    /// cost itself and not on its ratio to a table with nothing to give up, because that table's
    /// cost moves with the caches too.
    /// </summary>
    [Fact]
    public void TheCostOfGroupsThatEachWidenTheColumnStaysUnderAFixedBound()
    {
        static Document Build(int groups)
        {
            var doc = WidePage();
            var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(0, 0, 0);
            for (var i = 0; i < groups; i++)
            {
                t.AddRow().AddCell("x").AddCell(new Cell(Words(48 + i)) { RowSpan = 2 })
                    .AddCell("lorem ipsum dolor sit amet " + i);
                t.AddRow().AddCell("y");
            }
            doc.Add(t);
            return doc;
        }

        Allocated(() => Build(10));
        var withBreaks = Allocated(() => Build(300));

        Assert.Equal(300, withBreaks.Reported);
        Assert.True(withBreaks.Bytes < 60_000_000L, $"{withBreaks.Bytes} bytes");
    }

    /// <summary>
    /// A chain of 60 groups, each with a cell a little wider than the one before it beside a fixed
    /// column and an automatic one, and a wrapping cell under it. Every group is reported, and the
    /// save allocates under 30MB.
    /// </summary>
    [Fact]
    public void TheCostOfAChainOfTippedGroupsStaysUnderAFixedBound()
    {
        const int groups = 60;
        using var doc = WidePage();
        doc.Add(CascadeTable(groups));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Save(doc);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(groups, Dropped(doc).Length);
        Assert.True(bytes < 30_000_000L, $"{bytes} bytes");
    }

    private static TableElement CascadeTable(int n)
    {
        var font = new FontReference(Standard14.Helvetica);
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(1980, 0);
        var hp = new Cell("x").Padding.Horizontal;

        string Text(int k)
        {
            var lines = k == 1 ? 60 : 38;
            var chars = 100 + k;
            var sb = new System.Text.StringBuilder();
            for (var l = 0; l < lines; l++)
            {
                if (l > 0) sb.Append('\n');
                sb.Append('i', (chars / lines) + (l < chars % lines ? 1 : 0));
            }
            return sb.ToString();
        }

        var a = Enumerable.Range(1, n).Select(Text).ToArray();
        var longestLine = a.Max(x => x.Split('\n').Max(l => font.MeasureString(l, 10)));
        double Inner(double floorA) => 1980 - (floorA + hp) - hp;
        var inner = new double[n + 1];
        inner[0] = Inner(longestLine);
        for (var j = 1; j <= n; j++) inner[j] = Inner(font.MeasureString(a[j - 1], 10));
        var iw = font.MeasureString("i", 10);
        var sp = font.MeasureString(" ", 10);
        for (var k = 1; k <= n; k++)
        {
            string b;
            if (k == 1)
            {
                b = "r";
            }
            else
            {
                var target = (inner[k - 2] + inner[k - 1]) / 2;
                string? best = null;
                var bestErr = double.MaxValue;
                for (var y = 0; y < 6; y++)
                {
                    var fw = font.MeasureString(new string('f', y), 10);
                    var x = (int)Math.Round((((target - sp) / 2) - fw) / iw);
                    if (x < 0) continue;
                    var w = new string('i', x) + new string('f', y);
                    var err = Math.Abs(font.MeasureString(w + " " + w, 10) - target);
                    if (err < bestErr) { bestErr = err; best = w; }
                }
                b = string.Join(" ", Enumerable.Repeat(best!, 14));
            }
            t.AddRow().AddCell("r").AddCell(new Cell(a[k - 1]) { RowSpan = 2 });
            t.AddRow().AddCell(b);
        }
        return t;
    }
}
