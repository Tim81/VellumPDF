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
    public void Memo_isKeyedByTheElement_soTwoElementsAtTheSameStartDecideApart()
    {
        // Both start at the top of a page with nothing counted. The first would reach the limit with
        // its new layout and the second would not; the verdict of one is not the other's.
        var logA = new Log();
        var logB = new Log();
        var a = new Scripted(new Plan { NewPages = 301, TwinPages = 100 }, logA);
        var b = new Scripted(new Plan { NewPages = 100, TwinPages = 100 }, logB);
        var outcome = Run(new Setup(), new Log(), a, new PageBreakRenderer(new PageBreak()), b);

        Assert.Null(outcome.Error);
        Assert.Equal(4, outcome.Probes);
        Assert.DoesNotContain("new", logA.Draws);
        Assert.Equal("new", logB.Draws[^1]);
        Assert.Empty(logA.BoundViolations);
        Assert.Empty(logB.BoundViolations);
    }

    [Fact]
    public void Memo_doesNotCarryAVerdictAcrossMeasuringRunsThatResolveToADifferentLayout()
    {
        // Resolving changes the layout of the element and not where it starts, which is what a
        // verdict is kept under. Run one lays out 150 continuations in the new layout and run two
        // 301. The twin takes 100 in both and is what 2.3.3 saved.
        var log = new Log();
        var plans = new[]
        {
            new Plan { NewPages = 150, TwinPages = 100 },
            new Plan { NewPages = 301, TwinPages = 100 },
        };
        var outcome = Run(new Setup(), log, new DependentScripted(plans, log));

        Assert.Null(outcome.Error);
        Assert.Equal(3, outcome.Renderer.MeasureRuns);
        Assert.DoesNotContain("new", log.Draws);
        Assert.Equal(101, outcome.Pages);
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

    [Theory]
    [InlineData(Variant.Plain)]
    [InlineData(Variant.Lead)]
    [InlineData(Variant.RetrySite)]
    public void Probe_newLayoutThrowing_whereTheTwinSaves_choosesTheTwin(Variant v)
    {
        // The twin is 2.3.3's own placement, so it saves what 2.3.3 saved. The new layout is not
        // placed, and the exception it would have thrown is never raised.
        var plan = PlanFor(v, 50, 50) with { FailNew = Failure.Exception };
        var outcome = Place(v, plan);
        var silent = Place(v, PlanFor(v, 50, 50) with { Reports = false });

        Assert.Null(outcome.Error);
        Assert.Equal(1, outcome.Faults);
        Assert.DoesNotContain("new", outcome.Log.Draws);
        Assert.Equal(silent.Bytes, outcome.Bytes);
    }

    [Fact]
    public void Probe_bothLayoutsThrowing_keepTheTwinsRefusal()
    {
        var plan = PlanFor(Variant.Lead, 50, 50) with { FailNew = Failure.Exception, FailTwin = Failure.Exception };
        var outcome = Place(Variant.Lead, plan);

        Assert.Equal(2, outcome.Faults);
        Assert.IsType<InvalidOperationException>(outcome.Error);
        Assert.Equal("scripted failure in the twin", outcome.Error!.Message);
    }

    [Fact]
    public void Probe_twinThrowing_whereTheNewLayoutSaves_choosesTheNewLayout()
    {
        // A document 2.3.3 refused saves with the new layout: the verdict is about the limit, and
        // a fix that lets a refused document save is the fix's own.
        var plan = PlanFor(Variant.Lead, 50, 50) with { FailTwin = Failure.Exception };
        var outcome = Place(Variant.Lead, plan);

        Assert.Null(outcome.Error);
        Assert.Equal(1, outcome.Faults);
        Assert.Equal("new", outcome.Log.Draws[^1]);
    }

    [Theory]
    [InlineData(Variant.Plain)]
    [InlineData(Variant.Lead)]
    [InlineData(Variant.RetrySite)]
    public void Decision_newLayoutThatRefusesAPageTop_whereTheTwinSaves_choosesTheTwin(Variant v)
    {
        // Under the retry site the new layout's own retry answers Nothing, which the loop would
        // throw as too tall; the twin's retry fits, and the document saves as 2.3.3 saved.
        var plan = PlanFor(v, 100, 100) with { NewRefusesAtTop = true };
        var outcome = Place(v, plan);

        Assert.Null(outcome.Error);
        Assert.DoesNotContain("new", outcome.Log.Draws);
        Assert.Equal(PagesFor(v, 100), outcome.Pages);
    }

    [Theory]
    [InlineData(24_076, true)]
    [InlineData(24_075, true)]
    [InlineData(24_076, false)]
    [InlineData(24_075, false)]
    public void Decision_probesStartWhereThePlacementStarts_notAtThePageTop(double units, bool theNewLayoutIsTheLongOne)
    {
        // 75 points are left on the page after the lead block and 80 on a fresh one. A content of
        // 24,076 points takes 301 continuations from the first and 300 from the second, so a
        // probe that began at the top of the page would miss the limit the placement reaches.
        var plan = new Plan
        {
            NewUnits = theNewLayoutIsTheLongOne ? units : 1000,
            TwinUnits = theNewLayoutIsTheLongOne ? 1000 : units,
        };
        var outcome = Place(Variant.Lead, plan);
        var reaches = units > 24_075;

        if (reaches && !theNewLayoutIsTheLongOne)
        {
            Assert.True(IsLimitRefusal(outcome.Error), outcome.Error?.ToString());
            Assert.Equal(1, outcome.Probes);
            return;
        }

        Assert.Null(outcome.Error);
        Assert.Equal(2, outcome.Probes);
        if (reaches)
            Assert.DoesNotContain("new", outcome.Log.Draws);
        else
            Assert.Equal("new", outcome.Log.Draws[^1]);
    }

    [Fact]
    public void Decision_atTheRetrySite_redoesTheRetryWithTheTwin()
    {
        // After a Nothing the retry is the call that reported. Chosen, the twin makes the retry
        // itself, then the loop call that the document goes on with.
        var outcome = Place(Variant.RetrySite, PlanFor(Variant.RetrySite, 301, 100));
        Assert.Null(outcome.Error);

        var calls = outcome.Log.RealCalls.ToList();
        var reporting = calls.FindIndex(c => c.Flavor == "new" && c.AtPageTop);
        Assert.True(reporting >= 0);
        Assert.Equal("twin", calls[reporting + 1].Flavor);
        Assert.True(calls[reporting + 1].AtPageTop);
        Assert.Equal(calls[reporting].Height, calls[reporting + 1].Height);

        // The twin's 99 continuations are 99 Partial calls and one Full, after the retry.
        Assert.Equal(1 + 99 + 1, calls.Count(c => c.Flavor == "twin"));
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
        Assert.Null(made.Session);

        var target = new Block(1);
        var other = new Block(1);
        var addressed = new LayoutContext(area) { Session = new ContinuationSession(ContinuationMode.New) { Called = target } };
        var narrowed = addressed.WithArea(area);
        Assert.Null(narrowed.Session);

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
        var seen = new List<LayoutContext>();
        var parent = new Parent(child, seen);
        var context = new LayoutContext(new LayoutBox(0, 0, 10, 10))
        {
            Session = new ContinuationSession(mode) { Called = parent },
        };

        parent.Layout(context);
        Assert.Equal(mode, seen[0].ModeFor(child));
        Assert.Equal(ContinuationMode.Twin, seen[0].ModeFor(parent));

        // A parent that a caller's renderer lays out is not addressed, so its child is the twin's.
        seen.Clear();
        parent.Layout(context.WithArea(context.Area));
        Assert.Equal(ContinuationMode.Twin, seen[0].ModeFor(child));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Address_carriesWhatTheParentNamesAndWhatTheDocumentSet(bool addressedParent)
    {
        var area = new LayoutBox(1, 2, 30, 40);
        var childArea = new LayoutBox(5, 6, 7, 8);
        var fresh = new LayoutBox(9, 10, 11, 12);
        var parent = new Block(1);
        var child = new Block(1);
        var context = new LayoutContext(area, 3)
        {
            FreshPageArea = fresh,
            AtPageTop = true,
            HonourLineCounts = true,
            Session = addressedParent ? new ContinuationSession(ContinuationMode.New) { Called = parent } : null,
        };

        // The page top and the line counts are about the parent's area, not the child's, so they
        // are carried only when the parent says the child's area has them, as WithArea would.
        var plain = context.Address(parent, child, childArea);
        Assert.Equal(childArea, plain.Area);
        Assert.Equal(3, plain.ContentTop);
        Assert.Equal(fresh, plain.FreshPageArea);
        Assert.False(plain.AtPageTop);
        Assert.False(plain.HonourLineCounts);

        var named = context.Address(parent, child, childArea, atTop: true, lineCounts: true);
        Assert.True(named.AtPageTop);
        Assert.True(named.HonourLineCounts);
        Assert.Equal(fresh, named.FreshPageArea);

        var onlyTop = context.Address(parent, child, childArea, atTop: true);
        Assert.True(onlyTop.AtPageTop);
        Assert.False(onlyTop.HonourLineCounts);

        var onlyCounts = context.Address(parent, child, childArea, lineCounts: true);
        Assert.False(onlyCounts.AtPageTop);
        Assert.True(onlyCounts.HonourLineCounts);

        Assert.Equal(addressedParent ? ContinuationMode.New : ContinuationMode.Twin, plain.ModeFor(child));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Addressed_givesADrawTimeChildTheModeItsParentWasLaidOutIn(int modeValue)
    {
        // A parent's Draw lays its child out again with no context of the document's, so it keeps
        // the mode its own layout had and names it here.
        var mode = (ContinuationMode)modeValue;
        var area = new LayoutBox(1, 2, 3, 4);
        var child = new Block(1);
        var context = LayoutContext.Addressed(area, child, mode);

        Assert.Equal(area, context.Area);
        Assert.Equal(mode, context.ModeFor(child));
        Assert.Equal(ContinuationMode.Twin, context.ModeFor(new Block(1)));
        Assert.Equal(ContinuationMode.Twin, context.WithArea(area).ModeFor(child));
    }

    [Fact]
    public void IsPlacedAsTwin_isTrueOnlyForTheDocumentsOwnCallOutsideAProbe()
    {
        var target = new Block(1);
        var area = new LayoutBox(0, 0, 10, 10);
        var placed = new LayoutContext(area) { Session = new ContinuationSession(ContinuationMode.Twin) { Called = target } };
        var probe = new LayoutContext(area) { Session = new ContinuationSession(ContinuationMode.Twin, probing: true) { Called = target } };
        var decidedNew = new LayoutContext(area) { Session = new ContinuationSession(ContinuationMode.New) { Called = target } };

        Assert.True(placed.IsPlacedAsTwin(target));
        Assert.False(placed.IsPlacedAsTwin(new Block(1)));
        Assert.False(placed.WithArea(area).IsPlacedAsTwin(target));
        Assert.False(new LayoutContext(area).IsPlacedAsTwin(target));
        Assert.False(probe.IsPlacedAsTwin(target));
        Assert.False(decidedNew.IsPlacedAsTwin(target));
    }

    [Fact]
    public void IsPlacedAsTwin_tellsATwinTheDocumentChose_andNotOneAProbeOrAWrapperCalled()
    {
        var log = new Log();
        var outcome = Run(new Setup(), log, new Scripted(new Plan { NewPages = 301, TwinPages = 100 }, log));
        Assert.Null(outcome.Error);

        var twinCalls = log.Calls.Where(c => c.Flavor == "twin").ToList();
        Assert.All(twinCalls.Where(c => !c.Probing), c => Assert.True(c.PlacedAsTwin));
        Assert.All(log.Calls.Where(c => c.Probing), c => Assert.False(c.PlacedAsTwin));
        Assert.Contains(twinCalls, c => !c.Probing);

        var wrappedLog = new Log();
        var wrapped = Run(new Setup(), wrappedLog, new Wrapper(new Scripted(new Plan { NewPages = 50, TwinPages = 50 }, wrappedLog), WrapKind.PassThrough));
        Assert.Null(wrapped.Error);
        Assert.All(wrappedLog.Calls, c => Assert.False(c.PlacedAsTwin));
    }

    [Fact]
    public void Parent_thatReportsForItself_andAddressesItsChild_decidesLikeAnyRenderer()
    {
        // A reporting child under a reporting parent reports into a context that is not the
        // document's call, which is dropped; the parent's own report is what decides.
        foreach (var (newC, twinC) in new[] { (301, 100), (100, 100), (301, 301) })
        {
            var log = new Log();
            var child = new Scripted(new Plan { NewPages = newC, TwinPages = twinC }, log);
            var outcome = Run(new Setup(), log, new ReportingParent(child, 1000));

            Assert.Equal(0, outcome.Renderer.UnreportedChildReports);
            if (twinC > Limit)
            {
                Assert.True(IsLimitRefusal(outcome.Error), outcome.Error?.ToString());
                continue;
            }

            Assert.Null(outcome.Error);
            Assert.Equal(2, outcome.Probes);
            if (newC > Limit)
                Assert.DoesNotContain("new", log.Draws);
            else
                Assert.Equal("new", log.Draws[^1]);
            Assert.Equal((newC > Limit ? twinC : newC) + 1, outcome.Pages);
        }
    }

    [Fact]
    public void Parent_thatReportsForItself_isTheTwinUnderACallersWrapper()
    {
        var log = new Log();
        var child = new Scripted(new Plan { NewPages = 301, TwinPages = 100 }, log);
        var outcome = Run(new Setup(), log, new Wrapper(new ReportingParent(child, 1000), WrapKind.PassThrough));

        Assert.Null(outcome.Error);
        Assert.Equal(0, outcome.Probes);
        Assert.DoesNotContain("new", log.Draws);
    }

    [Fact]
    public void Parent_thatAddressesAReportingChildAndDoesNotReport_isCounted()
    {
        // The child lays out the new way while the parent has not reported, its report is dropped,
        // and the first piece fixes the twin for the rest: one new piece and a hundred twin ones.
        // The count is how a test of a built-in parent finds it; a Debug build fails instead.
        var log = new Log();
        var child = new Scripted(new Plan { NewPages = 301, TwinPages = 100 }, log);
        var seen = new List<LayoutContext>();
        var outcome = Run(new Setup { AllowUnreportedChild = true }, log, new Parent(child, seen));

        Assert.Equal(1, outcome.Renderer.UnreportedChildReports);
        Assert.Null(outcome.Error);
        Assert.Equal(0, outcome.Probes);
        Assert.Equal("new", log.Draws[0]);
        Assert.Equal(100, log.Draws.Count(d => d == "twin"));
    }

    [Fact]
    public void Scripted_aCallThatDoesNotReport_returnsTheTwinsResult()
    {
        // The late site reports from the second piece on, so the first call does not report and must
        // lay out as the twin does: the twin has five continuations and the new layout none.
        var log = new Log();
        var plan = new Plan { NewPages = 0, TwinPages = 5, Site = Site.Late };
        var scripted = new Scripted(plan, log);
        var session = new ContinuationSession { Called = scripted };
        var first = scripted.Layout(new LayoutContext(new LayoutBox(0, 0, 100, 80)) { Session = session });

        Assert.False(session.TakeReport());
        Assert.Equal(LayoutResult.Outcome.Partial, first.Status);

        var reporting = new Scripted(plan with { Site = Site.Loop }, log);
        var reportingSession = new ContinuationSession { Called = reporting };
        var second = reporting.Layout(new LayoutContext(new LayoutBox(0, 0, 100, 80)) { Session = reportingSession });
        Assert.True(reportingSession.TakeReport());
        Assert.Equal(LayoutResult.Outcome.Full, second.Status);
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
        Assert.Equal(0, probing.Strays);
    }

    [Fact]
    public void Report_fromAnInstanceTheDocumentDidNotCall_countsAsAStray()
    {
        var log = new Log();
        var called = new Scripted(new Plan(), log);
        var child = new Scripted(new Plan(), log);

        var root = new ContinuationSession { Called = called };
        Assert.False(root.Pending);
        root.Report(child, 5);
        Assert.False(root.Reported);
        Assert.Equal(1, root.Strays);
        Assert.True(root.Pending);

        // A view for a child counts on the document's session, and one made where there is none
        // has nothing to count on.
        root.Reset();
        Assert.False(root.Pending);
        Assert.Equal(0, root.Strays);
        var view = ContinuationSession.ViewFor(child, ContinuationMode.Undecided, root);
        view.Report(child, 5);
        Assert.Equal(1, root.Strays);
        Assert.False(root.Reported);
        ContinuationSession.ViewFor(child, ContinuationMode.Undecided, null).Report(child, 5);
        ContinuationSession.ViewFor(child, ContinuationMode.New, root).Report(child, 5);
        Assert.Equal(1, root.Strays);

        // A view of a view reaches the same document session.
        ContinuationSession.ViewFor(child, ContinuationMode.Undecided, view).Report(child, 5);
        Assert.Equal(2, root.Strays);
    }

    [Fact]
    public void Reset_forgetsThePlacementBeforeIt()
    {
        var log = new Log();
        var a = new Scripted(new Plan(), log);
        var session = new ContinuationSession { Called = a };
        session.Report(a, 5);
        session.Mode = ContinuationMode.Twin;
        session.Reset();

        Assert.Equal(ContinuationMode.Undecided, session.Mode);
        Assert.False(session.Pending);
        Assert.False(session.TakeReport());
        session.Report(a, 3);
        Assert.Equal(3, session.Bound);
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
