// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using VellumPdf.Document;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Saving a tagged document does not walk the structure tree once per page. The assertions count
/// the elements the walks enter, so they hold on any machine and under any load.
/// </summary>
public sealed class StructureTreeWalkCostTests
{
    private static readonly DateTimeOffset Pinned = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly FieldInfo VisitCounter = typeof(PdfStructElem).Assembly
        .GetType("VellumPdf.Document.PdfStructureTree", throwOnError: true)!
        .GetField("ElementVisits", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

    // One table whose every row sits on a page of its own: Table, then per page a TR with a TH and a
    // TD that each hold a P, and a heading leaf on the same page.
    private static PdfDocument TableOnePerPage(int pages)
    {
        var d = new PdfDocument { Tagged = true, Timestamp = Pinned };
        var table = new PdfStructElem("Table");
        for (var i = 0; i < pages; i++)
        {
            var page = d.AddPage();
            var row = table.AddChild(new PdfStructElem("TR"));
            row.AddChild(new PdfStructElem("TH") { Page = page, Mcid = 0 }).AddChild(new PdfStructElem("P"));
            row.AddChild(new PdfStructElem("TD") { Page = page, Mcid = 1 }).AddChild(new PdfStructElem("P"));
            table.AddChild(new PdfStructElem("H2") { Page = page, Mcid = 2 });
        }

        d.RegisterStructElem(table);
        return d;
    }

    private static long SaveCounting(PdfDocument d)
    {
        using var ms = new MemoryStream();
        var before = (long)VisitCounter.GetValue(null)!;
        d.Save(ms);
        return (long)VisitCounter.GetValue(null)! - before;
    }

    [Fact]
    public void Save_visitsEachElementAFixedNumberOfTimes_notOncePerPage()
    {
        // Warm-up, so the first save's one-time work is not counted below.
        TableOnePerPage(10).Save(new MemoryStream());

        const int small = 1_000;
        const int large = 8_000;
        var smallVisits = SaveCounting(TableOnePerPage(small));
        var largeVisits = SaveCounting(TableOnePerPage(large));

        var smallPerPage = (double)smallVisits / small;
        var largePerPage = (double)largeVisits / large;
        Assert.True(
            largePerPage <= 1.5 * smallPerPage,
            $"visits per page: {smallPerPage:F1} at {small} pages, {largePerPage:F1} at {large} pages.");
    }

    [Fact]
    public void Save_groupsLeavesByPage_inTheOrderTheWalkMeetsThem()
    {
        // Pages interleave through the tree, an MCID is claimed twice on one page only, and the
        // refusals name the page that has the fault.
        var d = new PdfDocument { Tagged = true, Timestamp = Pinned };
        var p0 = d.AddPage();
        var p1 = d.AddPage();
        var root = new PdfStructElem("Sect");
        root.AddChild(new PdfStructElem("P") { Page = p1, Mcid = 0 });
        root.AddChild(new PdfStructElem("P") { Page = p0, Mcid = 0 });
        root.AddChild(new PdfStructElem("P") { Page = p1, Mcid = 0 });
        d.RegisterStructElem(root);

        var ex = Assert.Throws<InvalidOperationException>(() => d.Save(new MemoryStream()));
        Assert.Contains("on page 0", ex.Message); // p1 is the first page the walk meets: index 0
        Assert.Contains("duplicate MCID 0", ex.Message);
    }
}
