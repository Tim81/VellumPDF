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
    /// Not decided yet. The renderer lays out its new way and reports a bound if its result can
    /// differ from the twin's, and the document then decides before it uses that result.
    /// </summary>
    Undecided = 0,

    /// <summary>The new layout.</summary>
    New,

    /// <summary>The layout as 2.3.3 produced it, which is what <see cref="IContinuationTwin.Twin"/> returns.</summary>
    Twin,
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
/// (#686). One lives for each <c>PlaceRenderer</c> activation and is reached from the context of
/// every call it makes, so a renderer reports without knowing the document.
/// </summary>
internal sealed class ContinuationSession
{
    internal ContinuationSession(ContinuationMode mode = ContinuationMode.Undecided, bool probing = false)
    {
        Mode = mode;
        Probing = probing;
    }

    /// <summary>
    /// Undecided until the first call that reports, or the first piece placed without a report,
    /// then New or Twin for the rest of the placement. Fixed for a probe, which never decides.
    /// </summary>
    internal ContinuationMode Mode { get; set; }

    /// <summary>True for a layout-only placement that measures one mode and decides nothing.</summary>
    internal bool Probing { get; }

    /// <summary>Set by a probe when its loop reached the continuation limit instead of throwing.</summary>
    internal bool LimitHit { get; set; }

    /// <summary>The instance the call in progress is addressed to.</summary>
    internal IRenderer? Called { get; set; }

    internal bool Reported { get; private set; }

    internal long Bound { get; private set; }

    internal IContinuationTwin? Reporter { get; private set; }

    /// <summary>
    /// A renderer reports that its result in this call, or in a later one, can differ from its
    /// twin's, with an upper bound on the continuations of the rest of its placement in either
    /// layout, valid when the document places it directly.
    /// </summary>
    /// <remarks>
    /// <b>If a call made no report, its result equals the twin's.</b> The decision relies on it.
    /// A report from an instance the document did not address, such as a child a built-in parent
    /// lays out, or one made after the mode is decided or inside a probe, is dropped: the parent
    /// reports for itself.
    /// </remarks>
    internal void Report<T>(T renderer, long bound)
        where T : IRenderer, IContinuationTwin
    {
        if (Mode != ContinuationMode.Undecided || Probing || !ReferenceEquals(renderer, Called))
            return;

        if (!Reported || bound > Bound) Bound = bound;
        Reported = true;
        Reporter = renderer;
    }

    /// <summary>Whether the call just made reported, and forgets it.</summary>
    internal bool TakeReport()
    {
        var reported = Reported;
        Reported = false;
        return reported;
    }
}
