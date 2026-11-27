// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.LinkSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A document that holds a page reference compares the drawing pass with the last measuring run,
/// because the labels it prints were resolved against that run. A renderer that answers differently
/// the later time, or a band replaced while the document renders, then makes the save throw. The
/// same documents with the label written as literal text have nothing to compare with, and save.
/// </summary>
public sealed class PageReferenceGuardTests
{
    private static readonly TextStyle S = Style();

    /// <summary>A fixed height. Runs <paramref name="onDraw"/> when it is drawn.</summary>
    private sealed class Block(double height, Action? onDraw = null) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            context.Area.Height < height ? LayoutResult.Nothing() : LayoutResult.Full(context.Area.WithHeight(height));

        public void Draw(DrawContext context) => onDraw?.Invoke();
    }

    /// <summary>
    /// 20pt for its first <paramref name="steadyCalls"/> calls and 250pt afterwards: the measuring
    /// runs see one document and the drawing pass another.
    /// </summary>
    private sealed class ChangesItsMind(int steadyCalls) : IRenderer
    {
        private int _calls;

        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(context.Area.WithHeight(_calls++ < steadyCalls ? 20 : 250));

        public void Draw(DrawContext context) { }
    }

    // The reference prints "1", the target's page in every measuring run, so two runs settle it,
    // and the renderer answers the same until the drawing pass. Without a reference there is one
    // measuring run.
    private static Document ARendererThatChangesItsMind(bool reference)
    {
        var doc = ShortPage();
        doc.Add(new Paragraph([new TextRun("See ", S), reference ? TextRun.PageReference("t", S) : new TextRun("1", Style())]));
        doc.Add(new ChangesItsMind(steadyCalls: reference ? 2 : 1));
        doc.Add(new Block(140));
        doc.Add(new Heading("target") { Anchor = "t" });
        return doc;
    }

    [Fact]
    public void ARendererThatAnswersDifferentlyTheLaterTime_failsTheSave_withAReference_andASecondSaveSaysItCannotBeSavedAgain()
    {
        // Without the reference the document saves, and the drawing pass puts the heading on
        // another page than the measuring run did: the plans differ in a page index.
        var literal = ARendererThatChangesItsMind(reference: false);
        literal.Footer = new RunningBand("{page}");
        var control = literal.CreateRenderer();
        control.RecordPlacements = true;
        control.RunLayout();
        var heading = control.MeasurePlan!.Placements.Single(p => p.Anchor == "t");
        var drawn = control.DrawPlan!.Placements.Single(p => p.Anchor == "t");
        Assert.NotEqual(heading.PageIndex, drawn.PageIndex);
        Assert.NotEmpty(Save(ARendererThatChangesItsMind(reference: false)));

        var doc = ARendererThatChangesItsMind(reference: true);
        var first = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.StartsWith("Pagination changed between", first.Message, StringComparison.Ordinal);
        var second = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.StartsWith("This document cannot be saved again", second.Message, StringComparison.Ordinal);
    }

    // A footer 120pt tall, put in place while the first block is drawn, leaves 260pt of content
    // area, and the heading that fitted the 361.6pt the measuring runs read no longer does.
    private static (DocumentRenderer Renderer, PdfDocument Pdf) ABandReplacedDuringTheRender(bool reference)
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 400), new EdgeInsets(10))
        {
            Footer = new RunningBand("f"),
        };
        renderer.Add(new ParagraphRenderer(new Paragraph([new TextRun("See ", S), reference ? TextRun.PageReference("t", S) : new TextRun("1", Style())])));
        renderer.Add(new Block(20, () => renderer.Footer = new RunningBand("g") { Height = 120 }));
        renderer.Add(new Block(220));
        renderer.Add(new HeadingRenderer(new Heading("target") { Anchor = "t" }));
        return (renderer, pdf);
    }

    [Fact]
    public void ABandReplacedDuringTheRender_failsTheSave_withAReference_andSavesWithoutOne()
    {
        var (control, _) = ABandReplacedDuringTheRender(reference: false);
        control.RecordPlacements = true;
        control.RunLayout();
        var measured = control.MeasurePlan!.Placements.Single(p => p.Anchor == "t");
        var drawn = control.DrawPlan!.Placements.Single(p => p.Anchor == "t");
        Assert.NotEqual(measured.PageIndex, drawn.PageIndex);

        var (literal, _) = ABandReplacedDuringTheRender(reference: false);
        using (var ms = new MemoryStream())
        {
            literal.Render(ms);
            Assert.NotEmpty(ms.ToArray());
        }

        var (renderer, _) = ABandReplacedDuringTheRender(reference: true);
        using var ms2 = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render(ms2));
        Assert.StartsWith("Pagination changed between", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("so a renderer answered differently the later time.", ex.Message, StringComparison.Ordinal);

        // The same renderer does not render again.
        Assert.ThrowsAny<Exception>(() => renderer.Render(new MemoryStream()));
    }

    [Fact]
    public void TheTable_isTheFirstPlanDependent_onlyWhenTheDocumentHoldsAReference()
    {
        var with = ABandReplacedDuringTheRender(reference: true).Renderer;
        var without = ABandReplacedDuringTheRender(reference: false).Renderer;
        with.RecordPlacements = true;
        without.RecordPlacements = true;

        // Both lay out; the one with a reference needs more than one measuring run to settle... or
        // the same single run when its label is the placeholder's. What differs is the guard.
        Assert.ThrowsAny<InvalidOperationException>(() => with.RunLayout());
        without.RunLayout();
        Assert.Equal(1, without.MeasureRuns);
        Assert.NotNull(with.ReferenceTable);
        Assert.Null(without.ReferenceTable);
    }
}
