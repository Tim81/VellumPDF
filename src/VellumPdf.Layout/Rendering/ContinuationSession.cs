// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Rendering;

// Everything in this file is a 2.x compatibility device and is removed in 3.0 (#688).
/// <summary>
/// Which layout a renderer that can lay out differently from 2.3.3 uses for one placement.
/// </summary>
internal enum ContinuationMode
{
    /// <summary>
    /// The layout as 2.3.3 produced it, which is what <see cref="IContinuationTwin.Twin"/> returns.
    /// It is the zero value, so a field that holds a mode and was never set is the safe one.
    /// </summary>
    Twin = 0,

    /// <summary>
    /// Not decided yet. The renderer lays out its new way and reports a bound if its result can
    /// differ from the twin's, and the document then decides before it uses that result.
    /// </summary>
    Undecided,

    /// <summary>The new layout.</summary>
    New,
}

/// <summary>
/// Implemented, explicitly, by a built-in renderer whose layout differs from 2.3.3's in some
/// case: the renderer laid out the way 2.3.3 laid it out.
/// </summary>
/// <remarks>
/// The twin is what keeps the continuation limit's verdict. A renderer uses its new layout only
/// under a context the document, or a built-in parent, addressed to that instance
/// (<see cref="LayoutContext.ModeFor"/>), and on any other context it lays out as its twin.
/// </remarks>
internal interface IContinuationTwin
{
    /// <summary>
    /// The renderer laid out the way it was laid out before the fix, from the same start. The
    /// same object on every call.
    /// </summary>
    IRenderer Twin();
}

/// <summary>
/// The state of one placement loop's decision between a renderer's new layout and its twin
/// (#686). The document keeps one for its own placements and resets it for each element, so an
/// element whose renderer never reports costs only the reference each context carries. A probe has
/// one of its own, and a built-in parent that lays a child out gets a view of it from
/// <see cref="LayoutContext.Address"/>. The context of every call reaches it, so a renderer reports
/// without knowing the document.
/// </summary>
internal sealed class ContinuationSession
{
    private readonly bool _view;
    private ContinuationSession? _root;

    internal ContinuationSession(ContinuationMode mode = ContinuationMode.Undecided, bool probing = false)
    {
        Mode = mode;
        Probing = probing;
    }

    private ContinuationSession(ContinuationMode mode, bool probing, bool view)
        : this(mode, probing) => _view = view;

    /// <summary>
    /// A session for a child that a built-in parent lays out in <paramref name="mode"/>, called
    /// with <paramref name="child"/>. A report made to it is never read: the parent reports for
    /// itself, and a report from a child while the mode is undecided is counted on the document's
    /// session as <see cref="Strays"/>, so a parent that forgot to report is found.
    /// </summary>
    internal static ContinuationSession ViewFor(IRenderer child, ContinuationMode mode, ContinuationSession? from) =>
        new(mode, from?.Probing ?? false, view: true) { Called = child, _root = from?._root ?? from, Pdf = from?.Pdf };

    /// <summary>
    /// Undecided until the first call that reports, or the first piece placed without a report,
    /// then New or Twin for the rest of the placement. Fixed for a probe, which never decides.
    /// </summary>
    internal ContinuationMode Mode { get; set; }

    /// <summary>
    /// True once a decision committed <see cref="ContinuationMode.Twin"/> for this placement. It
    /// stays set for every later call of the placement, including the twin's overflow pieces, and
    /// is not set where <see cref="Mode"/> became Twin because the first piece was placed with no
    /// report.
    /// </summary>
    internal bool TwinChosen { get; set; }

    /// <summary>
    /// The document the placement is for, so a renderer can ask whether what it would draw is
    /// drawable in it before it lays out text it used to discard. Null for a session a test built.
    /// </summary>
    internal VellumPdf.Document.PdfDocument? Pdf { get; init; }

    /// <summary>True for a layout-only placement that measures one mode.</summary>
    internal bool Probing { get; }

    /// <summary>Set by a probe whose loop reached the continuation limit.</summary>
    internal bool LimitHit { get; set; }

    /// <summary>The instance the call in progress is addressed to.</summary>
    internal IRenderer? Called { get; set; }

    /// <summary>
    /// A call left a report or a stray report to settle. The loop reads only this after each call,
    /// so a renderer that reports nothing costs it one field.
    /// </summary>
    internal bool Pending { get; private set; }

    internal bool Reported { get; private set; }

    internal long Bound { get; private set; }

    internal IContinuationTwin? Reporter { get; private set; }

    /// <summary>
    /// Reports made in the call in progress by an instance the document did not call, while the
    /// mode was undecided. A call that ends with some of these and no report of its own is a
    /// built-in parent that addressed a reporting child and did not report for itself: the child
    /// laid out its new way and the parent's result is neither layout.
    /// </summary>
    internal int Strays { get; private set; }

    /// <summary>Forgets the last placement so the session can serve the next element.</summary>
    internal void Reset()
    {
        // What a report wrote (Bound, Reporter) is read only after the next report has overwritten
        // it.
        Mode = ContinuationMode.Undecided;
        TwinChosen = false;
        Reported = false;
        Pending = false;
        Strays = 0;
    }

    /// <summary>
    /// A renderer reports that its result in this call, or in a later one, can differ from its
    /// twin's, with an upper bound on the continuations of the rest of its placement.
    /// </summary>
    /// <remarks>
    /// <b>The bound must cover both layouts.</b> The document trusts it completely: a bound within
    /// what is left of the limit means no probe runs, so a bound that holds for the twin's layout
    /// and not for the new one lets the new layout reach the limit where 2.3.3 saved. A bound
    /// that is too large only costs two probes. It holds where the document places the element
    /// directly, from the start of the call that reports.
    /// <para><b>If a call made no report, its result equals the twin's.</b> The decision relies on
    /// it. A report from an instance the document did not call, such as a child a built-in parent
    /// lays out, or one made after the mode is decided or inside a probe, is dropped: the parent
    /// reports for itself.</para>
    /// </remarks>
    internal void Report<T>(T renderer, long bound)
        where T : IRenderer, IContinuationTwin
    {
        if (Mode != ContinuationMode.Undecided || Probing) return;

        if (_view)
        {
            _root?.NoteStray();
            return;
        }

        if (!ReferenceEquals(renderer, Called))
        {
            NoteStray();
            return;
        }

        if (!Reported || bound > Bound) Bound = bound;
        Reported = true;
        Pending = true;
        Reporter = renderer;
    }

    internal void ClearStrays() => Strays = 0;

    private void NoteStray()
    {
        Strays++;
        Pending = true;
    }

    /// <summary>Whether the call just made reported, and forgets it.</summary>
    internal bool TakeReport()
    {
        var reported = Reported;
        Reported = false;
        Pending = false;
        return reported;
    }
}
