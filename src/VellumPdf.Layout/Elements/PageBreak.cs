// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A page break in the flow: whatever is added after it starts on a new page, or, with
/// <see cref="Recto"/>, on the next right-hand page.
/// </summary>
/// <remarks>
/// <b>A null break is refused.</b> <see cref="Document.Add(PageBreak)"/> throws
/// <see cref="ArgumentNullException"/> from that call. Nothing else about a break can be refused.
/// <para>A break takes effect before the next element, so it adds nothing when it is the first
/// element, when it follows another break, or when the page it ends has nothing on it. A break
/// with nothing after it is dropped, which means no trailing blank page. A document that holds
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
/// <para>Do not rely on a form feed (U+000C) in text to break the page: it is drawn as a space and
/// that does not change in 2.x. See <see cref="Core.PageBreakMode"/>.</para>
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
}
