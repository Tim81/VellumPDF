// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// What the element costs, pinned by allocation and by counts, never by the clock (#400): the
/// constructor reads the text once and copies nothing, and the layout and the save grow in step
/// with the text, so a ten-times longer line costs about ten times as much.
/// </summary>
public sealed class PreformattedBoundTests
{
    private static long Allocated(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static Document PageOf(Preformatted block)
    {
        var doc = new Document();
        doc.Add(block);
        return doc;
    }

    [Fact]
    public void TheConstructor_readsAMillionCharactersAndAllocatesAlmostNothing()
    {
        var text = new string('x', 1_000_000);
        _ = new Preformatted("warm up");

        var bytes = Allocated(() => _ = new Preformatted(text));

        Assert.True(bytes <= 1024, $"allocated {bytes} bytes");
    }

    [Fact]
    public void TenTimesTheTabs_costAboutTenTimesTheLayoutAndTheSave()
    {
        var smaller = new Preformatted(new string('\t', 1_000), Courier()) { TabWidth = 32 };
        var larger = new Preformatted(new string('\t', 10_000), Courier()) { TabWidth = 32 };

        Assert.Equal(32_000, Assert.Single(PreformattedText.Layout(smaller, 1e9, out _)).Text.Length);
        Assert.Equal(320_000, Assert.Single(PreformattedText.Layout(larger, 1e9, out _)).Text.Length);

        _ = PreformattedText.Layout(smaller, 1e9, out _);
        var layoutSmall = Allocated(() => _ = PreformattedText.Layout(smaller, 1e9, out _));
        var layoutLarge = Allocated(() => _ = PreformattedText.Layout(larger, 1e9, out _));
        Assert.InRange(layoutLarge, layoutSmall, layoutSmall * 15);

        using var warm = PageOf(new Preformatted("warm", Courier()));
        _ = Save(warm);
        using var docSmall = PageOf(smaller);
        using var docLarge = PageOf(larger);
        var saveSmall = Allocated(() => _ = Save(docSmall));
        var saveLarge = Allocated(() => _ = Save(docLarge));
        Assert.InRange(saveLarge, saveSmall, saveSmall * 15);
    }

    [Theory]
    [InlineData(100_000, 1_352)]
    [InlineData(1_000_000, 13_514)]
    public void ALongLine_wrapsIntoTheCountedSegments_andTheWrapRunsOnce(int length, int visualLines)
    {
        var block = new Preformatted(new string('y', length), Courier());

        var lines = PreformattedText.Layout(block, 451.28, out var wrapped);

        // 74 columns a segment while more than 75 remain, then the rest: 26 and 38 columns here.
        Assert.Equal(visualLines, lines.Count);
        Assert.Equal(1, wrapped);
        Assert.All(lines.Take(visualLines - 1), l => Assert.Equal(74, l.Text.Length));
        Assert.Equal(length - 74 * (visualLines - 1), lines[^1].Text.Length);
        Assert.Equal(length, lines.Sum(l => l.Text.Length));

        var renderer = new DocumentRenderer(NewPdf(), new PdfRectangle(0, 0, 595.28, 841.89), new EdgeInsets(72));
        var element = new PreformattedRenderer(block);
        renderer.Add(element);
        var pages = PageContents(Render(renderer));
        Assert.Equal(1, element.WrapCalls);
        Assert.Equal(visualLines, pages.Sum(p => Literals(p).Length));
    }

    [Fact]
    public void TenTimesTheText_costsAboutTenTimesTheLayoutAndTheSave()
    {
        var smaller = new Preformatted(new string('y', 100_000), Courier());
        var larger = new Preformatted(new string('y', 1_000_000), Courier());

        _ = PreformattedText.Layout(smaller, 451.28, out _);
        var layoutSmall = Allocated(() => _ = PreformattedText.Layout(smaller, 451.28, out _));
        var layoutLarge = Allocated(() => _ = PreformattedText.Layout(larger, 451.28, out _));
        Assert.InRange(layoutLarge, layoutSmall, layoutSmall * 15);

        using var warm = PageOf(new Preformatted("warm", Courier()));
        _ = Save(warm);
        using var docSmall = PageOf(smaller);
        using var docLarge = PageOf(larger);
        var saveSmall = Allocated(() => _ = Save(docSmall));
        var saveLarge = Allocated(() => _ = Save(docLarge));
        Assert.InRange(saveLarge, saveSmall, saveSmall * 15);
    }
}
