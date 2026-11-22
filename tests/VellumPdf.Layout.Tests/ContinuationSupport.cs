// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// An internal renderer whose continuation count depends on the layout it is in (#686), a log of
/// every call it receives, and the harness that places it. It stands in for a built-in renderer
/// whose new layout differs from 2.3.3's, so the decision between the two is tested without one.
/// </summary>
internal static class ContinuationSupport
{
    /// <summary>The continuation limit every test document uses, instead of 50,000.</summary>
    internal const int Limit = 300;

    /// <summary>The height a scripted piece needs to be placed at all.</summary>
    internal const double MinHeight = 10;

    internal enum Site
    {
        /// <summary>Reports at the first call that has room.</summary>
        Loop,

        /// <summary>Answers Nothing mid-page without reporting, and reports at the retry.</summary>
        Retry,

        /// <summary>Reports only at the keep-together probe of the next page.</summary>
        KeepProbe,

        /// <summary>
        /// Reports only from the second piece on, which the document must not accept.
        /// </summary>
        Late,
    }

    internal enum Failure
    {
        None,
        Exception,
        OutOfMemory,
        Cancelled,
    }

    internal sealed record Plan
    {
        /// <summary>Partial results before the Full one in the new layout.</summary>
        internal int NewPages { get; init; }

        /// <summary>Partial results before the Full one in the twin.</summary>
        internal int TwinPages { get; init; }

        /// <summary>
        /// When set, the new layout is a content of this height and each piece fills the offered
        /// area, so its continuation count depends on where it starts. <c>NewPages</c> is then not
        /// read.
        /// </summary>
        internal double? NewUnits { get; init; }

        /// <summary>The same for the twin.</summary>
        internal double? TwinUnits { get; init; }

        /// <summary>The layout answers Nothing wherever it is offered the top of a page.</summary>
        internal bool NewRefusesAtTop { get; init; }

        internal bool TwinRefusesAtTop { get; init; }

        internal long Bound { get; init; } = 1000;
        internal Site Site { get; init; } = Site.Loop;
        internal bool KeepTogether { get; init; }
        internal bool Reports { get; init; } = true;
        internal Failure FailNew { get; init; }
        internal Failure FailTwin { get; init; }

        /// <summary>
        /// Throw only while a probe measures, so the placement that follows runs clean.
        /// </summary>
        internal bool FailOnlyInProbe { get; init; }

        /// <summary>The piece at which a failing layout throws.</summary>
        internal int FailAtPiece { get; init; } = 3;
    }

    internal sealed record Call(
        string Flavor, string Mode, bool Probing, double X, double Y, double Height, bool AtPageTop, bool PlacedAsTwin);

    internal sealed class Log
    {
        internal List<Call> Calls { get; } = [];
        internal List<string> Draws { get; } = [];

        /// <summary>
        /// A probe that went past the bound its renderer reported, which the document trusts.
        /// </summary>
        internal List<string> BoundViolations { get; } = [];

        internal IEnumerable<Call> RealCalls => Calls.Where(c => !c.Probing);
    }

    /// <summary>Draws a rectangle whose grey says which layout produced it.</summary>
    private static void DrawBox(DrawContext context, LayoutBox box, bool asNew)
    {
        var (x, y, w, h) = context.ToPdfRect(box);
        context.Canvas.SetFillColorGray(asNew ? 0.25 : 0.75).Rectangle(x, y, w, h).Fill();
    }

    private sealed class Drawn(Log log, LayoutBox box, bool asNew) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(box);

        public void Draw(DrawContext context)
        {
            log.Draws.Add(asNew ? "new" : "twin");
            DrawBox(context, box, asNew);
        }
    }

    /// <summary>
    /// Takes <c>NewPages</c> continuations in the new layout and <c>TwinPages</c> in the twin (or a
    /// content of <c>NewUnits</c> or <c>TwinUnits</c> in height), and reports as <see cref="Plan"/>
    /// says. A call that does not report lays out as the twin does, which is the contract the
    /// document relies on.
    /// </summary>
    internal class Scripted : IRenderer, IContinuationTwin, IKeepSource
    {
        private protected Plan _plan;
        private readonly Log _log;
        private readonly bool _twin;
        private readonly int _done;
        private readonly double _consumed;
        private protected Scripted? _twinInstance;
        private LayoutBox _lastBox;
        private bool _lastAsNew;

        internal Scripted(Plan plan, Log log) : this(plan, log, false, 0, 0) { }

        private Scripted(Plan plan, Log log, bool twin, int done, double consumed)
        {
            _plan = plan;
            _log = log;
            _twin = twin;
            _done = done;
            _consumed = consumed;
        }

        bool IKeepSource.KeepWithNext => false;
        bool IKeepSource.KeepTogether => _plan.KeepTogether;

        IRenderer IContinuationTwin.Twin() => Twin();

        private Scripted Twin() => _twinInstance ??= _twin ? this : new Scripted(_plan, _log, true, _done, _consumed);

        public LayoutResult Layout(LayoutContext context)
        {
            var mode = context.ModeFor(this);
            if (!_twin && mode == ContinuationMode.Twin) return Twin().Layout(context);

            var room = context.Area.Height >= MinHeight;
            var siteOk = _plan.Site switch
            {
                Site.Loop => true,
                Site.Retry or Site.KeepProbe => context.AtPageTop,
                Site.Late => _done > 0,
                _ => false,
            };
            var reportsNow = !_twin && mode == ContinuationMode.Undecided && _plan.Reports && room && siteOk;

            // Only a call that reported, or that the document decided, lays out the new way. An
            // undecided call that did not report is the twin's.
            var asNew = !_twin && (mode == ContinuationMode.New || reportsNow);
            var probing = context.Session?.Probing ?? false;

            _log.Calls.Add(new Call(
                _twin ? "twin" : "new", mode.ToString(), probing,
                context.Area.X, context.Area.Y, context.Area.Height, context.AtPageTop, context.IsPlacedAsTwin(this)));

            // The bound is what the document trusts, so a probe past it is a test renderer that
            // reported less than it does, and the verdict it bought is not worth reading.
            if (probing && _plan.Reports && _done > _plan.Bound)
                _log.BoundViolations.Add($"{(asNew ? "new" : "twin")} piece {_done} of a bound of {_plan.Bound}");

            var failure = asNew ? _plan.FailNew : _plan.FailTwin;
            if (failure != Failure.None && _done == _plan.FailAtPiece
                && (!_plan.FailOnlyInProbe || probing))
            {
                throw failure switch
                {
                    Failure.OutOfMemory => new OutOfMemoryException("scripted"),
                    Failure.Cancelled => new OperationCanceledException("scripted"),
                    _ => new InvalidOperationException(asNew ? "scripted failure in the new layout" : "scripted failure in the twin"),
                };
            }

            if (reportsNow) context.Session!.Report(this, _plan.Bound);

            if (!room || (_plan.Site == Site.Retry && !context.AtPageTop)) return LayoutResult.Nothing();
            if (context.AtPageTop && (asNew ? _plan.NewRefusesAtTop : _plan.TwinRefusesAtTop)) return LayoutResult.Nothing();

            _lastAsNew = asNew;
            var box = context.Area;
            var units = asNew ? _plan.NewUnits : _plan.TwinUnits;
            bool more;
            if (units is { } total)
            {
                var remaining = total - _consumed;
                more = remaining > box.Height;
                _lastBox = box.WithHeight(Math.Max(MinHeight, Math.Min(remaining, box.Height)));
            }
            else
            {
                more = (asNew ? _plan.NewPages : _plan.TwinPages) - _done > 0;
                _lastBox = box.WithHeight(MinHeight);
            }

            if (more)
            {
                var next = new Scripted(_plan, _log, _twin, _done + 1, _consumed + box.Height);
                return LayoutResult.Partial(box, new Drawn(_log, box, asNew), next);
            }

            return LayoutResult.Full(_lastBox);
        }

        public void Draw(DrawContext context)
        {
            _log.Draws.Add(_lastAsNew ? "new" : "twin");
            DrawBox(context, _lastBox, _lastAsNew);
        }
    }

    /// <summary>
    /// The renderer of a plan-dependent element: each resolve takes the next plan, which changes
    /// the layout and not where the element starts.
    /// </summary>
    internal sealed class DependentScripted(Plan[] byResolve, Log log) : Scripted(byResolve[0], log), IPlanDependent
    {
        private int _resolves;

        public string TargetName => "scripted";

        public string ResolvedText => (Math.Min(_resolves, byResolve.Length) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public bool Resolve(PaginationPlan plan)
        {
            var index = Math.Min(_resolves++, byResolve.Length - 1);
            var changed = _resolves == 1 || !ReferenceEquals(_plan, byResolve[index]);
            _plan = byResolve[index];
            if (changed) _twinInstance = null;
            return changed;
        }
    }

    /// <summary>A fixed-height element, optionally holding on to what follows it.</summary>
    internal sealed class Block(double height, bool keepWithNext = false) : IRenderer, IKeepSource
    {
        bool IKeepSource.KeepWithNext => keepWithNext;
        bool IKeepSource.KeepTogether => false;

        public LayoutResult Layout(LayoutContext context) =>
            context.Area.Height >= height ? LayoutResult.Full(context.Area.WithHeight(height)) : LayoutResult.Nothing();

        public void Draw(DrawContext context) { }
    }

    internal enum WrapKind
    {
        PassThrough,
        WithArea,
        PublicConstructor,
        TwiceAtHalfWidth,
        CachedByArea,
    }

    /// <summary>
    /// A caller's renderer that forwards to another one in one of the ways a caller can.
    /// </summary>
    internal sealed class Wrapper : IRenderer
    {
        private readonly IRenderer _inner;
        private readonly WrapKind _kind;
        private readonly Dictionary<(IRenderer, LayoutBox), LayoutResult> _cache;

        internal Wrapper(IRenderer inner, WrapKind kind) : this(inner, kind, []) { }

        private Wrapper(IRenderer inner, WrapKind kind, Dictionary<(IRenderer, LayoutBox), LayoutResult> cache)
        {
            _inner = inner;
            _kind = kind;
            _cache = cache;
        }

        public LayoutResult Layout(LayoutContext context)
        {
            LayoutResult result;
            switch (_kind)
            {
                case WrapKind.WithArea:
                    result = _inner.Layout(context.WithArea(context.Area));
                    break;
                case WrapKind.PublicConstructor:
                    result = _inner.Layout(new LayoutContext(context.Area));
                    break;
                case WrapKind.TwiceAtHalfWidth:
                    var a = context.Area;
                    _ = _inner.Layout(context.WithArea(new LayoutBox(a.X, a.Y, a.Width / 2, a.Height)));
                    result = _inner.Layout(context);
                    break;
                case WrapKind.CachedByArea:
                    if (!_cache.TryGetValue((_inner, context.Area), out var cached))
                        _cache[(_inner, context.Area)] = cached = _inner.Layout(context);
                    result = cached;
                    break;
                default:
                    result = _inner.Layout(context);
                    break;
            }

            return result.Status == LayoutResult.Outcome.Partial
                ? LayoutResult.Partial(
                    result.OccupiedArea!.Value,
                    new Wrapper(result.SplitRenderer!, _kind, _cache),
                    new Wrapper(result.OverflowRenderer!, _kind, _cache))
                : result;
        }

        public void Draw(DrawContext context) => _inner.Draw(context);
    }

    /// <summary>
    /// One element that lays its parts out one after another, the way a caller's container would.
    /// </summary>
    internal sealed class Sequence : IRenderer
    {
        private readonly IRenderer[] _parts;
        private readonly int _index;

        internal Sequence(IRenderer[] parts, int index = 0)
        {
            _parts = parts;
            _index = index;
        }

        public LayoutResult Layout(LayoutContext context)
        {
            var result = _parts[_index].Layout(context);
            if (result.Status == LayoutResult.Outcome.Partial)
            {
                var rest = _parts.ToArray();
                rest[_index] = result.OverflowRenderer!;
                return LayoutResult.Partial(result.OccupiedArea!.Value, result.SplitRenderer!, new Sequence(rest, _index));
            }

            if (result.Status == LayoutResult.Outcome.Full && _index + 1 < _parts.Length)
                return LayoutResult.Partial(result.OccupiedArea!.Value, new Wrapper(_parts[_index], WrapKind.PassThrough), new Sequence(_parts, _index + 1));

            return result;
        }

        public void Draw(DrawContext context) => _parts[_index].Draw(context);
    }

    /// <summary>
    /// A built-in-style parent that lays one child out through <see cref="LayoutContext.Address"/>
    /// and records the context the child got. It never reports, so a reporting child under it is
    /// the silent mistake the document counts.
    /// </summary>
    internal sealed class Parent(IRenderer child, List<LayoutContext> seen) : IRenderer, IContinuationTwin
    {
        IRenderer IContinuationTwin.Twin() => this;

        public LayoutResult Layout(LayoutContext context)
        {
            var childContext = context.Address(this, child, context.Area);
            seen.Add(childContext);
            var result = child.Layout(childContext);
            return result.Status == LayoutResult.Outcome.Partial
                ? LayoutResult.Partial(result.OccupiedArea!.Value, result.SplitRenderer!, result.OverflowRenderer!)
                : LayoutResult.Full(context.Area.WithHeight(1));
        }

        public void Draw(DrawContext context) { }
    }

    /// <summary>
    /// A built-in-style parent that adopts the mechanism: it reports a bound for itself, lays its
    /// child out in its own mode through <see cref="LayoutContext.Address"/>, and is its own twin
    /// with the child on the twin. The child must place a piece, so the parent never draws.
    /// </summary>
    internal sealed class ReportingParent : IRenderer, IContinuationTwin
    {
        private readonly IRenderer _child;
        private readonly long _bound;
        private readonly bool _reports;
        private readonly bool _twin;
        private ReportingParent? _twinInstance;

        internal ReportingParent(IRenderer child, long bound, bool reports = true) : this(child, bound, reports, false) { }

        private ReportingParent(IRenderer child, long bound, bool reports, bool twin)
        {
            _child = child;
            _bound = bound;
            _reports = reports;
            _twin = twin;
        }

        IRenderer IContinuationTwin.Twin() => Twin();

        private ReportingParent Twin() => _twinInstance ??= _twin ? this : new ReportingParent(_child, _bound, _reports, true);

        public LayoutResult Layout(LayoutContext context)
        {
            var mode = context.ModeFor(this);
            if (!_twin && mode == ContinuationMode.Twin) return Twin().Layout(context);

            if (!_twin && mode == ContinuationMode.Undecided && _reports) context.Session!.Report(this, _bound);

            var result = _child.Layout(context.Address(this, _child, context.Area));
            return result.Status == LayoutResult.Outcome.Partial
                ? LayoutResult.Partial(result.OccupiedArea!.Value, result.SplitRenderer!, result.OverflowRenderer!)
                : throw new InvalidOperationException("The child must place a piece.");
        }

        public void Draw(DrawContext context) { }
    }

    internal sealed record Setup
    {
        internal double PageHeight { get; init; } = 100;
        internal bool Footer { get; init; }
        internal bool Mirror { get; init; }

        /// <summary>
        /// Lets a test provoke an unreported child, which fails a Debug build otherwise.
        /// </summary>
        internal bool AllowUnreportedChild { get; init; }
        internal int Limit { get; init; } = ContinuationSupport.Limit;
    }

    internal sealed record Outcome(
        Exception? Error, byte[]? Bytes, int Pages, Log Log, int Probes, int Faults, DocumentRenderer Renderer);

    internal static double ContentHeight(Setup setup) => setup.PageHeight - 20;

    /// <summary>
    /// Places the elements, with a pinned timestamp and id, and returns what happened.
    /// </summary>
    internal static Outcome Run(Setup setup, Log log, params IRenderer[] elements)
    {
        var pdf = PlacementLoopSupport.NewPdf();
        var margins = setup.Mirror ? new EdgeInsets(10, 5, 10, 25) : new EdgeInsets(10);
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, setup.PageHeight), margins)
        {
            ContinuationLimit = setup.Limit,
            MirrorMargins = setup.Mirror,
            AssertOnUnreportedChild = !setup.AllowUnreportedChild,
        };
        if (setup.Footer) renderer.Footer = new RunningBand("f");
        foreach (var e in elements) renderer.Add(e);

        Exception? error = null;
        byte[]? bytes = null;
        try
        {
            bytes = PlacementLoopSupport.Render(renderer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            error = ex;
        }

        Assert.Empty(log.BoundViolations);
        if (!setup.AllowUnreportedChild) Assert.Equal(0, renderer.UnreportedChildReports);
        return new Outcome(error, bytes, bytes is null ? 0 : PlacementLoopSupport.PageCount(bytes), log,
            renderer.ContinuationProbes, renderer.ContinuationProbeFaults, renderer);
    }

    internal static bool IsLimitRefusal(Exception? ex) =>
        ex is InvalidOperationException && ex.Message.Contains("page continuations", StringComparison.Ordinal);
}
