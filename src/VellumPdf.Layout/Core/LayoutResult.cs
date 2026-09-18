// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// Result of <see cref="IRenderer.Layout"/>. The three outcomes drive pagination:
/// Full: the content fit; Partial: some fit and the rest continues on the next page;
/// Nothing: none of it fit in the area offered.
/// </summary>
/// <remarks>
/// Read <see cref="Status"/> before the nullable members. What the document does with each
/// outcome is described on the members of <see cref="Outcome"/>.
/// </remarks>
public sealed class LayoutResult
{
    /// <summary>The possible outcomes of a layout attempt.</summary>
    /// <remarks>
    /// The constructor is private, so a result can only come from <see cref="LayoutResult.Full"/>,
    /// <see cref="LayoutResult.Partial"/> or <see cref="LayoutResult.Nothing()"/>, and carries one
    /// of these three values.
    /// </remarks>
    public enum Outcome
    {
        /// <summary>The content fit entirely within the available area.</summary>
        /// <remarks>
        /// <see cref="SplitRenderer"/> and <see cref="OverflowRenderer"/> are null. The document
        /// draws the renderer and continues from the bottom of <see cref="OccupiedArea"/>.
        /// </remarks>
        Full,

        /// <summary>Part of the content fit; the remainder overflows to the next page.</summary>
        /// <remarks>
        /// The document draws <see cref="SplitRenderer"/> on the current page, starts a new page,
        /// and lays out <see cref="OverflowRenderer"/> there. <see cref="LayoutResult.Partial"/>
        /// does not check that either renderer is set.
        /// </remarks>
        Partial,

        /// <summary>No content fit in the area offered.</summary>
        /// <remarks>
        /// The occupied area and both renderers are null. The document finishes the current page,
        /// even when nothing is on it, starts a new one, and calls <see cref="IRenderer.Layout"/>
        /// again with the whole content area. A <c>Nothing</c> from the first element on a page
        /// therefore leaves that page empty apart from any header or footer.
        /// <para>The retry only decides whether to throw: a second <c>Nothing</c> makes the save
        /// throw, and any other result is discarded, after which the document lays the renderer out
        /// again from the top of the new page. That area can be one rounding step shorter than the
        /// retry's, so a renderer that needs the whole content area exactly can return
        /// <c>Nothing</c> again and repeat the cycle until it reaches the page-continuation limit
        /// (#549).</para>
        /// </remarks>
        Nothing,
    }

    /// <summary>The outcome of the layout attempt.</summary>
    /// <remarks>
    /// Read this before <see cref="OccupiedArea"/>, <see cref="SplitRenderer"/> or
    /// <see cref="OverflowRenderer"/>. Which of those are null depends on this value.
    /// </remarks>
    public Outcome Status { get; }

    /// <summary>The occupied area after layout (valid for Full and Partial).</summary>
    /// <remarks>
    /// Null on <see cref="Outcome.Nothing"/>. The document reads only the box's
    /// <see cref="LayoutBox.Bottom"/>, and only after <see cref="Outcome.Full"/>: it becomes the
    /// position of the next element. The Bottom is not checked. One above the current position
    /// moves the next element up the page, and a non-finite one becomes the top of the next
    /// element's area. Later elements can then write <c>NaN</c> or <c>Infinity</c> into the content
    /// stream, be left out of the file without an exception, or make the save throw. After
    /// <see cref="Outcome.Partial"/> the box is not read.
    /// </remarks>
    public LayoutBox? OccupiedArea { get; }

    /// <summary>Renderer representing the part that fit (Partial only).</summary>
    /// <remarks>
    /// Null on <see cref="Outcome.Full"/> and <see cref="Outcome.Nothing"/>. The document draws
    /// it without calling its <see cref="IRenderer.Layout"/> first.
    /// </remarks>
    public IRenderer? SplitRenderer { get; }

    /// <summary>Renderer representing overflow to be placed on the next page (Partial only).</summary>
    /// <remarks>
    /// Null on <see cref="Outcome.Full"/> and <see cref="Outcome.Nothing"/>. The document lays it
    /// out on the next page, and counts every such step against the limit described on
    /// <see cref="IRenderer.Layout"/>.
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
    /// <remarks>
    /// Any box is accepted. <see cref="OccupiedArea"/> says which part of it the document reads.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other
    /// save overloads, not from this call, when a non-finite <see cref="LayoutBox.Bottom"/> leaves
    /// a later element at a position the save writes outside the content stream, such as the
    /// rectangle of a link a later renderer places in the area it was given, or, after negative
    /// infinity, a heading's bookmark. The message says PDF does not support NaN or Infinity as a
    /// real number.
    /// </exception>
    public static LayoutResult Full(LayoutBox occupied) =>
        new(Outcome.Full, occupied, null, null);

    /// <summary>Creates a result indicating the content was split, with the part that fit and the overflow to place on the next page.</summary>
    /// <remarks>
    /// No argument is checked here, and the document does not read <paramref name="occupied"/>. A
    /// null <paramref name="split"/> or <paramref name="overflow"/> is stored, and the save throws
    /// <see cref="NullReferenceException"/>.
    /// <para>Do not pass null for either renderer. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other
    /// save overloads, not from this call, when <paramref name="split"/> or
    /// <paramref name="overflow"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this call, when the element needs more than 50,000 page
    /// continuations, such as an <paramref name="overflow"/> that never gets smaller; see
    /// <see cref="IRenderer.Layout"/>.
    /// </exception>
    public static LayoutResult Partial(LayoutBox occupied, IRenderer split, IRenderer overflow) =>
        new(Outcome.Partial, occupied, split, overflow);

    /// <summary>Creates a result indicating no content fit in the available area.</summary>
    /// <remarks>
    /// <see cref="OccupiedArea"/>, <see cref="SplitRenderer"/> and
    /// <see cref="OverflowRenderer"/> are all null. The document retries on a new page. If the
    /// renderer returns this again there, the save throws.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other
    /// save overloads, not from this call, when the retry on a new page also returns
    /// <c>Nothing</c>. The message says the element is too tall to fit on a single page. Through
    /// the cycle described on <see cref="Outcome.Nothing"/>, a renderer can instead reach the
    /// page-continuation limit, which raises the same type with a different message (#549).
    /// </exception>
    public static LayoutResult Nothing() =>
        new(Outcome.Nothing, null, null, null);
}
