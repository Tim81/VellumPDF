// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// DocumentRenderer.PlaceRenderer and CountPlaceRenderer used to recurse once per page
/// continuation, so a single element spanning thousands of pages overflowed the CLR stack and
/// killed the process (#459). The too-tall cases also pin the two passes to one verdict (#460).
/// </summary>
public sealed class PaginationDepthTests
{
    private const string ElementTooTallMessage =
        "An element is too tall to fit on a single page and cannot be rendered. " +
        "Reduce the element's content or increase the page size.";

    // ── (a) Stack depth stays flat ───────────────────────────────────────────

    // No lower-bound guard on this constant (review round 4): a prior one asserted
    // ProbeContinuations > 1 on the theory that AssertFlat's checks pass trivially at 0 or 1.
    // Measured against a reverted-to-recursion DocumentRenderer, both stack-depth probes below
    // still fail at ProbeContinuations = 1 — one continuation already puts a second frame of
    // the same method on the stack, which AssertFlat's InRange(0, 1) already catches. At 0, the
    // file does not compile at all: Assert.Equal(ProbeContinuations + 1, ...) below folds to
    // Assert.Equal(1, ...) and xUnit2013 requires Assert.Single instead. So the constant a
    // regression could actually reach without breaking the build already discriminates, and the
    // guard was asserting a compile-time literal that could never fail.
    private const int ProbeContinuations = 200;

    [Fact]
    public void PlaceRenderer_manyContinuations_stackDepthStaysFlat()
    {
        var probe = Paginate(new StackDepthProbeRenderer(ProbeContinuations), withFooter: false);

        // One Layout call per continuation, plus the final one that fits. Asserted so the frame
        // check below cannot pass by never having run.
        Assert.Equal(ProbeContinuations + 1, probe.Samples.Count);
        AssertFlat(probe.Samples);
    }

    [Fact]
    public void CountPlaceRenderer_manyContinuations_stackDepthStaysFlat()
    {
        var probe = Paginate(new StackDepthProbeRenderer(ProbeContinuations), withFooter: true);

        // A footer makes RunLayout count pages before drawing them, so the whole walk happens
        // twice — once in each of the two methods that used to recurse.
        Assert.Equal(2 * (ProbeContinuations + 1), probe.Samples.Count);
        AssertFlat(probe.Samples);
    }

    /// <summary>
    /// Every Layout call must sit under exactly one pagination frame: the loops hold one, the
    /// recursion they replaced held one per continuation. This pins stack depth under the JIT
    /// configuration CI actually runs, tiered compilation on, which is the default. It is not a
    /// claim that recursion can never be flattened. Measured against the pre-fix renderer in
    /// Release with <c>DOTNET_TieredCompilation=0</c>: RyuJIT emits the tail call for
    /// <c>PlaceRenderer</c>, so its probe passes and the no-footer deep-list test renders; but
    /// <c>CountPlaceRenderer</c> takes <c>ref</c> parameters, which block a fast tail call, so
    /// its probe still fails and the footer deep-list test still dies of stack overflow. Half the
    /// file therefore keeps working on the setting that defeats the other half. Release matters in
    /// that sentence: a Debug assembly carries <c>DebuggableAttribute</c> with optimisations off,
    /// so the JIT never flattens anything and the setting changes nothing at all.
    /// </summary>
    private static void AssertFlat(IReadOnlyList<(int Place, int Count)> samples)
    {
        // Each frame is present (1) only while its own pass is on the stack and absent (0)
        // otherwise — e.g. the no-footer probe never runs CountPlaceRenderer at all, so its
        // Count component is 0 throughout. Bounding the two components separately, rather than
        // asserting their summed value, means a regression confined to a single loop (say,
        // PlaceRenderer alone growing past 1) is reported against that component by name instead
        // of an ambiguous "the sum of both moved" — which would surface inside
        // CountPlaceRenderer_manyContinuations_stackDepthStaysFlat too, since it runs PlaceRenderer
        // as its second pass, and wrongly read as CountPlaceRenderer's fault (review round 2, #459).
        // The trailing sum check still catches the case neither assertion above would: both
        // frames present, or neither, at the same sample. Bounds rather than Assert.All, because
        // a recursion regression otherwise prints one near-identical failure block per sample —
        // measured at 200 blocks and 34,670 characters for a single test.
        Assert.InRange(samples.Max(s => s.Place), 0, 1);
        Assert.InRange(samples.Max(s => s.Count), 0, 1);
        Assert.Equal(1, samples.Min(s => s.Place + s.Count));
        Assert.Equal(1, samples.Max(s => s.Place + s.Count));
    }

    private static StackDepthProbeRenderer Paginate(StackDepthProbeRenderer probe, bool withFooter)
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10));
        if (withFooter)
            renderer.Footer = new RunningBand("f");
        renderer.Add(probe);

        using var ms = new MemoryStream();
        renderer.Render(ms);
        return probe;
    }

    /// <summary>
    /// Splits a fixed number of times, recording how many PlaceRenderer and CountPlaceRenderer
    /// frames are on the stack at each Layout call. Each overflow is a fresh instance sharing the
    /// same recording list, as the library's renderers do, so a second pass repeats the walk from
    /// the start instead of resuming a spent counter.
    /// </summary>
    private sealed class StackDepthProbeRenderer : IRenderer
    {
        private readonly int _remaining;
        private readonly List<(int Place, int Count)> _samples;

        public StackDepthProbeRenderer(int continuations) : this(continuations, []) { }

        private StackDepthProbeRenderer(int remaining, List<(int Place, int Count)> samples)
        {
            _remaining = remaining;
            _samples = samples;
        }

        public IReadOnlyList<(int Place, int Count)> Samples => _samples;

        public LayoutResult Layout(LayoutContext context)
        {
            var frames = new StackTrace(false).GetFrames() ?? [];
            _samples.Add((
                frames.Count(f => f.GetMethod()?.Name == "PlaceRenderer"),
                frames.Count(f => f.GetMethod()?.Name == "CountPlaceRenderer")));

            var occupied = context.Area.WithHeight(0);
            return _remaining > 0
                ? LayoutResult.Partial(occupied, this, new StackDepthProbeRenderer(_remaining - 1, _samples))
                : LayoutResult.Full(occupied);
        }

        // DrawContext's constructor is pure field assignment, so a no-op Draw is safe here.
        public void Draw(DrawContext context) { }
    }

    // ── (b) A deep render completes ───────────────────────────────────────────

    // Under a recursion regression these two tests do not fail cleanly the way the stack-depth
    // probes above do: a CLR stack overflow is uncatchable, so the process dies mid-run with no
    // results XML and no test name attached to the failure, and observed ordering runs these
    // before the probes, so the probes never get a chance to name the regression first. Kept
    // anyway, because they are the only end-to-end proof that a ten-thousand-page document
    // actually renders; the probes only pin frame counts on a synthetic renderer that never
    // creates a PdfPage (review round 2, #459).

    // Geometry chosen so exactly one list entry fits per page:
    //   no-footer content height = 200 - 2*55 = 90
    //   with-footer content height = 200 - 2*55 - RunningBand.EffectiveHeight
    //                               = 90 - (8 * 1.2 + 4) = 90 - 13.6 = 76.4
    //   EffectiveLeading = 50 (explicit, larger than FontSize * 1.2)
    //   maxLines = floor(90 / 50) = floor(76.4 / 50) = 1 either way
    // 3,334 ListItems x (1 item + 2 AddChild) = 10,002 flattened entries -> 10,002 pages
    // The 20,000pt width is deliberate: it makes wrapping irrelevant, so every entry costs
    // exactly one line. It is wider than the 14,400 unit maximum ISO 32000-1 Annex C
    // recommended, but that was a "should" for conforming writers and ISO 32000-2 drops the
    // table altogether, so nothing here is out of conformance.
    // in both variants, since both content heights floor to the same one-line-per-page cap.
    private const int ListItemCount = 3_334;
    private const int ExpectedPages = 10_002;

    [Fact]
    public void DocumentRenderer_deepList_producesExpectedPageCount()
    {
        var pageCount = RenderDeepList(withFooter: false);
        Assert.Equal(ExpectedPages, pageCount);
    }

    [Fact]
    public void DocumentRenderer_deepList_withFooter_producesExpectedPageCount()
    {
        // A footer makes RunLayout run CountPlaceRenderer before PlaceRenderer, so this drives
        // both of the loops that used to recurse (#459) over a ten-thousand-page document.
        var pageCount = RenderDeepList(withFooter: true);
        Assert.Equal(ExpectedPages, pageCount);
    }

    private static int RenderDeepList(bool withFooter)
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 20_000, 200), new EdgeInsets(55));
        if (withFooter)
            renderer.Footer = new RunningBand("{page}/{pages}", style: new TextStyle { FontSize = 8 });

        var style = new TextStyle { FontSize = 36, Leading = 50 };
        var list = new ListElement(ListStyle.Unordered) { DefaultStyle = style };
        for (var i = 0; i < ListItemCount; i++)
        {
            var item = new ListItem("x");
            item.AddChild("y");
            item.AddChild("z");
            list.Add(item);
        }

        renderer.Add(new ListRenderer(list));

        using var ms = new MemoryStream();
        renderer.Render(ms);

        return pdf.Pages.Count;
    }

    // ── (c) The continuation cap throws ──────────────────────────────────────

    // Generous relative to the ~50,001 real page objects the PlaceRenderer case creates (measured ~75-120ms,
    // ~62MB allocated). Without a timeout, deleting the cap check makes both tests loop forever,
    // but the two are not equally inert while they do: pass 1 (CountPlaceRenderer) creates no PDF
    // objects, so there is no allocation growth to trip an OOM, and it just burns the CI
    // job to the workflow timeout with no attributable test name. Pass 2 (PlaceRenderer) is not
    // inert the same way — every turn creates a real page — and measured working set reaches
    // 2.0-2.6GB within this timeout under a cap-removed mutation, so on a memory-tight runner an
    // OOM could beat the timeout instead of the workflow-level one (review round 2, #459).
    private const int CapTestTimeoutMs = 10_000;

    // xUnit1069 wants TestContext.Current.CancellationToken threaded through so a Timeout can end
    // the test promptly; DocumentRenderer.Render takes no CancellationToken, and there is nothing
    // to thread it into (same pattern as MalformedInputTests' CmapFormat4 case).
#pragma warning disable xUnit1069
    [Fact(Timeout = CapTestTimeoutMs)]
    public void CountPlaceRenderer_nonAdvancingOverflow_throwsAfterCap()
    {
        var pdf = new PdfDocument();
        // Footer set so pass 1 (CountPlaceRenderer) runs first. It never creates PDF objects
        // and never reads OccupiedArea on its Partial branch, so 50,001 trivial turns are cheap.
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10))
        {
            Footer = new RunningBand("f"),
        };
        renderer.Add(new NeverAdvancingRenderer());

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var ms = new MemoryStream();
            renderer.Render(ms);
        });

        // A plain Contains("50000", ...) also matches "500000", so it cannot tell the cap constant
        // apart from a tenfold-raised one. Pinning the surrounding words makes the digit run a
        // whole token: "500000 page" does not contain "50000 page" as a substring.
        Assert.Contains("more than 50000 page continuations", ex.Message);

        // Both passes throw this same message, so the message alone cannot say which one did it.
        // Pass 1 creates no page objects, so an empty document is what identifies it: without
        // this, narrowing RunLayout's band gate so a footer-only document skips the counting
        // pass leaves this test green while pass 2 throws the identical text (round 5).
        Assert.Empty(pdf.Pages);
    }

    [Fact(Timeout = CapTestTimeoutMs)]
    public void PlaceRenderer_nonAdvancingOverflow_throwsAfterCap()
    {
        var pdf = new PdfDocument();
        // No header or footer, so RunLayout skips CountPages() entirely and this renderer's
        // overflow is placed straight through PlaceRenderer — the path every document with no
        // running bands takes, and the one the sibling test above never reaches (its footer makes
        // CountPlaceRenderer throw first, so pass 2 is never entered). Small page size keeps the
        // ~50,001 real page objects this creates cheap.
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10));
        renderer.Add(new NeverAdvancingRenderer());

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var ms = new MemoryStream();
            renderer.Render(ms);
        });

        Assert.Contains("more than 50000 page continuations", ex.Message);
    }

    /// <summary>
    /// A renderer whose overflow never shrinks — the third-party misbehaviour the continuation
    /// cap exists to catch, since every renderer in the library advances its own overflow (#459).
    /// </summary>
    private sealed class NeverAdvancingRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Partial(context.Area.WithHeight(0), this, this);

        public void Draw(DrawContext context) { }
    }

    // Both cap tests above drive the throw through the Partial branch, whose FinishCurrentPage
    // call has already run by the time the cap is checked — so the FinishCurrentPage(totalPages)
    // immediately before TooManyContinuations() is a no-op for them, and deleting it leaves all
    // other tests in this file green. Reaching the throw with a page still open needs the Nothing
    // branch instead, which only NothingThenFullRenderer below can drive (review round 4, #459).
    [Fact(Timeout = CapTestTimeoutMs)]
    public void PlaceRenderer_nonAdvancingOverflowViaNothingBranch_finishesLastPageBeforeCapThrows()
    {
        var pdf = new PdfDocument();
        // No header or footer, so this reaches PlaceRenderer directly, as the sibling test above
        // does for the same reason.
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10));
        renderer.Add(new NothingThenFullRenderer());

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var ms = new MemoryStream();
            renderer.Render(ms);
        });

        Assert.Contains("more than 50000 page continuations", ex.Message);

        // Canvas.Finish() is the only thing that turns ContentBytes from null into an array (see
        // the too-tall test below), so a mutation dropping the FinishCurrentPage call ahead of
        // this throw leaves the last page's canvas open and its ContentBytes null.
        Assert.NotNull(pdf.Pages[^1].ContentBytes);
    }

    /// <summary>
    /// Answers Nothing on odd Layout calls and Full on even ones, which drives the continuation cap
    /// through the Nothing branch rather than Partial (review round 4, #459). Real renderers give
    /// the same verdict for the same object on the same area every time, so this deliberately
    /// breaks IRenderer's documented purity contract. That is not the only route to that branch: a
    /// perfectly pure renderer reaches it too when an element's height sits within a rounding error
    /// of the content area, because the two boxes the loop and its retry probe offer are not bitwise
    /// equal (#463). This probe is simply the one that does not depend on that geometry.
    /// </summary>
    private sealed class NothingThenFullRenderer : IRenderer
    {
        private int _calls;

        public LayoutResult Layout(LayoutContext context) =>
            ++_calls % 2 != 0
                ? LayoutResult.Nothing()
                : LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context) { }
    }

    // ── (c2) The cap boundary is exact ───────────────────────────────────────

    // Mirrors DocumentRenderer's private MaxContinuationsPerElement (#459). NeverAdvancingRenderer
    // above throws whether the cap check is '>' or '>=', since it never stops splitting either
    // way — an off-by-one in the comparison only shifts *when* it throws, which neither existing
    // cap test observes. Pinning the exact threshold DocumentRenderer.cs claims (N continuations
    // render, N+1 throws) needs a renderer with a known, finite split count instead.
    private const int MaxContinuationsPerElement = 50_000;

    /// <summary>
    /// Splits exactly <paramref name="splits"/> times, then reports Full — unlike
    /// NeverAdvancingRenderer, which never stops (review round 4, #459).
    /// </summary>
    [Fact]
    public void ContinuationCap_isPerElement_notPerDocument()
    {
        // The cap's own comment calls it a ceiling per top-level element, and RunLayout gets that
        // by declaring the counter inside PlaceRenderer rather than on the renderer. Hoisting it
        // to a field would make a document of several ordinary elements throw where the documented
        // contract renders, and every other cap test uses a single Add, so nothing else here would
        // notice (review round 5, #459).
        const int perElement = 30_000;
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10));
        renderer.Add(new FixedSplitCountRenderer(perElement));
        renderer.Add(new FixedSplitCountRenderer(perElement));

        using var ms = new MemoryStream();
        renderer.Render(ms);

        // 60,000 continuations in total, over the cap, but 30,000 per element, under it. The two
        // elements share the last page of the first and the first page of the second, hence + 1.
        Assert.Equal((2 * perElement) + 1, pdf.Pages.Count);
    }

    private sealed class FixedSplitCountRenderer(int splits) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            var occupied = context.Area.WithHeight(0);
            return splits > 0
                ? LayoutResult.Partial(occupied, this, new FixedSplitCountRenderer(splits - 1))
                : LayoutResult.Full(occupied);
        }

        public void Draw(DrawContext context) { }
    }

    // No Timeout here, unlike the non-advancing tests above. FixedSplitCountRenderer counts
    // down from a fixed number, so this terminates even with the cap check deleted and has
    // no hang to guard against. A timeout would only add a wall-clock assertion, and it did:
    // 0.7s locally, cancelled at 10s on a CI runner executing seven test assemblies at once.
    // That is #400's failure mode, and rendering ~50,001 pages twice is what makes it slow.
    [Fact]
    public void PlaceRenderer_continuationCap_boundaryIsExact()
    {
        var pdf = new PdfDocument();
        // No header or footer, so this drives PlaceRenderer's own cap, as the non-advancing
        // test above does for the same reason.
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10));
        renderer.Add(new FixedSplitCountRenderer(MaxContinuationsPerElement));

        using (var ms = new MemoryStream())
            renderer.Render(ms);

        // One page per continuation, plus the final page the terminating Full occupies.
        Assert.Equal(MaxContinuationsPerElement + 1, pdf.Pages.Count);

        var pdfOverflow = new PdfDocument();
        var overflowRenderer = new DocumentRenderer(pdfOverflow, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10));
        overflowRenderer.Add(new FixedSplitCountRenderer(MaxContinuationsPerElement + 1));

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var ms = new MemoryStream();
            overflowRenderer.Render(ms);
        });
        Assert.Contains("more than 50000 page continuations", ex.Message);
    }

    // No Timeout here, unlike the non-advancing tests above. FixedSplitCountRenderer counts
    // down from a fixed number, so this terminates even with the cap check deleted and has
    // no hang to guard against. A timeout would only add a wall-clock assertion, and it did:
    // 0.7s locally, cancelled at 10s on a CI runner executing seven test assemblies at once.
    // That is #400's failure mode, and rendering ~50,001 pages twice is what makes it slow.
    [Fact]
    public void CountPlaceRenderer_continuationCap_boundaryIsExact()
    {
        var pdf = new PdfDocument();
        // Footer set so pass 1 (CountPlaceRenderer) is the one whose cap this pins; pass 2
        // (PlaceRenderer) then walks the same, freshly-instanced split chain again and must
        // also complete without error for the render to succeed at all.
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10))
        {
            Footer = new RunningBand("f"),
        };
        renderer.Add(new FixedSplitCountRenderer(MaxContinuationsPerElement));

        using (var ms = new MemoryStream())
            renderer.Render(ms);

        Assert.Equal(MaxContinuationsPerElement + 1, pdf.Pages.Count);

        var pdfOverflow = new PdfDocument();
        var overflowRenderer = new DocumentRenderer(pdfOverflow, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10))
        {
            Footer = new RunningBand("f"),
        };
        overflowRenderer.Add(new FixedSplitCountRenderer(MaxContinuationsPerElement + 1));

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var ms = new MemoryStream();
            overflowRenderer.Render(ms);
        });
        Assert.Contains("more than 50000 page continuations", ex.Message);

        // CountPlaceRenderer never calls EnsurePage, so if pass 1 is the one that throws here,
        // as it must — pass 2 never gets far enough to need 50,001 continuations of its own —
        // no PdfPage exists yet.
        Assert.Empty(pdfOverflow.Pages);
    }
#pragma warning restore xUnit1069

    // ── (d) Both passes agree an element is too tall ─────────────────────────

    [Fact]
    public void PlaceRenderer_tooTallElement_throwsInvalidOperation()
    {
        PdfDocument pdf = null!;
        var ex = Assert.Throws<InvalidOperationException>(() => RenderOversizedChart(withFooter: false, out pdf));
        Assert.Equal(ElementTooTallMessage, ex.Message);

        // No footer, so this is the only pass and it runs PlaceRenderer directly. EnsurePage adds
        // a PdfPage the moment a layout attempt begins, before Layout is even called — once for
        // the original attempt, once for the retry on a fresh page — so both throw and the two
        // committed (if half-drawn) pages remain in the document.
        Assert.Equal(2, pdf.Pages.Count);

        // "Remain in the document" is only useful to a caller that catches the throw if those
        // pages are actually saveable. PlaceRenderer's Nothing branch calls FinishCurrentPage
        // before this throw so the last page's content stream is closed out, not left pending on
        // a canvas nothing will ever flush.
        using var saveStream = new MemoryStream();
        pdf.Save(saveStream);
        Assert.True(saveStream.Length > 0);

        // Save() not throwing is necessary but not sufficient to pin the FinishCurrentPage call.
        // PdfDocument.Save coalesces a null ContentBytes to an empty array, and PieChartRenderer
        // never draws for this element anyway (both attempts answer Nothing, and only Full and
        // Partial reach DrawRenderer), so Save() succeeds with or without the call. Nor is an
        // empty page invalid: ISO 32000-2 Table 31 makes a page object's /Contents Optional, and
        // says an absent one means the page is empty. So this asserts the narrower thing the call
        // is actually for — Canvas.Finish() is the only thing that turns ContentBytes from null
        // into an array, so a mutation dropping the call leaves the last page unfinished.
        Assert.NotNull(pdf.Pages[^1].ContentBytes);
    }

    [Fact]
    public void CountPlaceRenderer_tooTallElement_throwsInvalidOperation()
    {
        // Footer set so pass 1 (CountPlaceRenderer) is the one that hits the too-tall element
        // and must throw the same exception PlaceRenderer would. Before the fix this pass
        // silently skipped the element and undercounted the document (#460).
        //
        // Both passes reach one producer by design, so the message alone cannot tell
        // "CountPlaceRenderer threw" apart from "the return the #460 fix replaced was restored, and
        // PlaceRenderer threw on the same element moments later instead". The page count can:
        // CountPlaceRenderer never calls EnsurePage, so if pass 1 is the one that throws, RunLayout
        // never reaches pass 2 and no PdfPage exists yet.
        //
        // The message names the bands here, because with a footer set they are what shrank the
        // content box and the element itself never changed. Asserted in full rather than by
        // substring: every figure is fixed by this fixture's own geometry, since RunningBand("f")
        // takes the default height of TextStyle.Default's leading plus four, which is
        // 12 * 1.2 + 4 = 18.4, against a 200pt page with 10pt margins, leaving 161.6.
        PdfDocument pdf = null!;
        var ex = Assert.Throws<InvalidOperationException>(() => RenderOversizedChart(withFooter: true, out pdf));
        Assert.Equal(
            "An element is too tall to fit on a single page and cannot be rendered. "
            + "Running bands reserve 18.4pt of the 200.0pt page height "
            + "(header 0.0pt, footer 18.4pt), leaving a content area 161.6pt tall. "
            + "Reduce the element's content, lower the band heights, or increase the page size.",
            // Points, not commas, on every machine: the message formats invariantly, so this
            // assertion does not depend on the runner's locale. It did before this commit.
            ex.Message);
        Assert.Empty(pdf.Pages);
    }

    private static void RenderOversizedChart(bool withFooter, out PdfDocument pdf)
    {
        pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10));
        if (withFooter)
            renderer.Footer = new RunningBand("f");

        // Content area is at most 200 - 20 = 180pt tall; a 300pt diameter (plus margins) cannot
        // fit on any single page, so PieChartRenderer.Layout returns Nothing both times it is
        // tried. It needs a positive-value slice or the validation ahead of the height check
        // throws ArgumentException instead, masking the case under test.
        var chart = new PieChart
        {
            Diameter = 300,
            Slices = [new PieSlice(1, ColorRgb.Black)],
        };
        renderer.Add(new PieChartRenderer(chart));

        using var ms = new MemoryStream();
        renderer.Render(ms);
    }

    // ── (e) Nothing-then-fits still counts as a page turn ────────────────────

    [Fact]
    public void CountPlaceRenderer_nothingThenFitsOnFreshPage_pagesTokenMatchesActualCount()
    {
        // Regresses the Nothing branch's pages++ (#460 review): a first element that partly
        // fills page 1, followed by one that does not fit in what remains but does fit on a
        // fresh page, must still be counted as a page turn.
        //
        // Deleting pages++ leaves every other test in the assembly passing, because the bug is
        // invisible to a page-count assertion taken from the real PDF: PlaceRenderer's own
        // EnsurePage/FinishCurrentPage calls are untouched, so pass 2 still creates the correct
        // number of pages. The only observable symptom is the {pages} token pass 1 hands to
        // pass 2 being one low, so this test reads the resolved footer text back out of the
        // content stream rather than trusting PdfDocument.Pages.Count alone.
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 300), new EdgeInsets(10))
        {
            Footer = new RunningBand("{page}/{pages}", style: new TextStyle { FontSize = 8 }),
        };

        // Content height = 300 - 2*10 - RunningBand.EffectiveHeight(FontSize 8) = 266.4.
        renderer.Add(new FixedHeightRenderer(200)); // fills page 1, leaving 66.4pt in the remainder
        var chart = new PieChart
        {
            Diameter = 100, // > the 66.4pt remainder, but <= the 266.4pt full content height
            Margins = new EdgeInsets(0),
            Slices = [new PieSlice(1, ColorRgb.Black)],
        };
        // Nothing in page 1's remainder, then Full when retried on a fresh page — the exact
        // branch CountPlaceRenderer's pages++ guards.
        renderer.Add(new PieChartRenderer(chart));

        using var ms = new MemoryStream();
        renderer.Render(ms);

        Assert.Equal(2, pdf.Pages.Count);

        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        Assert.Equal(1, PdfTestUtil.CountOccurrences(decompressed, "(1/2) Tj"));
        Assert.Equal(1, PdfTestUtil.CountOccurrences(decompressed, "(2/2) Tj"));
    }

    /// <summary>
    /// Occupies exactly <paramref name="height"/> of the available area when it fits, otherwise
    /// reports Nothing — a minimal element for pinning the Nothing-then-fits retry path without
    /// depending on font metrics the way real text content would.
    /// </summary>
    private sealed class FixedHeightRenderer(double height) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            context.Area.Height >= height
                ? LayoutResult.Full(context.Area.WithHeight(height))
                : LayoutResult.Nothing();

        public void Draw(DrawContext context) { }
    }

    // ── (f) Counting-pass state survives a splitting element ─────────────────

    // Page 200x300, margins 10, footer FontSize 8 -> FooterHeight = 8*1.2 + 4 = 13.6,
    // full content height = 300 - 2*10 - 13.6 = 266.4.
    //
    // A FixedHeightRenderer(100) fills page 1 first, leaving 166.4pt. The list below uses an
    // explicit Leading of 90 (as the deep-list test does, to control item height directly rather
    // than depend on font metrics), so each single-line item occupies exactly 90pt:
    //   page 1 remainder (166.4pt): 1 item fits (90 <= 166.4), a 2nd does not (180 > 166.4)
    //     -> ListRenderer.Layout returns Partial after item 0.
    //   page 2 (full 266.4pt): 2 items fit (180 <= 266.4), a 3rd does not (270 > 266.4)
    //     -> Partial again, after items 1-2.
    //   page 3 (full 266.4pt): the last item (90 <= 266.4) -> Full.
    // Three pages, reached through two Partial page turns of one top-level ListRenderer — the
    // exact path that drives CountPlaceRenderer's Partial branch, not the Nothing branch (b)/(e)
    // above already cover.
    private const int SplittingListItemCount = 4;
    private const int SplittingListExpectedPages = 3;

    [Fact]
    public void CountPlaceRenderer_splittingElement_pagesTokenMatchesActualCount()
    {
        // Regresses two lines on CountPlaceRenderer's Partial branch (#460 review round 2):
        //   - pages++: without it, pass 1 never advances past page 1, so every footer resolves
        //     {pages} to 1 no matter how many pages pass 2 actually draws.
        //   - currentY = contentArea.Y: without it, pass 1's continuation pages measure from
        //     wherever the previous page's cursor stopped instead of the fresh page's top, so an
        //     element that splits again after an earlier split sees the wrong remaining height
        //     and pass 1's page count diverges from pass 2's real one.
        // Both mutations leave DocumentRenderer_deepList_withFooter_producesExpectedPageCount and
        // every other test in this file green: that test asserts pdf.Pages.Count, which pass 2
        // computes independently and correctly either way. Only the resolved {page}/{pages} text,
        // which pass 2 gets handed by pass 1, exposes either bug.
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 300), new EdgeInsets(10))
        {
            Footer = new RunningBand("{page}/{pages}", style: new TextStyle { FontSize = 8 }),
        };

        renderer.Add(new FixedHeightRenderer(100));

        var style = new TextStyle { FontSize = 20, Leading = 90 };
        var list = new ListElement(ListStyle.Unordered) { DefaultStyle = style };
        for (var i = 0; i < SplittingListItemCount; i++)
            list.Add("x");
        renderer.Add(new ListRenderer(list));

        using var ms = new MemoryStream();
        renderer.Render(ms);

        Assert.Equal(SplittingListExpectedPages, pdf.Pages.Count);

        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        for (var page = 1; page <= SplittingListExpectedPages; page++)
        {
            Assert.Equal(1, PdfTestUtil.CountOccurrences(
                decompressed, $"({page}/{SplittingListExpectedPages}) Tj"));
        }
    }

    // ── (g) A header-only document still runs the counting pass ──────────────

    [Fact]
    public void PlaceRenderer_headerOnlyDocument_resolvesPagesToken()
    {
        // Regresses RunLayout's two-pass gate (#460 review round 2): no other test in this file
        // sets Header, so narrowing "Header is not null || Footer is not null" to just
        // "Footer is not null" still passes all of them — the footer tests are unaffected by that
        // narrowing, and the band-less ones already skip CountPages(). A header-only document would then skip
        // CountPages() entirely, and DrawRunningBands would resolve {pages} against the
        // totalPages == 0 default instead of the real count.
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10))
        {
            Header = new RunningBand("{page}/{pages}", style: new TextStyle { FontSize = 8 }),
        };
        renderer.Add(new FixedHeightRenderer(50));

        using var ms = new MemoryStream();
        renderer.Render(ms);

        Assert.Single(pdf.Pages);

        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        Assert.Equal(1, PdfTestUtil.CountOccurrences(decompressed, "(1/1) Tj"));
    }

    // ── (h) The Partial branch draws the split, not the original renderer ────

    [Fact]
    public void PlaceRenderer_partialResult_drawsSplitRendererNotTheOriginal()
    {
        // Pins DrawRenderer(result.SplitRenderer!) against a DrawRenderer(renderer) mutation
        // (review round 4, #459/#460). Paragraph, List and Table each set their own
        // _endLine/_endItem/_occupied to exactly what their split copy carries, so for those the
        // mutant draws indistinguishable output and no fixture built from them can catch it.
        // HeadingRenderer is the exception, and not a benign one: its split copy never receives
        // _occupied, so the two differ and the shipped branch is the wrong one — a heading that
        // splits registers its outline destination at the page top (#464). This probe draws
        // different marker text from each half, so it pins the contract rather than leaning on
        // which renderers happen to diverge.
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10));
        renderer.Add(new SplitVsOriginalRenderer());

        using var ms = new MemoryStream();
        renderer.Render(ms);

        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        Assert.Equal(1, PdfTestUtil.CountOccurrences(decompressed, "(SPLIT) Tj"));
        Assert.Equal(0, PdfTestUtil.CountOccurrences(decompressed, "(ORIGINAL) Tj"));
    }

    /// <summary>
    /// Draws fixed marker text and reports Full immediately, so it terminates whatever chain
    /// it is placed in without splitting further.
    /// </summary>
    private sealed class MarkerRenderer(string marker) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context)
        {
            var font = context.GetFont(Standard14.Helvetica);
            context.Canvas.BeginText();
            context.Canvas.SetFont(font, 12);
            context.Canvas.ShowTextAligned(marker, 10, 10);
            context.Canvas.EndText();
        }
    }

    /// <summary>
    /// Reports Partial once, with a split half and an overflow that each draw distinguishable
    /// marker text, and draws its own "ORIGINAL" marker if <see cref="Draw"/> is ever called on
    /// it directly rather than on <see cref="LayoutResult.SplitRenderer"/> — which a correct
    /// PlaceRenderer never does (#459/#460 review round 4).
    /// </summary>
    private sealed class SplitVsOriginalRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Partial(context.Area.WithHeight(0), new MarkerRenderer("SPLIT"), new MarkerRenderer("OVERFLOW"));

        public void Draw(DrawContext context)
        {
            var font = context.GetFont(Standard14.Helvetica);
            context.Canvas.BeginText();
            context.Canvas.SetFont(font, 12);
            context.Canvas.ShowTextAligned("ORIGINAL", 10, 10);
            context.Canvas.EndText();
        }
    }
}
