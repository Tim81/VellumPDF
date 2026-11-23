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
    /// <para>Keep requests, such as <see cref="VellumPdf.Layout.Elements.Paragraph.KeepWithNext"/>,
    /// add calls of the same kind. To decide where a group of elements goes, the document lays out
    /// the group and the first element after it once more before it places any of them, and again
    /// from the next page when the group does not hold where it stands below the top of a page. At
    /// the top of a page there is no later page to try, so that second pass is skipped.
    /// Your renderer can therefore be laid out ahead of the elements before it, and with areas on
    /// pages it is not placed on in the end. The document draws a <see cref="LayoutResult.Full"/>
    /// result after the last call on that instance, so a renderer that draws from what its last
    /// call left is not affected.</para>
    /// <para>One element may take at most <b>50,000</b> page continuations. Each
    /// <see cref="LayoutResult.Outcome.Partial"/>, and each retry after
    /// <see cref="LayoutResult.Outcome.Nothing"/>, counts as one. An overflow that never gets
    /// smaller reaches the limit; the save then throws. Make each overflow hold less than the
    /// renderer that returned it.</para>
    /// <para><b>A renderer that fits a smaller area but not a larger one is not supported.</b> At
    /// the top of a page the document offers the larger of the content area's height and the
    /// height from the top of the content area to its bottom, which can differ by one rounding
    /// step, so such a renderer's result can depend on which of the two it is offered.
    /// <b>Do not pass</b> a renderer whose result gets worse as the area grows, such as one that
    /// returns <see cref="LayoutResult.Outcome.Nothing"/> for an area and fits a shorter one. One
    /// that returns <c>Nothing</c> for exactly the content area's height and fits the one step
    /// shorter was saved by an earlier build on a geometry where the shorter height was offered
    /// first; the save now throws <see cref="InvalidOperationException"/> for it.</para>
    /// <para><b>A <see cref="VellumPdf.Layout.Rendering.ListRenderer"/> that your renderer lays out
    /// is laid out as it was before the fixes of #476 and #685.</b> Through the context you were
    /// given, through <see cref="LayoutContext.WithArea"/> or through the public
    /// <see cref="LayoutContext"/> constructor, it narrows no entry's gutter and moves no entry to
    /// the next page, so the list saves, or is refused for the continuation limit, as it did. The
    /// document applies those fixes only to a list it lays out itself, where it can see the limit.
    /// <b>Do not</b> rely on where such a list lands: 3.0 lays a list out one way, whatever called
    /// it.</para>
    /// <para><b>A document that holds a list ahead of a renderer of yours lays out once more
    /// before it draws.</b> That extra pass lays every list out as it was before the fixes above,
    /// calls your renderer on the areas it gets, and keeps nothing. It finds out whether your
    /// renderer lays out a <see cref="VellumPdf.Layout.Rendering.ListRenderer"/> whose items use a
    /// font the document cannot draw, in which case every list of the document stays as it was
    /// before the fixes; see <see cref="VellumPdf.Layout.Elements.ListElement"/>. It comes before
    /// a header or footer's page-counting pass and apart from it, so a document with one calls your
    /// renderer one pass more than it otherwise would. The library's own renderers that hold no
    /// list, such as the barcode renderer, do not cause it. <b>An exception the extra pass meets is
    /// not raised from it</b>: the drawing pass raises what it meets itself, so an exception that
    /// only the extra pass met, because your renderer refuses an area only that pass offered, is
    /// lost.</para>
    /// <para><b>A renderer that breaks the rules at the top of these remarks can give other output
    /// than before.</b> One that changes its result from one call to the next, counts its calls,
    /// or throws on its first, sees the extra pass as one more call. A list ahead of your renderer
    /// that moves an entry (#685) also moves where your renderer starts, so a renderer that
    /// refuses some areas can save where it was refused, or be refused where it saved. <b>Do
    /// not</b> write a renderer whose result depends on anything but the area it is offered.</para>
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
