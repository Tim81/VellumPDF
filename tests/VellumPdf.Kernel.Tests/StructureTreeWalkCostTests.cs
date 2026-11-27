// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Saving a tagged document does not walk the structure tree once per page. The assertions count
/// the steps the tree's walks take, so they hold on any machine and under any load.
/// </summary>
public sealed class StructureTreeWalkCostTests
{
    private static readonly DateTimeOffset Pinned = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    // Walks Build makes over the whole tree: five pre-order walks and FirstLeafPages.
    private const int Walks = 6;

    // One table whose every row sits on a page of its own: per page a TR with a TH and a TD that
    // each hold a P, and a heading leaf on the same page. Returns the tree, the pages and the
    // number of elements under the tree's document root, the root included.
    private static (PdfStructureTree Tree, List<PdfPage> Pages, int Elements) TableOnePerPage(int pages)
    {
        var d = new PdfDocument { Tagged = true, Timestamp = Pinned };
        var list = new List<PdfPage>();
        var table = new PdfStructElem("Table");
        for (var i = 0; i < pages; i++)
        {
            var page = d.AddPage();
            list.Add(page);
            var row = table.AddChild(new PdfStructElem("TR"));
            row.AddChild(new PdfStructElem("TH") { Page = page, Mcid = 0 }).AddChild(new PdfStructElem("P"));
            row.AddChild(new PdfStructElem("TD") { Page = page, Mcid = 1 }).AddChild(new PdfStructElem("P"));
            table.AddChild(new PdfStructElem("H2") { Page = page, Mcid = 2 });
        }

        var tree = new PdfStructureTree();
        tree.AddStructElem(table);
        return (tree, list, 1 + 1 + 6 * pages);
    }

    private static long BuildSteps(PdfStructureTree tree, List<PdfPage> pages)
    {
        var registry = new PdfObjectRegistry();
        var refs = pages.ToDictionary(p => p, _ => registry.Reserve());
        tree.Build(registry, refs, out _);
        return tree.LastBuildSteps;
    }

    [Fact]
    public void Build_takesAFixedNumberOfStepsPerElement_notOnePerPage()
    {
        const int small = 1_000;
        const int large = 8_000;
        var (smallTree, smallPages, smallElements) = TableOnePerPage(small);
        var (largeTree, largePages, largeElements) = TableOnePerPage(large);
        var smallSteps = BuildSteps(smallTree, smallPages);
        var largeSteps = BuildSteps(largeTree, largePages);

        // The floor: each of the walks enters every element, so a count of nothing cannot pass.
        Assert.True(smallSteps >= Walks * smallElements, $"{smallSteps} steps for {smallElements} elements.");
        Assert.True(largeSteps >= Walks * largeElements, $"{largeSteps} steps for {largeElements} elements.");

        var smallPerElement = (double)smallSteps / smallElements;
        var largePerElement = (double)largeSteps / largeElements;
        Assert.True(
            largePerElement <= 1.5 * smallPerElement,
            $"steps per element: {smallPerElement:F2} at {small} pages, {largePerElement:F2} at {large} pages.");
    }

    [Fact]
    public void Build_resetsTheCountOnEachCall()
    {
        var (tree, pages, _) = TableOnePerPage(10);
        var first = BuildSteps(tree, pages);
        Assert.Equal(first, BuildSteps(tree, pages));
    }

    [Fact]
    public void Save_listsALeafsPageInTheOrderTheWalkMeetsThem()
    {
        // Two pages, and on the first page (the one the walk meets first) the MCIDs 3, 1, 3, 1 in walk
        // order. The refusal names the first MCID met twice: 3 in walk order, 1 if the page's leaves
        // were listed back to front.
        var d = new PdfDocument { Tagged = true, Timestamp = Pinned };
        var p0 = d.AddPage();
        var p1 = d.AddPage();
        var root = new PdfStructElem("Sect");
        foreach (var mcid in new[] { 3, 1, 3, 1 })
            root.AddChild(new PdfStructElem("P") { Page = p0, Mcid = mcid });
        root.AddChild(new PdfStructElem("P") { Page = p1, Mcid = 0 });
        d.RegisterStructElem(root);

        var ex = Assert.Throws<InvalidOperationException>(() => d.Save(new MemoryStream()));
        Assert.Contains("on page 0", ex.Message);
        Assert.Contains("duplicate MCID 3", ex.Message);
    }
}
