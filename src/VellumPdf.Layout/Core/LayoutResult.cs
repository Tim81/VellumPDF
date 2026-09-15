// Copyright © Timothy van der Ham (@Tim81)
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
    /// <remarks>
    /// Read this before <see cref="OccupiedArea"/>, <see cref="SplitRenderer"/> or
    /// <see cref="OverflowRenderer"/>. Those are null on <see cref="Outcome.Nothing"/>.
    /// </remarks>
    public Outcome Status { get; }

    /// <summary>The occupied area after layout (valid for Full and Partial).</summary>
    /// <remarks>
    /// A <see cref="Outcome.Nothing"/> result returns <see langword="null"/>. Do not dereference
    /// this without checking <see cref="Status"/>.
    /// </remarks>
    public LayoutBox? OccupiedArea { get; }

    /// <summary>Renderer representing the part that fit (Partial only).</summary>
    /// <remarks>
    /// Null on <see cref="Outcome.Full"/> and <see cref="Outcome.Nothing"/>.
    /// </remarks>
    public IRenderer? SplitRenderer { get; }

    /// <summary>Renderer representing overflow to be placed on the next page (Partial only).</summary>
    /// <remarks>
    /// Null on <see cref="Outcome.Full"/> and <see cref="Outcome.Nothing"/>. The overflow is
    /// accepted as given: a renderer that never advances hits the 50,000-continuation cap
    /// described on <see cref="IRenderer.Layout"/>.
    /// </remarks>
    public IRenderer? OverflowRenderer { get; }

    private LayoutResult(Outcome status, LayoutBox? area, IRenderer? split, IRenderer? overflow)
    {
        Status = status;
        OccupiedArea = area;
        SplitRenderer = split;
        OverflowRenderer = overflow;
    }

    /// <summary>Creates a result indicating the content fit entirely, occupying the given area.</summary>
    /// <remarks>Occupied area is stored as given, including an empty box.</remarks>
    public static LayoutResult Full(LayoutBox occupied) =>
        new(Outcome.Full, occupied, null, null);

    /// <summary>Creates a result indicating the content was split, with the part that fit and the overflow to place on the next page.</summary>
    /// <remarks>
    /// Neither renderer is inspected here. An overflow that occupies the same height on every
    /// call hits the 50,000-continuation cap on <see cref="IRenderer.Layout"/> at save.
    /// </remarks>
    public static LayoutResult Partial(LayoutBox occupied, IRenderer split, IRenderer overflow) =>
        new(Outcome.Partial, occupied, split, overflow);

    /// <summary>Creates a result indicating no content fit in the available area.</summary>
    /// <remarks>
    /// <see cref="OccupiedArea"/>, <see cref="SplitRenderer"/> and
    /// <see cref="OverflowRenderer"/> are all null.
    /// </remarks>
    public static LayoutResult Nothing() =>
        new(Outcome.Nothing, null, null, null);
}
