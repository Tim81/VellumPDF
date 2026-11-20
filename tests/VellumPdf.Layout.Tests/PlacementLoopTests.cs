// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCheck;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #288: the one placement loop, the placement reports a heading makes from it, the plan they
/// build, and the measuring loop that resolves plan-dependent renderers. Everything here reads
/// internals (<see cref="DocumentRenderer.MeasureRuns"/>, <see cref="DocumentRenderer.LayoutCalls"/>,
/// the plans), which no public member exposes.
/// </summary>
public sealed class PlacementLoopTests
{
    // ── Test renderers ───────────────────────────────────────────────────────

    /// <summary>A block of fixed height.</summary>
    private sealed class Block(double height) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            context.Area.Height < height
                ? LayoutResult.Nothing()
                : LayoutResult.Full(context.Area.WithHeight(height));

        public void Draw(DrawContext context) { }
    }

    /// <summary>
    /// A plan-dependent renderer whose text is the label of its target's page, and whose height
    /// depends on that label. It caches the height and drops the cache when <see cref="Resolve"/>
    /// reports a change, as <see cref="IPlanDependent"/> says it must.
    /// </summary>
    private sealed class LabelDependent(object target, Func<string, double> heightOf) : IRenderer, IPlanDependent
    {
        private string _text = "";
        private double? _height;

        public int ResolveCalls { get; private set; }
        public string TargetName => "target";
        public string ResolvedText => _text;

        public bool Resolve(PaginationPlan plan)
        {
            ResolveCalls++;
            plan.TryGetTarget(target, out var page, out _);
            var text = plan.LabelOf(page);
            if (text == _text) return false;
            _text = text;
            _height = null;
            return true;
        }

        public LayoutResult Layout(LayoutContext context)
        {
            _height ??= heightOf(_text);
            return context.Area.Height < _height
                ? LayoutResult.Nothing()
                : LayoutResult.Full(context.Area.WithHeight(_height.Value));
        }

        public void Draw(DrawContext context) { }
    }

    /// <summary>A plan-dependent renderer that takes the next string of a script on each resolve.</summary>
    private sealed class ScriptedDependent(params string[] script) : IRenderer, IPlanDependent
    {
        private string _text = "";

        public string TargetName => "scripted";
        public string ResolvedText => _text;

        public bool Resolve(PaginationPlan plan)
        {
            var next = script[Math.Min(Resolves++, script.Length - 1)];
            if (next == _text) return false;
            _text = next;
            return true;
        }

        public int Resolves { get; private set; }
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(10));
        public void Draw(DrawContext context) { }
    }

    private static Heading NewHeading(string text) => new(text, Body) { Margins = EdgeInsets.Zero };

    private static DocumentRenderer NewRenderer(PdfDocument pdf, RunningBand? footer = null) =>
        new(pdf, Page, Margins) { Footer = footer, RecordPlacements = true };

    // ── Plan correctness ─────────────────────────────────────────────────────

    private static readonly Regex DestPattern = new(
        @"/Dest \[(\d+) 0 R /XYZ (\S+) (\S+) null\]", RegexOptions.CultureInvariant);

    /// <summary>The page index and PDF y each outline entry of the file points at, in entry order.</summary>
    private static List<(int PageIndex, double PdfY)> OutlineDests(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pages = Regex.Matches(kids, @"(\d+) 0 R")
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
        return DestPattern.Matches(text)
            .Select(m => (
                pages.IndexOf(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)),
                double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)))
            .ToList();
    }

    /// <summary>
    /// Headings among filler, one of them too long for the room left on its page, so it splits.
    /// Returns the headings in document order.
    /// </summary>
    private static List<Heading> HeadingDocument(DocumentRenderer renderer)
    {
        var headings = new List<Heading>
        {
            NewHeading("First"),
            NewHeading(string.Join(' ', Enumerable.Repeat("Split", 40))),
            NewHeading("Third"),
        };
        renderer.Add(new HeadingRenderer(headings[0]));
        renderer.Add(new Block(190));
        renderer.Add(new HeadingRenderer(headings[1]));
        renderer.Add(new Block(120));
        renderer.Add(new Block(120));
        renderer.Add(new HeadingRenderer(headings[2]));
        return headings;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeadingPlan_equalsTheOutlineDestination_includingASplitHeading(bool withBand)
    {
        var pdf = NewPdf();
        var renderer = NewRenderer(pdf, withBand ? new RunningBand("Page {page}") : null);
        var headings = HeadingDocument(renderer);
        var bytes = Render(renderer);

        // Two independent routes to one fact: the placement loop's report and the /Dest the draw
        // pass wrote. A plan from the measuring run exists only with a band.
        var plans = new List<PaginationPlan> { renderer.DrawPlan! };
        if (withBand) plans.Add(renderer.MeasurePlan!);
        Assert.Equal(withBand ? 2 : 1, plans.Count);

        var dests = OutlineDests(bytes);
        Assert.Equal(3, dests.Count);
        Assert.True(PageCount(bytes) >= 3, "the fixture must span at least three pages");

        foreach (var plan in plans)
        {
            for (var i = 0; i < headings.Count; i++)
            {
                Assert.True(plan.TryGetTarget(headings[i], out var page, out var y), $"heading {i} has no placement");
                Assert.Equal(dests[i].PageIndex, page);
                Assert.Equal(dests[i].PdfY, Page.Height - y, 4);
            }
        }

        // The second heading is the one that splits: it starts where the filler left the cursor,
        // and its whole height would run past the bottom of the content area.
        plans[0].TryGetTarget(headings[1], out _, out var splitY);
        var whole = new HeadingRenderer(headings[1])
            .Layout(new LayoutContext(new LayoutBox(20, 0, 260, 10_000))).OccupiedArea!.Value.Height;
        Assert.True(splitY + whole > Page.Height - Margins.Bottom, $"{splitY} + {whole} does not overflow the page");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHeadingThatFindsNoRoom_isPlannedAtTheTopOfTheNextPage(bool withBand)
    {
        // 5pt are left under the block, less than one line, so the heading answers Nothing and is
        // laid out again on the next page. The report belongs to the result that was accepted.
        var renderer = NewRenderer(NewPdf(), withBand ? new RunningBand("f") : null);
        var heading = NewHeading("Late");
        renderer.Add(new Block(withBand ? 236 : 255));
        renderer.Add(new HeadingRenderer(heading));
        var bytes = Render(renderer);

        var plan = renderer.DrawPlan!;
        Assert.True(plan.TryGetTarget(heading, out var page, out var y));
        Assert.Equal(1, page);
        Assert.Equal(Margins.Top, y);
        Assert.Single(plan.Placements);
        var dest = Assert.Single(OutlineDests(bytes));
        Assert.Equal((page, Page.Height - y), dest);
    }

    [Fact]
    public void MeasurePlan_andDrawPlan_reportTheSamePlacements()
    {
        var renderer = NewRenderer(NewPdf(), new RunningBand("Page {page}"));
        HeadingDocument(renderer);
        Render(renderer);

        var measured = renderer.MeasurePlan!.Placements;
        var drawn = renderer.DrawPlan!.Placements;
        Assert.Equal(3, measured.Count);
        Assert.Equal(
            measured.Select(p => (p.Key, p.PageIndex, p.Y)).ToList(),
            drawn.Select(p => (p.Key, p.PageIndex, p.Y)).ToList());
        Assert.Equal(renderer.MeasurePlan!.PageCount, renderer.DrawPlan!.PageCount);
    }

    [Fact]
    public void PlanIndices_startAtZero_whenTheKernelDocumentAlreadyHoldsPages()
    {
        var pdf = NewPdf();
        pdf.AddPage(Page);
        var renderer = NewRenderer(pdf, new RunningBand("{page}/{pages}"));
        var first = NewHeading("First");
        renderer.Add(new HeadingRenderer(first));
        var bytes = Render(renderer);

        Assert.Equal(2, pdf.Pages.Count);
        foreach (var plan in new[] { renderer.MeasurePlan!, renderer.DrawPlan! })
        {
            Assert.True(plan.TryGetTarget(first, out var page, out _));
            Assert.Equal(0, page);
        }

        // The destination is on the second page of the file: index 0 of the layout is index 1 of
        // the document.
        Assert.Equal(1, Assert.Single(OutlineDests(bytes)).PageIndex);
    }

    // ── Counters ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, 36, 0)]
    [InlineData(true, 68, 1)]
    public void Counters_forADocumentWithoutADependent(bool withBand, int layoutCalls, int measureRuns)
    {
        var renderer = NewRenderer(NewPdf(), withBand ? new RunningBand("Page {page}") : null);
        foreach (var r in StandardContent())
            renderer.Add(r);
        Render(renderer);

        Assert.Equal(layoutCalls, renderer.LayoutCalls);
        Assert.Equal(measureRuns, renderer.MeasureRuns);
        Assert.Equal(withBand, renderer.MeasurePlan is not null);
    }

    [Fact]
    public void ThousandParagraphs_makeTheSameLayoutCallsAsBefore_andOneMeasureRunWithAFooter()
    {
        foreach (var (band, calls, runs) in new[] { (false, 1076, 0), (true, 2116, 1) })
        {
            var renderer = new DocumentRenderer(NewPdf(), new PdfRectangle(0, 0, 595, 842), new EdgeInsets(40))
            {
                Footer = band ? new RunningBand("Page {page}") : null,
            };
            for (var i = 0; i < 1000; i++)
                renderer.Add(new ParagraphRenderer(new Paragraph(ParagraphText(i), Body)));
            Render(renderer);

            Assert.Equal(calls, renderer.LayoutCalls);
            Assert.Equal(runs, renderer.MeasureRuns);
        }
    }

    [Fact]
    public void ADependentGetsAtLeastTwoMeasureRuns_andANonDependentParagraphWrapsOnce()
    {
        var paragraph = new ParagraphRenderer(new Paragraph(ParagraphText(0), Body));
        var dependent = new ScriptedDependent("a", "b");
        var renderer = NewRenderer(NewPdf());
        renderer.Add(paragraph);
        renderer.Add(dependent);
        Render(renderer);

        // Run 1 resolves to "a", run 2 to "b", and run 3 resolves nothing new.
        Assert.Equal(3, renderer.MeasureRuns);
        Assert.Equal(3, dependent.Resolves);
        Assert.Equal(1, paragraph.WrapCalls);
        // Three measuring runs and the drawing pass: one call each.
        Assert.Equal(8, renderer.LayoutCalls);
    }

    [Fact]
    public void ADependentThatNeverChangesAfterTheFirstRun_stillGetsASecondRun()
    {
        var dependent = new ScriptedDependent("a");
        var renderer = NewRenderer(NewPdf());
        renderer.Add(dependent);
        Render(renderer);

        Assert.Equal(2, renderer.MeasureRuns);
        Assert.Equal(2, dependent.Resolves);
    }

    [Fact]
    public void LayoutCallsAndMeasureRuns_startOverOnEveryRunLayout()
    {
        var pdf = NewPdf();
        var renderer = NewRenderer(pdf, new RunningBand("f"));
        renderer.Add(new Block(10));
        renderer.RunLayout();
        Assert.Equal((2, 1), (renderer.LayoutCalls, renderer.MeasureRuns));

        // A second layout of the same renderer starts its counters over; accumulating would read
        // (4, 2).
        renderer.RunLayout();
        Assert.Equal((2, 1), (renderer.LayoutCalls, renderer.MeasureRuns));
    }

    [Fact]
    public void WithoutADependentAndWithoutRecording_nothingIsCollected_butThePageCountsAre()
    {
        var renderer = NewRenderer(NewPdf(), new RunningBand("f"));
        renderer.RecordPlacements = false;
        renderer.Add(new HeadingRenderer(NewHeading("Unrecorded")));
        var bytes = Render(renderer);

        Assert.Empty(renderer.DrawPlan!.Placements);
        Assert.Empty(renderer.MeasurePlan!.Placements);
        Assert.Equal(1, renderer.DrawPlan.PageCount);
        Assert.Equal(1, PageCount(bytes));
    }

    // ── Convergence and the cap ──────────────────────────────────────────────

    // The label of the target's page decides the dependent's height, and the heading comes after the
    // dependent, so each height moves the heading to the other page. With the filler, a short
    // dependent leaves room for the heading on page 1; a tall one does not.
    private static (DocumentRenderer Renderer, LabelDependent Dependent, Heading Target) OscillatingDocument(bool oscillate)
    {
        var target = NewHeading("Target");
        // Odd label (page 1): tall. Even label: short. Alternates when oscillating; otherwise the
        // height ignores the label and the document settles at once.
        var dependent = new LabelDependent(target, label => !oscillate ? 40 : (label == "1" ? 80 : 40));
        var renderer = NewRenderer(NewPdf());
        renderer.Add(new Block(200));
        renderer.Add(dependent);
        renderer.Add(new HeadingRenderer(target));
        return (renderer, dependent, target);
    }

    [Fact]
    public void OscillatingDependent_isRefusedAfterExactlyMaxMeasureRuns_namingTheTargetAndEveryString()
    {
        var (renderer, dependent, _) = OscillatingDocument(oscillate: true);

        var ex = Assert.Throws<InvalidOperationException>(() => Render(renderer));

        Assert.Equal(5, DocumentRenderer.MaxMeasureRuns);
        Assert.Equal(DocumentRenderer.MaxMeasureRuns, renderer.MeasureRuns);
        Assert.Equal(DocumentRenderer.MaxMeasureRuns, dependent.ResolveCalls);
        Assert.Equal(
            "Pagination did not settle after 5 layout passes: the text for 'target' read " +
            "'1', '2', '1', '2', '1'. A string that depends on where its target lands can change " +
            "the layout and so move that target, and here the passes keep changing it instead of " +
            "settling. The renderers have already measured, so lay the content out again with a " +
            "new Document.",
            ex.Message);
        // The drawing pass never ran: the document was refused before any page was added.
        Assert.Null(renderer.DrawPlan);
    }

    /// <summary>A dependent whose text is taken from <paramref name="textOf"/> by resolve number.</summary>
    private sealed class NamedDependent(string name, Func<int, string> textOf) : IRenderer, IPlanDependent
    {
        private string _text = "";
        private int _resolves;

        public string TargetName => name;
        public string ResolvedText => _text;

        public bool Resolve(PaginationPlan plan)
        {
            var next = textOf(_resolves++);
            if (next == _text) return false;
            _text = next;
            return true;
        }

        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(10));
        public void Draw(DrawContext context) { }
    }

    private static string Flip(int n) => n % 2 == 0 ? "a" : "b";

    [Fact]
    public void FourOscillatingDependents_areRefused_namingExactlyThree()
    {
        var renderer = NewRenderer(NewPdf());
        for (var i = 0; i < 4; i++)
            renderer.Add(new NamedDependent($"d{i}", Flip));

        var ex = Assert.Throws<InvalidOperationException>(() => Render(renderer));

        Assert.Contains("the text for 'd0' read 'a', 'b', 'a', 'b', 'a'; ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the text for 'd1' read", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the text for 'd2' read", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'d3'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(", and 1 more. ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettledDependentBesideAnOscillatingOne_isNotNamed()
    {
        var renderer = NewRenderer(NewPdf());
        renderer.Add(new NamedDependent("settled", _ => "x"));
        renderer.Add(new NamedDependent("moving", Flip));

        var ex = Assert.Throws<InvalidOperationException>(() => Render(renderer));

        Assert.Contains("'moving'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'settled'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("more.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Full at 250pt on its even calls and at 20pt on its odd ones.</summary>
    private sealed class TallThenShort : IRenderer
    {
        private int _calls;
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(context.Area.WithHeight(_calls++ % 2 == 0 ? 250 : 20));
        public void Draw(DrawContext context) { }
    }

    [Fact]
    public void APageCountThatKeepsChanging_isNamedWhenNoDependentChanged()
    {
        var renderer = NewRenderer(NewPdf());
        renderer.Add(new NamedDependent("settled", _ => "x"));
        renderer.Add(new TallThenShort());
        renderer.Add(new Block(100));

        var ex = Assert.Throws<InvalidOperationException>(() => Render(renderer));

        Assert.StartsWith(
            "Pagination did not settle after 5 layout passes: the page count read 2, 1, 2, 1, 2. ",
            ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADependentThatSettles_isDrawnWithItsResolvedStrings_andSaves()
    {
        var (renderer, dependent, target) = OscillatingDocument(oscillate: false);

        var bytes = Render(renderer);

        Assert.Equal(2, renderer.MeasureRuns);
        Assert.Equal("1", dependent.ResolvedText);
        Assert.True(renderer.DrawPlan!.TryGetTarget(target, out var page, out _));
        Assert.Equal(0, page);
        Assert.Equal(1, PageCount(bytes));
    }

    [Fact]
    public void OscillatingDependent_throughDocument_failsTheSave_andASecondSaveSaysItCannotBeSavedAgain()
    {
        using var doc = new Document { PageSize = Page, Margins = Margins };
        var target = NewHeading("Target");
        doc.Add(new Block(200));
        doc.Add(new LabelDependent(target, label => label == "1" ? 80 : 40));
        doc.Add(target);

        var first = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.StartsWith("Pagination did not settle after 5 layout passes", first.Message, StringComparison.Ordinal);

        var second = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.StartsWith("This document cannot be saved again", second.Message, StringComparison.Ordinal);
    }

    // ── The determinism guard ────────────────────────────────────────────────

    /// <summary>
    /// Answers 20pt for its first <paramref name="steadyCalls"/> calls and 250pt afterwards: the
    /// measuring runs see one document and the drawing pass another. A custom renderer may do this;
    /// only a document with a plan-dependent renderer refuses it.
    /// </summary>
    private sealed class ChangesItsMind(int steadyCalls) : IRenderer
    {
        private int _calls;

        public LayoutResult Layout(LayoutContext context)
        {
            var height = _calls++ < steadyCalls ? 20 : 250;
            return LayoutResult.Full(context.Area.WithHeight(height));
        }

        public void Draw(DrawContext context) { }
    }

    [Fact]
    public void ARendererThatPaginatesDifferentlyInTheDrawingPass_isRefused_whenADependentExists()
    {
        var renderer = NewRenderer(NewPdf());
        renderer.Add(new ScriptedDependent("a"));
        renderer.Add(new ChangesItsMind(steadyCalls: 2));
        renderer.Add(new HeadingRenderer(NewHeading("After")));

        var ex = Assert.Throws<InvalidOperationException>(() => Render(renderer));

        Assert.StartsWith("Pagination changed between the measuring and the drawing pass", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARendererThatPaginatesDifferentlyInTheDrawingPass_stillSaves_withABandAndNoDependent()
    {
        var pdf = NewPdf();
        var renderer = NewRenderer(pdf, new RunningBand("{page} of {pages}"));
        renderer.Add(new ChangesItsMind(steadyCalls: 1));
        renderer.Add(new HeadingRenderer(NewHeading("After")));

        var bytes = Render(renderer);

        // One page counted, two drawn: a valid file whose {pages} is wrong, as before the guard
        // existed.
        Assert.Equal(1, renderer.MeasurePlan!.PageCount);
        Assert.Equal(2, renderer.DrawPlan!.PageCount);
        Assert.Equal(2, PageCount(bytes));
    }

    // ── Measured pages against drawn pages ───────────────────────────────────

    // Built-in elements only, with a footer so that both passes run, and a heading at the end so
    // the plans hold a placement. The generator is seeded and run on one thread, so the same
    // documents come back every time; documents the layout refuses are counted, not skipped
    // silently.
    [Fact]
    public void GeneratedDocuments_measureTheSamePagesAndPlacementsAsTheyDraw()
    {
        const int iterations = 200;
        var refused = 0;
        var compared = 0;

        LayoutGen.ValidDoc.Sample(spec =>
        {
            using var doc = LayoutGen.Build(spec);
            doc.Footer ??= new RunningBand("{page}/{pages}");
            var heading = new Heading("End");
            doc.Add(heading);
            var renderer = doc.CreateRenderer();
            renderer.RecordPlacements = true;

            try
            {
                renderer.RunLayout();
            }
            catch (InvalidOperationException)
            {
                refused++;
                return;
            }

            var measured = renderer.MeasurePlan!;
            var drawn = renderer.DrawPlan!;
            Assert.Equal(1, renderer.MeasureRuns);
            Assert.Equal(measured.PageCount, drawn.PageCount);
            Assert.Equal(
                measured.Placements.Select(p => (p.Key, p.PageIndex, p.Y)).ToList(),
                drawn.Placements.Select(p => (p.Key, p.PageIndex, p.Y)).ToList());
            Assert.True(drawn.TryGetTarget(heading, out var page, out _));
            Assert.InRange(page, 0, drawn.PageCount - 1);
            compared++;
        }, seed: "0N0XIzNsQ0O2", iter: iterations, threads: 1);

        Assert.Equal(iterations, refused + compared);
        Assert.True(compared > iterations / 2, $"only {compared} of {iterations} documents were laid out");
    }

    // ── Diagnostics come from the drawing pass only ──────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasuringRunsReportNoDiagnostics_soACutFooterIsReportedOnce(bool withDependent)
    {
        using var doc = new Document { PageSize = Page, Margins = Margins };
        var style = new TextStyle { FontSize = 10 };
        doc.Footer = new RunningBand(new string('Æ', 40), style);
        doc.Add(new Paragraph("body", style));
        if (withDependent)
            doc.Add(new NamedDependent("settled", n => n == 0 ? "a" : "b"));

        doc.Save(new MemoryStream());

        // One footer and a dependent that takes three measuring runs still give one report, with
        // a count of one, as with no dependent and a single run.
        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.BandTruncated, item.Code);
        Assert.Equal(1, item.Count);
        Assert.Equal(1, item.PageNumber);
    }

    // ── Retry, through Document ──────────────────────────────────────────────

    private sealed class NothingRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Nothing();
        public void Draw(DrawContext context) { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASaveThatFailsInTheMeasuringOrTheDrawingPass_cannotBeRetried(bool withBand)
    {
        using var doc = new Document { PageSize = Page, Margins = Margins };
        if (withBand) doc.SetFooter("f");
        doc.Add(new Paragraph("before", Body));
        doc.Add(new NothingRenderer());

        var first = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.Contains("too tall", first.Message, StringComparison.Ordinal);

        var second = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.StartsWith("This document cannot be saved again", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedContentArea_isRetryable_andTheRetrySavesOnce()
    {
        using var doc = new Document { PageSize = Page, Margins = Margins };
        doc.Footer = new RunningBand("f") { Height = 400 };
        doc.Add(new Paragraph("text", Body));

        var refusal = Assert.Throws<ArgumentException>(() => doc.Save(new MemoryStream()));
        Assert.Equal("margins", refusal.ParamName);

        doc.Footer = null;
        using var ms = new MemoryStream();
        doc.Save(ms);

        Assert.Equal(1, PageCount(ms.ToArray()));
    }

    // ── Oracles ──────────────────────────────────────────────────────────────

    private static byte[] BandedHeadingDocument(out PaginationPlan plan, out List<Heading> headings)
    {
        var renderer = NewRenderer(NewPdf(), new RunningBand("Page {page}"));
        headings = HeadingDocument(renderer);
        var bytes = Render(renderer);
        plan = renderer.MeasurePlan!;
        return bytes;
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vellum-placement-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Qpdf_checksTheFile_andItsOutlinePagesAreThePlansPages()
    {
        var bytes = BandedHeadingDocument(out var plan, out var headings);
        var path = WriteTemp(bytes);
        try
        {
            ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
            Assert.False(timedOut, "qpdf --check timed out, or its output could not be fully captured.");
            Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
            Assert.True(exit == 0, $"qpdf --check exited {exit}.\n{stdout}\n{stderr}");

            ExternalTool.TryRun("qpdf", ["--json", "--json-key=outlines", "--json-key=pages", path],
                out exit, out var json, out stderr, out timedOut);
            Assert.False(timedOut, "qpdf --json timed out.");
            Assert.True(exit == 0, $"qpdf --json exited {exit}.\n{stderr}");

            using var document = JsonDocument.Parse(json);
            var pages = document.RootElement.GetProperty("pages").EnumerateArray()
                .Select(p => p.GetProperty("object").GetString()!)
                .ToList();
            var outlines = document.RootElement.GetProperty("outlines").EnumerateArray().ToList();
            Assert.Equal(headings.Count, outlines.Count);
            for (var i = 0; i < headings.Count; i++)
            {
                var destination = outlines[i].GetProperty("dest")[0].GetString()!;
                Assert.True(plan.TryGetTarget(headings[i], out var planned, out _));
                Assert.Equal(planned, pages.IndexOf(destination));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pdftotext_findsOneFormFeedPerPage_andTheBandsLastPageNumber()
    {
        var bytes = BandedHeadingDocument(out var plan, out _);
        var path = WriteTemp(bytes);
        try
        {
            ExternalTool.TryRun("pdftotext", ["-layout", path, "-"], out var exit, out var text, out var stderr, out var timedOut);
            Assert.False(timedOut, "pdftotext timed out.");
            Assert.True(exit == 0, $"pdftotext exited {exit}.\n{stderr}");

            // The measuring run's page count is the number of pages the file has, and the last
            // page carries the band that names it.
            Assert.Equal(plan.PageCount, text.Count(c => c == '\f'));
            Assert.Contains($"Page {plan.PageCount}", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
