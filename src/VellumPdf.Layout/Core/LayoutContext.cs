// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Core;

/// <summary>
/// Passed to <see cref="IRenderer.Layout"/>. Carries the available area and
/// any constraints that flow down from the parent renderer.
/// </summary>
/// <remarks>
/// Nothing here is checked. A renderer receives the area exactly as its caller built it,
/// including an empty or inverted one.
/// </remarks>
public sealed class LayoutContext
{
    /// <summary>The area available for this renderer to lay out into.</summary>
    /// <remarks>
    /// When the document lays out an element, the area runs from the current position to the bottom
    /// of the page's content area. The content area already excludes the page margins and any
    /// header or footer band.
    /// <para>The first area offered at the top of a page, which is a new page or the page after a
    /// <see cref="LayoutResult.Outcome.Partial"/>, is as tall as the larger of the content area's
    /// height and the height from the top of the content area to its bottom. The two can differ by
    /// one rounding step. The larger one is offered so that an element exactly as tall as the
    /// content area is placed there. The retry after a <see cref="LayoutResult.Outcome.Nothing"/>
    /// is offered the content area itself. Elsewhere on a page the area ends at the bottom of the
    /// content area. A renderer that fits a smaller area but not a larger one is not supported;
    /// see <see cref="IRenderer.Layout"/>.</para>
    /// </remarks>
    public LayoutBox Area { get; }

    /// <summary>
    /// The content area of the page a <see cref="LayoutResult.Outcome.Nothing"/> answer is retried
    /// on, which is what the document offers that retry. Null on a context built through the
    /// public constructor.
    /// </summary>
    /// <remarks>
    /// On the first call for an element this is the area of the next page, and on the retry it is
    /// the area of the page the element is already on. The two have the same width and height, and
    /// under <see cref="Document.MirrorMargins"/> with different left and right margins they have
    /// a different X.
    /// The document sets it on every layout call, in both passes, so a renderer can tell whether
    /// an element that does not fit the area it was offered could fit a page of its own. The area
    /// the document offers at the top of a page is at least this tall, so an element that fits
    /// this box fits there and on the retry; see <see cref="Area"/>. Where the height left below
    /// the top of the content area is the larger of the two, the area offered at the top of a page
    /// is taller than this box, and an element that fits only that taller area is placed on a
    /// page of its own only when it starts one.
    /// </remarks>
    internal LayoutBox? FreshPageArea { get; init; }

    /// <summary>
    /// Whether the area starts at the top of a page's content area, so that answering
    /// <see cref="LayoutResult.Outcome.Nothing"/> can only move the element to a page that is no
    /// better. The document sets it on every layout call. A renderer that holds a line count or a
    /// keep rule drops it there and splits where the page ends, because a page top cannot be left
    /// for another. False on a context built through the public constructor, and not what
    /// <see cref="ContentTop"/> says: that stays 0.
    /// </summary>
    internal bool AtPageTop { get; init; }

    /// <summary>
    /// Whether the paragraph this context is handed to reads its line counts. The document sets it
    /// on a call to a paragraph renderer that was added to it, and on the pieces that renderer's own
    /// splits return. It is
    /// false on a context built through the public constructor and on one made by
    /// <see cref="WithArea"/>, so a renderer of yours, or a direct call, places as many lines as
    /// fit.
    /// </summary>
    internal bool HonourLineCounts { get; init; }

    // Target, Mode and Session exist only to keep the 2.3.3 continuation verdict; removed in 3.0
    // (#688).
    /// <summary>
    /// The renderer the document called with this context. Null on a context built through the
    /// public constructor or made by <see cref="WithArea"/>.
    /// </summary>
    internal IRenderer? Target { get; init; }

    /// <summary>
    /// Which layout <see cref="Target"/> uses. Read through <see cref="ModeFor"/>, never
    /// directly, because it means nothing to any other renderer.
    /// </summary>
    internal ContinuationMode Mode { get; init; }

    /// <summary>The session a renderer reports to; null wherever <see cref="Target"/> is.</summary>
    internal ContinuationSession? Session { get; init; }

    // 2.x compatibility device, removed in 3.0 (#688): 3.0 lays a built-in renderer out one way
    // under every context, so there is no twin to select.
    /// <summary>
    /// The layout <paramref name="renderer"/> uses under this context: the mode the document
    /// decided when this context is addressed to that instance, and the twin under any other
    /// context. That is what makes a renderer of a caller's, a context from
    /// <see cref="WithArea"/> and one from the public constructor lay a built-in renderer out as
    /// 2.3.3 did.
    /// </summary>
    internal ContinuationMode ModeFor(IRenderer renderer) =>
        ReferenceEquals(Target, renderer) ? Mode : ContinuationMode.Twin;

    /// <summary>
    /// A context for a built-in renderer that <paramref name="parent"/>, itself called under this
    /// context, lays out as part of its own layout: addressed to <paramref name="child"/> and in
    /// the parent's own mode, with the fresh-page area carried as <see cref="WithArea"/> carries
    /// it. A parent that lays a child out under any other context leaves the child on its twin.
    /// </summary>
    internal LayoutContext Address(IRenderer parent, IRenderer child, LayoutBox area) =>
        new(area, ContentTop)
        {
            FreshPageArea = FreshPageArea,
            Target = child,
            Mode = ModeFor(parent),
            Session = Session,
        };

    /// <summary>A minimum Y position, carried for callers that set one.</summary>
    /// <remarks>
    /// Nothing in this package acts on it, and the document passes <b>0</b> on every call. Use
    /// <see cref="Area"/> to find where content may start.
    /// </remarks>
    public double ContentTop { get; }

    /// <summary>Creates a context for the given available area and optional content top.</summary>
    /// <remarks>
    /// Nothing is refused. Both arguments are stored as passed.
    /// <para><b>A context you build is not the document's.</b> A built-in renderer that you lay
    /// out under it uses the layout it had in 2.3.3 wherever a later fix would lay it out
    /// differently. That keeps the verdict on the 50,000-continuation limit that the same element
    /// had in 2.3.3. <b>Do not</b> lay a built-in renderer out under a context you built and expect
    /// a fix that changes its layout to apply to it.</para>
    /// </remarks>
    public LayoutContext(LayoutBox area, double contentTop = 0)
    {
        Area = area;
        ContentTop = contentTop;
    }

    /// <summary>Returns a copy of this context with the available area replaced.</summary>
    /// <remarks>
    /// Nothing is refused. <see cref="ContentTop"/> is copied unchanged, and so is the content area
    /// a fresh page offers, which this package reads and which is null on a context built through
    /// the public constructor. The two flags the document sets for its own paragraphs, that the area
    /// is a page top and that a paragraph reads its line counts, are not copied: a smaller area is
    /// not known to be a page top, and a renderer that wraps a paragraph renderer places as many
    /// lines as fit.
    /// <para><b>The copy is not the document's context.</b> A built-in renderer that you lay out
    /// under it uses the layout it had in 2.3.3 wherever a later fix would lay it out differently,
    /// which keeps the continuation limit's verdict for the element as it was in 2.3.3. <b>Do
    /// not</b> narrow a context for a built-in renderer and expect a fix that changes its layout
    /// to apply to it.</para>
    /// </remarks>
    public LayoutContext WithArea(LayoutBox area) => new(area, ContentTop) { FreshPageArea = FreshPageArea };
}
