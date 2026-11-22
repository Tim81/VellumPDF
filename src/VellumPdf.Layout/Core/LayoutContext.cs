// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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

    /// <summary>A minimum Y position, carried for callers that set one.</summary>
    /// <remarks>
    /// Nothing in this package acts on it, and the document passes <b>0</b> on every call. Use
    /// <see cref="Area"/> to find where content may start.
    /// </remarks>
    public double ContentTop { get; }

    /// <summary>Creates a context for the given available area and optional content top.</summary>
    /// <remarks>Nothing is refused. Both arguments are stored as passed.</remarks>
    public LayoutContext(LayoutBox area, double contentTop = 0)
    {
        Area = area;
        ContentTop = contentTop;
    }

    /// <summary>Returns a copy of this context with the available area replaced.</summary>
    /// <remarks>
    /// Nothing is refused. <see cref="ContentTop"/> is copied unchanged, and so is the content area
    /// a fresh page offers, which this package reads and which is null on a context built through
    /// the public constructor.
    /// </remarks>
    public LayoutContext WithArea(LayoutBox area) => new(area, ContentTop) { FreshPageArea = FreshPageArea, AtPageTop = AtPageTop };
}
