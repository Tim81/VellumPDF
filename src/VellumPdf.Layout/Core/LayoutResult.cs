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
    public enum Outcome { Full, Partial, Nothing }

    public Outcome Status { get; }

    /// <summary>The occupied area after layout (valid for Full and Partial).</summary>
    public LayoutBox? OccupiedArea { get; }

    /// <summary>Renderer representing the part that fit (Partial only).</summary>
    public IRenderer? SplitRenderer { get; }

    /// <summary>Renderer representing overflow to be placed on the next page (Partial only).</summary>
    public IRenderer? OverflowRenderer { get; }

    private LayoutResult(Outcome status, LayoutBox? area, IRenderer? split, IRenderer? overflow)
    {
        Status           = status;
        OccupiedArea     = area;
        SplitRenderer    = split;
        OverflowRenderer = overflow;
    }

    public static LayoutResult Full(LayoutBox occupied) =>
        new(Outcome.Full, occupied, null, null);

    public static LayoutResult Partial(LayoutBox occupied, IRenderer split, IRenderer overflow) =>
        new(Outcome.Partial, occupied, split, overflow);

    public static LayoutResult Nothing() =>
        new(Outcome.Nothing, null, null, null);
}
