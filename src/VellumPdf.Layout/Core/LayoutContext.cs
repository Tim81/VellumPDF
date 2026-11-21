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
    /// header or footer band. On a fresh page this area can differ from the content area by one
    /// rounding step. When it is the shorter of the two, a renderer that needs the whole content
    /// area exactly can reach the page-continuation limit; see <see cref="IRenderer.Layout"/>
    /// (#549).
    /// </remarks>
    public LayoutBox Area { get; }

    /// <summary>
    /// The content area every fresh page offers, which is what the document offers the retry after
    /// a <see cref="LayoutResult.Outcome.Nothing"/>. Null on a context built through the public
    /// constructor.
    /// </summary>
    /// <remarks>
    /// The document sets it on every layout call, in both passes, so a renderer can tell whether
    /// an element that does not fit the area it was offered could fit a page of its own. A fresh
    /// page can offer one rounding step less than this box; see <see cref="Area"/> (#549). A
    /// renderer that splits an element only when it fits neither box therefore leaves alone
    /// every element that either box places whole.
    /// </remarks>
    internal LayoutBox? FreshPageArea { get; init; }

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
    public LayoutContext WithArea(LayoutBox area) => new(area, ContentTop) { FreshPageArea = FreshPageArea };
}
