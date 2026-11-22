// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.ContinuationSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The mechanism that keeps the continuation limit's verdict for a renderer whose new layout
/// differs from 2.3.3's (#686), tested with a scripted renderer at a limit of 300.
/// </summary>
public sealed class ContinuationLimitTests
{
    public enum Variant
    {
        Plain,
        Lead,
        EmptyBox,
        RetrySite,
        KeepProbe,
        Footer,
        KeepWithNext,
        Mirror,
        AddedTwice,
    }

    // Continuations the loop counts for a Nothing before the scripted renderer's own pieces.
    private static int Extra(Variant v) => v is Variant.EmptyBox or Variant.RetrySite ? 1 : 0;

    private static (Setup Setup, IRenderer[] Elements) Build(Variant v, Plan plan, Log log)
    {
        var s = new Scripted(plan, log);
        var setup = new Setup();
        switch (v)
        {
            case Variant.Lead:
                return (setup, [new Block(5), s]);
            case Variant.EmptyBox:
                return (setup, [new Block(ContentHeight(setup)), s]);
            case Variant.RetrySite:
            case Variant.KeepProbe:
                return (setup, [new Block(5), s]);
            case Variant.Footer:
                return (setup with { Footer = true }, [s]);
            case Variant.KeepWithNext:
                return (setup, [new Block(5, keepWithNext: true), s]);
            case Variant.Mirror:
                return (setup with { Mirror = true, Footer = true }, [new Block(5), s]);
            case Variant.AddedTwice:
                return (setup, [s, s]);
            default:
                return (setup, [s]);
        }
    }

    private static Plan PlanFor(Variant v, int newContinuations, int twinContinuations, long bound = 1000) => new()
    {
        NewPages = newContinuations - Extra(v),
        TwinPages = twinContinuations - Extra(v),
        Bound = bound,
        Site = v switch
        {
            Variant.RetrySite => Site.Retry,
            Variant.KeepProbe => Site.KeepProbe,
            _ => Site.Loop,
        },
        KeepTogether = v == Variant.KeepProbe,
    };

    private static Outcome Place(Variant v, Plan plan)
    {
        var log = new Log();
        var (setup, elements) = Build(v, plan, log);
        return Run(setup, log, elements);
    }

    public static IEnumerable<TheoryDataRow<Variant, int, int>> DecisionRows()
    {
        foreach (var v in Enum.GetValues<Variant>())
        {
            // new / twin continuations: both save; the new layout reaches the limit; the twin
            // reaches it; both do; and the same four at the edge, where 300 saves and 301 does not.
            foreach (var (n, t) in new[] { (100, 100), (301, 100), (100, 301), (301, 301), (300, 300), (301, 300), (300, 301), (400, 200) })
                yield return new(v, n, t);
        }
    }

    [Theory]
    [MemberData(nameof(DecisionRows))]
    public void Decision_picksTheLayoutThatKeepsTheVerdict(Variant v, int newC, int twinC)
    {
        var outcome = Place(v, PlanFor(v, newC, twinC));
        var placements = v == Variant.AddedTwice ? 2 : 1;
        var twinRefused = twinC > Limit;
        var newRefused = newC > Limit;

        Assert.Equal(0, outcome.Faults);
        if (twinRefused)
        {
            // The twin reaches the limit, so the real placement uses it and throws what 2.3.3 threw.
            Assert.True(IsLimitRefusal(outcome.Error), outcome.Error?.ToString());
            Assert.Equal(1, outcome.Probes);
            Assert.DoesNotContain("new", outcome.Log.Draws);
            return;
        }

        Assert.Null(outcome.Error);
        Assert.Equal(2 * placements, outcome.Probes);
        if (newRefused)
        {
            // Saved on 2.3.3: the twin is placed.
            Assert.DoesNotContain("new", outcome.Log.Draws);
            Assert.Equal(PagesFor(v, twinC), outcome.Pages);
        }
        else
        {
            Assert.Equal("new", outcome.Log.Draws[^1]);
            Assert.Equal(PagesFor(v, newC), outcome.Pages);
            // Every call of the document's own, in every pass and simulation, was addressed.
            Assert.DoesNotContain(outcome.Log.RealCalls, c => c.Mode == "Twin");
        }
    }

    private static int PagesFor(Variant v, int continuations) =>
        v == Variant.AddedTwice ? 2 * continuations + 1 : continuations + 1;

    [Theory]
    [InlineData(Variant.Plain)]
    [InlineData(Variant.RetrySite)]
    [InlineData(Variant.Lead)]
    public void Decision_boundWithinWhatIsLeft_runsNoProbe(Variant v)
    {
        // What is left of the limit where the decision starts is 300, or 299 after a Nothing.
        var left = Limit - Extra(v);
        var within = Place(v, PlanFor(v, 200, 200, bound: left));
        Assert.Null(within.Error);
        Assert.Equal(0, within.Probes);

        var over = Place(v, PlanFor(v, 200, 200, bound: left + 1));
        Assert.Null(over.Error);
        Assert.Equal(2, over.Probes);
    }

    [Fact]
    public void Decision_newLayoutAtTheLimitWithBoundWithinIt_isUsedWithoutProbe()
    {
        // The bound is what the document trusts: a bound within the limit means no probe, and the
        // new layout is placed.
        var outcome = Place(Variant.Plain, PlanFor(Variant.Plain, 250, 250, bound: 250));
        Assert.Equal(0, outcome.Probes);
        Assert.Equal("new", outcome.Log.Draws[^1]);
    }

    [Fact]
    public void Decision_twinIsProbedFirst_andStopsTheDecisionWhenItReachesTheLimit()
    {
        var outcome = Place(Variant.Plain, PlanFor(Variant.Plain, 301, 301));
        Assert.Equal(1, outcome.Probes);
        Assert.All(outcome.Log.Calls.Where(c => c.Probing).Take(3), c => Assert.Equal("twin", c.Flavor));
    }

    [Theory]
    [InlineData(Variant.Footer)]
    [InlineData(Variant.KeepWithNext)]
    [InlineData(Variant.Mirror)]
    public void Memo_isSharedByBothPassesAndByKeepSimulations(Variant v)
    {
        var outcome = Place(v, PlanFor(v, 301, 100));
        Assert.Null(outcome.Error);
        Assert.True(outcome.Renderer.MeasureRuns >= 1 || v == Variant.KeepWithNext);
        // Two probes in all, not two for each pass or simulation that starts the element.
        Assert.Equal(2, outcome.Probes);
        Assert.DoesNotContain("new", outcome.Log.Draws);
    }

    [Fact]
    public void Memo_isKeyedByStart_soTheSameElementAddedTwiceDecidesTwice()
    {
        var outcome = Place(Variant.AddedTwice, PlanFor(Variant.AddedTwice, 100, 100));
        Assert.Equal(4, outcome.Probes);
    }

    [Fact]
    public void Memo_isKeyedByTheCountAsWellAsTheStart()
    {
        // The same element starts twice at a page top: once after a Nothing, with one continuation
        // counted, and once with none. What is left of the limit differs, so the decision does too.
        var plan = PlanFor(Variant.RetrySite, 100, 100);
        var log = new Log();
        var scripted = new Scripted(plan, log);
        var outcome = Run(new Setup(), log, new Block(5), scripted, new PageBreakRenderer(new PageBreak()), scripted);

        Assert.Null(outcome.Error);
        Assert.Equal(4, outcome.Probes);
    }

    [Fact]
    public void Probe_restoresTheCursorAndPageParity()
    {
        // The probes together take an odd number of pages (100 and 301). Left behind in the page
        // index, that flips the side of every later page under mirrored margins.
        var outcome = Place(Variant.Mirror, PlanFor(Variant.Mirror, 301, 100));
        Assert.Null(outcome.Error);
        var real = outcome.Log.RealCalls.ToList();
        Assert.Equal(2, outcome.Probes);
        Assert.Equal(real[0].X, real[1].X);

        // Page 1 is the first page top the document reaches, and a verso page's content starts at
        // the outside margin; from there each page alternates between the two sides.
        var tops = real.Where(c => c.AtPageTop).Select(c => c.X).Take(6).ToList();
        Assert.NotEqual(real[0].X, tops[0]);
        for (var i = 1; i < tops.Count; i++)
            Assert.NotEqual(tops[i - 1], tops[i]);
    }

    [Fact]
    public void Probe_leavesTheDocumentAsIfItHadNotRun()
    {
        // The same document whose twin probe throws something else, caught and left for the
        // placement, saves the bytes of the one that never threw.
        var clean = Place(Variant.Lead, PlanFor(Variant.Lead, 50, 50));
        var plan = PlanFor(Variant.Lead, 50, 50) with { FailTwin = Failure.Exception };
        var faulted = Place(Variant.Lead, plan);

        Assert.Null(faulted.Error);
        Assert.Equal(1, faulted.Faults);
        Assert.Equal(clean.Bytes, faulted.Bytes);
    }

    [Fact]
    public void Probe_exceptionOtherThanTheLimit_isLeftForTheRealPlacement()
    {
        var plan = PlanFor(Variant.Lead, 50, 50) with { FailNew = Failure.Exception };
        var outcome = Place(Variant.Lead, plan);

        Assert.Equal(1, outcome.Faults);
        Assert.IsType<InvalidOperationException>(outcome.Error);
        Assert.Equal("scripted failure", outcome.Error!.Message);
    }

    [Fact]
    public void Decision_twinChosenAtTheKeepTogetherProbe_redoesTheIterationWithTheTwin()
    {
        var outcome = Place(Variant.KeepProbe, PlanFor(Variant.KeepProbe, 301, 100));
        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.Probes);

        // The first call, mid-page, was the new layout's and matched the twin's. The iteration is
        // made again from its start with the twin, which is a call by the twin at that same start.
        var first = outcome.Log.RealCalls.First();
        Assert.False(first.AtPageTop);
        Assert.Contains(outcome.Log.RealCalls, c => c.Flavor == "twin" && !c.AtPageTop && c.Y == first.Y);
    }

    [Fact]
    public void Probe_outOfMemory_isNotCaught()
    {
        var plan = PlanFor(Variant.Lead, 50, 50) with { FailNew = Failure.OutOfMemory, FailOnlyInProbe = true };
        Assert.Throws<OutOfMemoryException>(() => Place(Variant.Lead, plan));
    }

    [Fact]
    public void Probe_cancellation_isNotCaught()
    {
        var plan = PlanFor(Variant.Lead, 50, 50) with { FailNew = Failure.Cancelled, FailOnlyInProbe = true };
        Assert.Throws<OperationCanceledException>(() => Place(Variant.Lead, plan));
    }

    [Fact]
    public void Decision_firstPieceWithoutAReport_fixesTheTwin()
    {
        // The renderer first has something to report on its second piece. The first piece was
        // accepted with no report, so it equals the twin's and the rest is the twin's: nothing is
        // decided later, and the late report is not read.
        var plan = new Plan { NewPages = 10, TwinPages = 20, Site = Site.Late };
        var log = new Log();
        var outcome = Run(new Setup(), log, new Scripted(plan, log));

        Assert.Null(outcome.Error);
        Assert.Equal(0, outcome.Probes);
        Assert.Equal(21, outcome.Pages);
        Assert.DoesNotContain("new", log.Draws);
    }

    [Fact]
    public void Contract_aRendererThatNeverReports_getsTheTwinsBytes()
    {
        var twinOnly = new Plan { NewPages = 10, TwinPages = 20, Reports = false };
        var viaTwin = new Plan { NewPages = 99, TwinPages = 20, Reports = false };
        var a = Place(Variant.Lead, twinOnly);
        var b = Place(Variant.Lead, viaTwin);
        Assert.Equal(a.Bytes, b.Bytes);
        Assert.Equal(0, a.Probes);
    }

    [Fact]
    public void Decision_reportingFromTheTwinChosenRestart_equalsThePlacementThatNeverReported()
    {
        // Chosen twin after a report equals the bytes of the same document that never reported.
        foreach (var v in new[] { Variant.Plain, Variant.Lead, Variant.EmptyBox, Variant.RetrySite, Variant.KeepProbe })
        {
            var reporting = Place(v, PlanFor(v, 301, 120));
            var silent = Place(v, PlanFor(v, 301, 120) with { Reports = false });
            Assert.Null(reporting.Error);
            Assert.Equal(silent.Bytes, reporting.Bytes);
        }
    }

    public static IEnumerable<TheoryDataRow<int, int, int>> WrapperRows()
    {
        foreach (var kind in Enum.GetValues<WrapKind>())
            foreach (var (n, t) in new[] { (120, 120), (50, 301), (400, 200), (301, 300), (400, 300), (100, 400) })
                yield return new((int)kind, n, t);
    }

    [Theory]
    [MemberData(nameof(WrapperRows))]
    public void Wrapper_alwaysGetsTheTwin(int wrapKind, int newC, int twinC)
    {
        var kind = (WrapKind)wrapKind;
        var plan = new Plan { NewPages = newC, TwinPages = twinC };
        var log = new Log();
        var outcome = Run(new Setup(), log, new Wrapper(new Scripted(plan, log), kind));

        var oracleLog = new Log();
        var oracle = Run(new Setup(), oracleLog, new Wrapper(new Scripted(plan with { Reports = false }, oracleLog), kind));

        Assert.Equal(0, outcome.Probes);
        Assert.DoesNotContain(log.Calls, c => c.Flavor == "new");
        Assert.DoesNotContain("new", log.Draws);
        Assert.Equal(oracle.Error?.GetType(), outcome.Error?.GetType());
        Assert.Equal(oracle.Error?.Message, outcome.Error?.Message);
        Assert.Equal(oracle.Bytes, outcome.Bytes);
        Assert.Equal(twinC > Limit, IsLimitRefusal(outcome.Error));
    }

    [Theory]
    [InlineData(140, 400)]
    [InlineData(100, 160)]
    public void Wrapper_containerPlacingTwoElements_countsAsTheTwinDoes(int twinEach, int newEach)
    {
        var plan = new Plan { NewPages = newEach, TwinPages = twinEach };
        var log = new Log();
        var parts = new IRenderer[] { new Scripted(plan, log), new Scripted(plan, log) };
        var outcome = Run(new Setup(), log, new Sequence(parts));

        var total = (2 * twinEach) + 1;
        Assert.Equal(0, outcome.Probes);
        Assert.DoesNotContain("new", log.Draws);
        if (total > Limit)
            Assert.True(IsLimitRefusal(outcome.Error), outcome.Error?.ToString());
        else
            Assert.Null(outcome.Error);
    }

    [Fact]
    public void Context_builtByTheDocument_isAddressed_andOthersAreNot()
    {
        var area = new LayoutBox(0, 0, 10, 10);
        var made = new LayoutContext(area);
        Assert.Null(made.Target);
        Assert.Null(made.Session);
        Assert.Equal(ContinuationMode.Undecided, made.Mode);

        var target = new Block(1);
        var other = new Block(1);
        var addressed = new LayoutContext(area) { Target = target, Mode = ContinuationMode.New, Session = new ContinuationSession() };
        var narrowed = addressed.WithArea(area);
        Assert.Null(narrowed.Target);
        Assert.Null(narrowed.Session);
        Assert.Equal(ContinuationMode.Undecided, narrowed.Mode);

        Assert.Equal(ContinuationMode.New, addressed.ModeFor(target));
        Assert.Equal(ContinuationMode.Twin, addressed.ModeFor(other));
        Assert.Equal(ContinuationMode.Twin, made.ModeFor(target));
        Assert.Equal(ContinuationMode.Twin, narrowed.ModeFor(target));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Address_givesTheChildTheParentsMode(int modeValue)
    {
        var mode = (ContinuationMode)modeValue;
        var child = new Block(1);
        var seen = new List<ContinuationMode>();
        var parent = new Parent(child, seen);
        var context = new LayoutContext(new LayoutBox(0, 0, 10, 10))
        {
            Target = parent,
            Mode = mode,
            Session = new ContinuationSession(mode),
        };

        parent.Layout(context);
        Assert.Equal([mode], seen);

        // A parent that a caller's renderer lays out is not addressed, so its child is the twin's.
        seen.Clear();
        parent.Layout(context.WithArea(context.Area));
        Assert.Equal([ContinuationMode.Twin], seen);
    }

    [Fact]
    public void Report_isDroppedUnlessTheCalledInstanceMakesItWhileUndecided()
    {
        var log = new Log();
        var a = new Scripted(new Plan(), log);
        var b = new Scripted(new Plan(), log);

        var session = new ContinuationSession { Called = a };
        session.Report(b, 5);
        Assert.False(session.TakeReport());

        session.Report(a, 5);
        session.Report(a, 9);
        session.Report(a, 7);
        Assert.Equal(9, session.Bound);
        Assert.True(session.TakeReport());
        Assert.False(session.TakeReport());

        session.Mode = ContinuationMode.New;
        session.Report(a, 5);
        Assert.False(session.TakeReport());

        var probing = new ContinuationSession(ContinuationMode.Undecided, probing: true) { Called = a };
        probing.Report(a, 5);
        Assert.False(probing.TakeReport());
    }

    [Fact]
    public void Limit_defaultsToTheConstant_andReadsByTheThrow()
    {
        var renderer = new DocumentRenderer(new VellumPdf.Document.PdfDocument());
        Assert.Equal(50_000, renderer.ContinuationLimit);

        // At a limit of 300 a renderer that never reports reaches it as it always did.
        var log = new Log();
        var outcome = Run(new Setup(), log, new Scripted(new Plan { NewPages = 350, TwinPages = 350, Reports = false }, log));
        Assert.True(IsLimitRefusal(outcome.Error));
        Assert.Equal(0, outcome.Probes);
    }
}
