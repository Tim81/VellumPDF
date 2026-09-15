// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// Passed to <see cref="IRenderer.Layout"/>. Carries the available area and
/// any constraints that flow down from the parent renderer.
/// </summary>
/// <remarks>
/// The area is not validated. An empty or inverted <see cref="Area"/> is what renderers
/// see.
/// </remarks>
public sealed class LayoutContext
{
    /// <summary>The area available for this renderer to lay out into.</summary>
    /// <remarks>Not validated. See the type.</remarks>
    public LayoutBox Area { get; }

    /// <summary>
    /// Minimum Y position on the current page that content may start at
    /// (used by DocumentRenderer to skip the reserved header area).
    /// </summary>
    /// <remarks>Not refused. A non-finite value is stored as given.</remarks>
    public double ContentTop { get; }

    /// <summary>Creates a context for the given available area and optional content top.</summary>
    /// <remarks>Arguments are stored as given. See the type.</remarks>
    public LayoutContext(LayoutBox area, double contentTop = 0)
    {
        Area = area;
        ContentTop = contentTop;
    }

    /// <summary>Returns a copy of this context with the available area replaced.</summary>
    /// <remarks>Not refused. <see cref="ContentTop"/> is kept.</remarks>
    public LayoutContext WithArea(LayoutBox area) => new(area, ContentTop);
}
