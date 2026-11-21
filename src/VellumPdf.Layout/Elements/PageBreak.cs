// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A page break in the flow: whatever is added after it starts on a new page, or, with
/// <see cref="Recto"/>, on the next right-hand page.
/// </summary>
/// <remarks>
/// <b>A null break is refused.</b> <see cref="Document.Add(PageBreak)"/> throws
/// <see cref="ArgumentNullException"/> from that call. The only other refusal is of a break that
/// holds a <see cref="PageLabel"/> and has no element after it, which the save raises.
/// <para>A break takes effect before the next element, so it adds nothing when it is the first
/// element, when it follows another break, or when nothing is laid out on the page it ends; an
/// element of zero height still counts as laid out. A break with nothing after it is dropped,
/// which means no trailing blank page, unless it holds a <see cref="PageLabel"/>, which the save
/// refuses instead. A document that holds
/// only breaks has no content, so a save throws <see cref="InvalidOperationException"/> saying
/// the document has no pages, and the document can still be saved once an element is added.</para>
/// <para>Where two breaks meet, the stronger one wins: a <see cref="Recto"/> break next to a plain
/// one gives the same pages as the <see cref="Recto"/> break alone, in either order. A break
/// followed by an element whose <c>BreakBefore</c> is also set is one break, not two.</para>
/// <para>A break occupies a position in the order the document's elements were added, so a
/// <see cref="Document.Diagnostics"/> entry's <c>ElementIndex</c> counts it. It never reports
/// anything itself.</para>
/// <para>A blank page that a <see cref="Recto"/> break inserts has no header or footer. Its page
/// number is counted by <c>{page}</c> on the pages after it and by <c>{pages}</c>. Page 1 is a
/// right-hand page and parity is the physical position in the document, not a page label.</para>
/// <para>Do not rely on a form feed (U+000C) in text to break the page. No built-in element starts
/// a page for it, and 2.x does not change that; see <see cref="Core.PageBreakMode"/> for what each
/// element does with the character.</para>
/// </remarks>
public sealed class PageBreak
{
    /// <summary>Creates a break that starts the next element on a new page.</summary>
    public PageBreak() { }

    /// <summary>
    /// When <see langword="true"/>, the next element starts on a right-hand (odd-numbered) page,
    /// and a blank page is added when the next page would be an even one.
    /// </summary>
    /// <remarks>
    /// Any value is accepted; nothing is refused. The default, <see langword="false"/>, starts the
    /// next element on a new page whatever its number. See
    /// <see cref="Core.PageBreakMode.Recto"/> for the blank page.
    /// </remarks>
    public bool Recto { get; init; }

    /// <summary>
    /// Starts a page-label range at the page the next element is placed on, or
    /// <see langword="null"/> (the default) to start none.
    /// </summary>
    /// <remarks>
    /// <b>A labelled break that no element follows is refused by the save.</b>
    /// <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads throw
    /// <see cref="InvalidOperationException"/> before any layout runs, because a break with nothing
    /// after it is dropped and the page its range would start on is never created. The document can
    /// be saved once an element is added after the break. Nothing else about the value is checked
    /// here: <see cref="PageLabelRange"/> refuses its own bad input.
    /// <para>The range starts at the page the following element starts on: the page the break
    /// opened, the next right-hand page when <see cref="Recto"/> is set (the blank page a recto
    /// break inserts keeps the label of the range before it), or the current page when the break
    /// added no page. When two breaks put their ranges on the same page, the later break's range
    /// is the one that applies, and <see cref="Document.PageLabel"/> counts as the earliest. A
    /// running band's <c>{page}</c> prints the label once any range exists; see
    /// <see cref="RunningBand"/>.</para>
    /// <para>A custom <see cref="Core.IRenderer"/> added with
    /// <see cref="Document.Add(Core.IRenderer)"/> cannot start a range itself. Put a labelled
    /// break before it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this property, when no element follows the break.
    /// </exception>
    public PageLabelRange? PageLabel { get; init; }
}
