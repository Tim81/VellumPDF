// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// A renderer that can say where a part of what it placed landed. <see cref="DocumentRenderer"/>
/// calls <see cref="ReportPlacements"/> on the renderer whose result it accepted: the renderer
/// itself for <see cref="LayoutResult.Outcome.Full"/>, the <see cref="LayoutResult.SplitRenderer"/>
/// for <see cref="LayoutResult.Outcome.Partial"/>. The call comes from the placement loop, not from
/// <see cref="IRenderer.Draw"/>, so the measuring run, which never draws, reports the same
/// positions as the drawing run (#288).
/// </summary>
/// <remarks>
/// Implement it explicitly. The built-in renderers are public classes, so an implicit
/// implementation would be a new public method.
/// </remarks>
internal interface IPlacementSource
{
    /// <summary>
    /// Reports only what lies in the placed part: a split heading reports from the piece that holds
    /// its first line and the overflow piece reports nothing.
    /// </summary>
    void ReportPlacements(PlacementSink sink);
}

/// <summary>
/// One reported placement. A struct with named fields so a later target kind (an anchor, an index
/// mark, a structure element's page) adds a field rather than changing every report site.
/// </summary>
/// <param name="Key">What was placed, compared by reference.</param>
/// <param name="PageIndex">0-based, relative to the first page of this layout.</param>
/// <param name="Y">The layout-space y of the top of <paramref name="Box"/>, which for a heading
/// is its first placed line.</param>
/// <param name="Renderer">The renderer whose result was accepted.</param>
/// <param name="Box">The area the placed part occupies.</param>
internal readonly record struct Placement(object Key, int PageIndex, double Y, IRenderer Renderer, LayoutBox Box);

/// <summary>
/// Collects the placements of one pass. The loop says which renderer is reporting and on which page
/// with <see cref="Enter"/>; the renderer supplies only the key and the box.
/// </summary>
internal sealed class PlacementSink
{
    // Created on the first report: a document without a reporting renderer allocates no list.
    private List<Placement>? _placements;
    private IRenderer? _renderer;
    private int _pageIndex;

    internal int Count => _placements?.Count ?? 0;

    internal Placement this[int index] => _placements![index];

    internal IReadOnlyList<Placement> Placements => _placements ?? (IReadOnlyList<Placement>)[];

    internal void Enter(IRenderer renderer, int pageIndex)
    {
        _renderer = renderer;
        _pageIndex = pageIndex;
    }

    /// <summary>Reports that <paramref name="key"/> was placed in <paramref name="box"/>.</summary>
    internal void Report(object key, LayoutBox box)
    {
        _placements ??= [];
        _placements.Add(new Placement(key, _pageIndex, box.Y, _renderer!, box));
    }
}
