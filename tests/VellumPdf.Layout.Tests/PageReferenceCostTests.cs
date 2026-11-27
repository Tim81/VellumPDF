// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using Xunit.Sdk;
using static VellumPdf.Layout.Tests.LinkSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// What the links and page references cost. A document that holds none pays for one walk that
/// allocates nothing beyond the plan, and the walk reads each run and cell once. A document that
/// holds many references settles in the measuring runs the cap allows.
/// </summary>
public sealed class PageReferenceCostTests
{
    private static readonly TextStyle S = Style(size: 8);

    private static long AllocationOfWalk(int paragraphs)
    {
        var renderers = new List<IRenderer>(paragraphs);
        for (var i = 0; i < paragraphs; i++)
            renderers.Add(new ParagraphRenderer(new Paragraph([new TextRun("a", S), new TextRun("b", S)])));

        // Warm up, so the first call's one-off allocations are not measured.
        NavigationPlan.Build(renderers);
        NavigationPlan.StylesRead = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        NavigationPlan.Build(renderers);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Each of the two runs of each paragraph is read once.
        Assert.Equal(paragraphs * 2, NavigationPlan.StylesRead);
        return allocated;
    }

    [Fact]
    public void TheWalk_ofADocumentWithNoLinks_allocatesWhatItDoesForOneParagraph_andReadsEachRunOnce()
    {
        var one = AllocationOfWalk(1);
        var thousand = AllocationOfWalk(1000);

        Assert.Equal(one, thousand);
    }

    [Fact]
    public void TheWalk_readsEachCellOnce()
    {
        var table = new TableElement { DefaultCellStyle = S }.SetColumnWidths(50, 50);
        for (var r = 0; r < 100; r++) table.AddRow().AddCell("a").AddCell(new Cell([new TextRun("b", S), new TextRun("c", S)]));
        var renderers = new List<IRenderer> { new TableRenderer(table) };

        NavigationPlan.StylesRead = 0;
        NavigationPlan.Build(renderers);

        // Each cell's own style is read once, and a run cell's runs besides.
        Assert.Equal(100 * (1 + 1 + 2), NavigationPlan.StylesRead);
    }

    [Fact]
    public void TenThousandReferencesToAThousandAnchors_settleInTheMeasuringRunsTheCapAllows()
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 400), Margins = new EdgeInsets(10) };
        for (var i = 0; i < 1000; i++)
            doc.Add(new Paragraph("anchor " + i, S) { Anchor = "a" + i });
        for (var i = 0; i < 1000; i++)
        {
            var runs = new List<TextRun>();
            for (var k = 0; k < 10; k++)
            {
                runs.Add(new TextRun("p. ", S));
                runs.Add(TextRun.PageReference("a" + ((i * 10 + k) % 1000), S));
                runs.Add(new TextRun(" ", S));
            }

            doc.Add(new Paragraph(runs));
        }

        var renderer = doc.CreateRenderer();
        renderer.RunLayout();

        Assert.InRange(renderer.MeasureRuns, 2, DocumentRenderer.MaxMeasureRuns);
        Assert.Equal(10_000, renderer.ReferenceTable!.Entries.Count);
        Assert.Equal(1000, renderer.ReferenceTable.Entries.Select(e => e.Anchor).Distinct().Count());
    }

    [Fact]
    public void ADocumentWithOneReferenceInAParagraphAndNoOtherDependent_hasOneMeasuringRunPerLabelChange()
    {
        // Time and allocation for a large run cell are measured by the author of a change and
        // reported with it; this pins the shape that makes them grow with the characters and the
        // measuring runs, and nothing else: a run cell's wrap is not repeated for an
        // unchanged label.
        using var doc = ShortPage();
        var table = new TableElement { DefaultCellStyle = S }.SetColumnWidths(250);
        var words = string.Join(" ", Enumerable.Range(0, 2000).Select(i => "w" + i));
        table.AddRow().AddCell(new Cell([new TextRun(words + " see ", S), TextRun.PageReference("t", S)]));
        doc.Add(table);
        doc.Add(new Paragraph("target") { Anchor = "t" });

        var clock = Stopwatch.StartNew();
        var renderer = doc.CreateRenderer();
        renderer.RunLayout();
        clock.Stop();

        Assert.True(renderer.MeasureRuns <= 3, $"{renderer.MeasureRuns} measuring runs in {clock.ElapsedMilliseconds} ms");
    }
}
