// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// Result of <see cref="IRenderer.Layout"/>. The three outcomes drive pagination:
/// Full — content fit entirely; Partial — some fit, overflow goes to next page;
/// Nothing — nothing fit (content taller than a single page).
/// </summary>
public sealed class LayoutResult
{
    /// <summary>The possible outcomes of a layout attempt.</summary>
    public enum Outcome
    {
        /// <summary>The content fit entirely within the available area.</summary>
        Full,

        /// <summary>Part of the content fit; the remainder overflows to the next page.</summary>
        Partial,

        /// <summary>No content fit (it is taller than a single page).</summary>
        Nothing,
    }

    /// <summary>The outcome of the layout attempt.</summary>
    public Outcome Status { get; }

    /// <summary>The occupied area after layout (valid for Full and Partial).</summary>
    public LayoutBox? OccupiedArea { get; }

    /// <summary>Renderer representing the part that fit (Partial only).</summary>
    public IRenderer? SplitRenderer { get; }

    /// <summary>Renderer representing overflow to be placed on the next page (Partial only).</summary>
    public IRenderer? OverflowRenderer { get; }

    private LayoutResult(Outcome status, LayoutBox? area, IRenderer? split, IRenderer? overflow)
    {
        Status = status;
        OccupiedArea = area;
        SplitRenderer = split;
        OverflowRenderer = overflow;
    }

    /// <summary>Creates a result indicating the content fit entirely, occupying the given area.</summary>
    public static LayoutResult Full(LayoutBox occupied) =>
        new(Outcome.Full, occupied, null, null);

    /// <summary>Creates a result indicating the content was split, with the part that fit and the overflow to place on the next page.</summary>
    public static LayoutResult Partial(LayoutBox occupied, IRenderer split, IRenderer overflow) =>
        new(Outcome.Partial, occupied, split, overflow);

    /// <summary>Creates a result indicating no content fit in the available area.</summary>
    public static LayoutResult Nothing() =>
        new(Outcome.Nothing, null, null, null);
}
