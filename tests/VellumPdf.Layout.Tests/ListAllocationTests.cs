// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

public sealed class ListAllocationTests
{
    private static long AllocatedBySaving(int items)
    {
        var style = new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 200), Margins = new EdgeInsets(10) };
        var list = new ListElement(ListStyle.Unordered);
        for (var i = 0; i < items; i++) list.Add("x", style);
        doc.Add(list);
        using var ms = new MemoryStream();

        // Per thread, not process-wide: the test host runs classes in parallel. Save lays out and
        // writes on the calling thread, so the thread-local counter sees all of it.
        var before = GC.GetAllocatedBytesForCurrentThread();
        doc.Save(ms);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>The allocation over a long list is linear in the items: quadrupling the items must cost about four times as much.</summary>
    [Fact]
    public void LongList_allocatesLinearlyInItsItemCount()
    {
        const int small = 5_000;
        // Warm up so one-time allocations (static tables, JIT) are not charged to the small run.
        _ = AllocatedBySaving(200);

        var a = AllocatedBySaving(small);
        var b = AllocatedBySaving(small * 4);

        var ratio = (double)b / a;
        // The bound of 6 sits between a linear allocation and one sized to the whole list for every page.
        Assert.True(
            ratio < RATIO_BOUND,
            $"{small} items allocated {a / 1048576.0:F1} MiB and {small * 4} items {b / 1048576.0:F1} MiB, ratio {ratio:F2}.");
    }

    private const double RATIO_BOUND = 6.0;
}
