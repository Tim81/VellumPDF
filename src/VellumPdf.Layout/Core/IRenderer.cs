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
    /// <para><b>A built-in renderer you call from yours lays out as it did before a fix that
    /// would change the continuation limit's verdict.</b> A
    /// <see cref="VellumPdf.Layout.Rendering.Table.TableRenderer"/> reads the breaks of its
    /// cells only when the document called it, so one that your <see cref="Layout"/> forwards to
    /// reads every cell unbroken (#679); see
    /// <see cref="VellumPdf.Layout.Elements.Table.Cell.Content"/>. A
    /// <see cref="VellumPdf.Layout.Rendering.ParagraphRenderer"/> that your renderer lays out wraps
    /// its text as 2.3.3 did: a word that continues over a run boundary is two words, U+202F and
    /// U+2007 are spaces, and a word wider than the line is cut at the glyph, never after a
    /// solidus, reverse solidus or hyphen-minus (#562, #590, #594). Your <see cref="Draw"/> must
    /// forward to the same renderer.</para>
    /// <para><b>A <see cref="VellumPdf.Layout.Rendering.ListRenderer"/> that your renderer lays out
    /// is laid out as it was before the fixes of #476 and #685.</b> Through the context you were
    /// given, through <see cref="LayoutContext.WithArea"/> or through the public
    /// <see cref="LayoutContext"/> constructor, it narrows no entry's gutter and moves no entry to
    /// the next page, so the list saves, or is refused for the continuation limit, as it did, over
    /// the entries <see cref="VellumPdf.Layout.Rendering.ListRenderer.Layout"/> says it reads. The
    /// document applies those fixes only to a list it lays out itself, where it can see the limit.
    /// <b>Do not</b> rely on where such a list lands: 3.0 lays a list out one way, whatever called
    /// it.</para>
    /// <para><b>A document that holds a renderer of yours lays every list out as it was before the
    /// fixes above,</b> whether the list sits before it or after it. The document decides this
    /// once, before it lays anything out, so a header or footer's page-counting pass and the pass
    /// that draws agree, and those fixes do not change where your renderer starts. See
    /// <see cref="VellumPdf.Layout.Rendering.ListRenderer.Layout"/> for which renderers count and
    /// what is reported. <b>Do not</b> write a renderer whose result depends on anything but the
    /// area it is offered.</para>
    /// <para><b>In a document that holds a page reference, a renderer that answers differently the
    /// later time is refused.</b> Such a document resolves the labels against measuring runs and
    /// then compares each placement the drawing pass reports with the last run's: the page it is
    /// on, the key it is reported under and the count. A renderer that returns another result in
    /// the drawing pass than in the measuring runs makes the save throw
    /// <see cref="InvalidOperationException"/> naming a change of pagination, and the document
    /// cannot be saved again. The placements compared are those of headings, anchored elements and
    /// applied page breaks, so a difference elsewhere is not seen. <b>Do not pass</b> a renderer
    /// whose result depends on how often it was called; 3.0 lays the passes out alike.</para>
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
