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

        /// <summary>Reports only from the second piece on, which the document must not accept.</summary>
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

        internal long Bound { get; init; } = 1000;
        internal Site Site { get; init; } = Site.Loop;
        internal bool KeepTogether { get; init; }
        internal bool Reports { get; init; } = true;
        internal Failure FailNew { get; init; }
        internal Failure FailTwin { get; init; }

        /// <summary>Throw only while a probe measures, so the placement that follows runs clean.</summary>
        internal bool FailOnlyInProbe { get; init; }

        /// <summary>The piece at which a failing layout throws.</summary>
        internal int FailAtPiece { get; init; } = 3;
    }

    internal sealed record Call(string Flavor, string Mode, bool Probing, double X, double Y, double Height, bool AtPageTop);

    internal sealed class Log
    {
        internal List<Call> Calls { get; } = [];
        internal List<string> Draws { get; } = [];

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
    /// Takes <c>Pages</c> continuations in the new layout and <c>TwinPages</c> in the twin, and
    /// reports as <see cref="Plan"/> says. A call that does not report lays out as the twin does,
    /// which is the contract the document relies on.
    /// </summary>
    internal sealed class Scripted : IRenderer, IContinuationTwin, IKeepSource
    {
        private readonly Plan _plan;
        private readonly Log _log;
        private readonly bool _twin;
        private readonly int _done;
        private Scripted? _twinInstance;
        private LayoutBox _lastBox;
        private bool _lastAsNew;

        internal Scripted(Plan plan, Log log) : this(plan, log, false, 0) { }

        private Scripted(Plan plan, Log log, bool twin, int done)
        {
            _plan = plan;
            _log = log;
            _twin = twin;
            _done = done;
        }

        bool IKeepSource.KeepWithNext => false;
        bool IKeepSource.KeepTogether => _plan.KeepTogether;

        IRenderer IContinuationTwin.Twin() => Twin();

        private Scripted Twin() => _twinInstance ??= _twin ? this : new Scripted(_plan, _log, true, _done);

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
            var asNew = !_twin && (mode == ContinuationMode.New || reportsNow);

            _log.Calls.Add(new Call(
                _twin ? "twin" : "new", mode.ToString(), context.Session?.Probing ?? false,
                context.Area.X, context.Area.Y, context.Area.Height, context.AtPageTop));

            var failure = asNew ? _plan.FailNew : _twin ? _plan.FailTwin : Failure.None;
            if (failure != Failure.None && _done == _plan.FailAtPiece
                && (!_plan.FailOnlyInProbe || (context.Session?.Probing ?? false)))
            {
                throw failure switch
                {
                    Failure.OutOfMemory => new OutOfMemoryException("scripted"),
                    Failure.Cancelled => new OperationCanceledException("scripted"),
                    _ => new InvalidOperationException("scripted failure"),
                };
            }

            if (reportsNow) context.Session!.Report(this, _plan.Bound);

            if (!room || (_plan.Site == Site.Retry && !context.AtPageTop)) return LayoutResult.Nothing();

            var left = (_twin ? _plan.TwinPages : _plan.NewPages) - _done;
            _lastAsNew = asNew;
            if (left > 0)
            {
                var box = context.Area;
                var next = new Scripted(_plan, _log, _twin, _done + 1);
                return LayoutResult.Partial(box, new Drawn(_log, box, asNew), next);
            }

            _lastBox = context.Area.WithHeight(MinHeight);
            return LayoutResult.Full(_lastBox);
        }

        public void Draw(DrawContext context)
        {
            _log.Draws.Add(_lastAsNew ? "new" : "twin");
            DrawBox(context, _lastBox, _lastAsNew);
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

    /// <summary>A caller's renderer that forwards to another one in one of the ways a caller can.</summary>
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

    /// <summary>One element that lays its parts out one after another, the way a caller's container would.</summary>
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
    /// A built-in-style parent: lays one scripted child out through <see cref="LayoutContext.Address"/>
    /// and records the mode the child saw.
    /// </summary>
    internal sealed class Parent(IRenderer child, List<ContinuationMode> seen) : IRenderer, IContinuationTwin
    {
        IRenderer IContinuationTwin.Twin() => this;

        public LayoutResult Layout(LayoutContext context)
        {
            var childContext = context.Address(this, child, context.Area);
            seen.Add(childContext.ModeFor(child));
            return LayoutResult.Full(context.Area.WithHeight(1));
        }

        public void Draw(DrawContext context) { }
    }

    internal sealed record Setup
    {
        internal double PageHeight { get; init; } = 100;
        internal bool Footer { get; init; }
        internal bool Mirror { get; init; }
        internal int Limit { get; init; } = ContinuationSupport.Limit;
    }

    internal sealed record Outcome(
        Exception? Error, byte[]? Bytes, int Pages, Log Log, int Probes, int Faults, DocumentRenderer Renderer);

    internal static double ContentHeight(Setup setup) => setup.PageHeight - 20;

    /// <summary>Places the elements, with a pinned timestamp and id, and returns what happened.</summary>
    internal static Outcome Run(Setup setup, Log log, params IRenderer[] elements)
    {
        var pdf = PlacementLoopSupport.NewPdf();
        var margins = setup.Mirror ? new EdgeInsets(10, 5, 10, 25) : new EdgeInsets(10);
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, setup.PageHeight), margins)
        {
            ContinuationLimit = setup.Limit,
            MirrorMargins = setup.Mirror,
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

        return new Outcome(error, bytes, bytes is null ? 0 : PlacementLoopSupport.PageCount(bytes), log,
            renderer.ContinuationProbes, renderer.ContinuationProbeFaults, renderer);
    }

    internal static bool IsLimitRefusal(Exception? ex) =>
        ex is InvalidOperationException && ex.Message.Contains("page continuations", StringComparison.Ordinal);
}
