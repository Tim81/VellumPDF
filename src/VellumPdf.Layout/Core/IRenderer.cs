// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// The core two-phase rendering contract.
/// Layout determines sizing and splitting; Draw emits PDF operators.
/// </summary>
/// <remarks>
/// Implement this to add a custom element through
/// <see cref="VellumPdf.Layout.Document.Add(IRenderer)"/>. The document calls <see cref="Layout"/>
/// and <see cref="Draw"/> during a save, so an exception from either one, or from the limits
/// described on <see cref="Layout"/>, reaches the caller from the save.
/// <see cref="VellumPdf.Layout.Rendering.DocumentRenderer.Render"/> runs the same layout, so the
/// exceptions these members name as raised from the save reach you from it too.
/// </remarks>
public interface IRenderer
{
    /// <summary>
    /// Phase 1: determine how much of this element fits in <paramref name="context"/>. Must not
    /// mutate any state visible to the caller, other than what measuring text adds to an embedded
    /// font's subset.
    /// </summary>
    /// <remarks>
    /// The document may call this more than once on the same instance. A header or footer adds a
    /// page-counting pass that lays every element out before the drawing pass does, and a
    /// <see cref="LayoutResult.Outcome.Nothing"/> is followed by a retry. Return the same result
    /// for the same area. A null return is not checked, and the save throws.
    /// <para>One element may take at most <b>50,000</b> page continuations. Each
    /// <see cref="LayoutResult.Outcome.Partial"/>, and each retry after
    /// <see cref="LayoutResult.Outcome.Nothing"/>, counts as one. An overflow that never gets
    /// smaller reaches the limit; the save then throws. Make each overflow hold less than the
    /// renderer that returned it.</para>
    /// <para><b>A renderer that fits a smaller area but not a larger one is not supported.</b> At
    /// the top of a page the document offers the larger of the content area's height and the
    /// height left below the cursor, so on some page geometries such a renderer is refused with
    /// <see cref="InvalidOperationException"/> from the save. The two heights can differ by one
    /// rounding step. <b>Do not pass</b> a renderer whose result gets
    /// worse as the area grows, such as one that returns
    /// <see cref="LayoutResult.Outcome.Nothing"/> for an area and fits a shorter one.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other
    /// save overloads, not from this method, when one element needs more than 50,000 page
    /// continuations, or when it returns <see cref="LayoutResult.Outcome.Nothing"/> twice in a row,
    /// the second time on a new page.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from the save, not from this method, when it returns <see langword="null"/>.
    /// </exception>
    LayoutResult Layout(LayoutContext context);

    /// <summary>
    /// Phase 2: emit PDF operators into <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// The document calls this on a renderer that returned <see cref="LayoutResult.Outcome.Full"/>,
    /// and on the <see cref="LayoutResult.SplitRenderer"/> of a
    /// <see cref="LayoutResult.Outcome.Partial"/> result. A split renderer is drawn without its own
    /// <see cref="Layout"/> ever being called, so it must carry everything it needs to draw. Apart
    /// from such a split renderer, calling <see cref="Draw"/> on a renderer whose
    /// <see cref="Layout"/> has not run is not supported. Called before <see cref="Layout"/>, the
    /// built-in renderers draw nothing, draw a zero-size or off-page mark, or throw; see
    /// <see cref="VellumPdf.Layout.Rendering.Table.TableRenderer.Draw"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A coordinate or extent the renderer writes is <c>NaN</c> or infinite, which
    /// <see cref="VellumPdf.Canvas.PdfCanvas"/> refuses (<c>ParamName</c> is null). <see cref="Layout"/>
    /// does not check every box it hands to <c>Draw</c>, so such a value can reach it.
    /// </exception>
    void Draw(DrawContext context);
}
