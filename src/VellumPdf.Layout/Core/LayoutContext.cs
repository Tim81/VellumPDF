// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// Passed to <see cref="IRenderer.Layout"/>. Carries the available area and
/// any constraints that flow down from the parent renderer.
/// </summary>
public sealed class LayoutContext
{
    /// <summary>The area available for this renderer to lay out into.</summary>
    public LayoutBox Area { get; }

    /// <summary>
    /// Minimum Y position on the current page that content may start at
    /// (used by DocumentRenderer to skip the reserved header area).
    /// </summary>
    public double ContentTop { get; }

    /// <summary>Creates a context for the given available area and optional content top.</summary>
    public LayoutContext(LayoutBox area, double contentTop = 0)
    {
        Area = area;
        ContentTop = contentTop;
    }

    /// <summary>Returns a copy of this context with the available area replaced.</summary>
    public LayoutContext WithArea(LayoutBox area) => new(area, ContentTop);
}
