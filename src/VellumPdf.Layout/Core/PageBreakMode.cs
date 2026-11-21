// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// Where an element starts: on the page the previous element left off, on a new page, or on the
/// next right-hand page.
/// </summary>
/// <remarks>
/// <b>An undefined value is refused.</b> Setting a <c>BreakBefore</c> property to a number this
/// enumeration does not name throws <see cref="ArgumentOutOfRangeException"/> from that
/// property's <c>init</c> accessor, so the error names the line that wrote the bad value.
/// <para>Only an element the document itself holds can break: a <see cref="Elements.Paragraph"/>,
/// <see cref="Elements.Heading"/>, <see cref="Elements.Table.TableElement"/>,
/// <see cref="Elements.ListElement"/> or <see cref="Elements.LayoutImage"/> added to a
/// <see cref="Document"/>, or the renderer of one of them added with
/// <see cref="Document.Add(IRenderer)"/>. A renderer you wrote yourself cannot ask for a break, and
/// neither can anything nested inside a list or a table, an overflow piece, or the paragraph a
/// heading draws its text with. Add a <see cref="Elements.PageBreak"/> before such a
/// renderer.</para>
/// <para>The page a mode counts from is the physical position in the kernel document, with page 1
/// on the right. A page label does not change it: a document whose front matter is numbered
/// i, ii, iii still starts a <see cref="Recto"/> chapter on an odd physical page.</para>
/// <para>Do not rely on a form feed (U+000C) inside text to break the page. The layout draws it
/// as a space, as it does most white-space characters other than a line feed, and 2.x does not
/// change that.
/// Use <see cref="Page"/> or a <see cref="Elements.PageBreak"/>.</para>
/// </remarks>
public enum PageBreakMode
{
    /// <summary>The element starts where the previous element ended.</summary>
    None = 0,

    /// <summary>
    /// The element starts at the top of a page. When the current page has nothing on it yet, as at
    /// the start of the document or straight after another break, no page is added.
    /// </summary>
    Page = 1,

    /// <summary>
    /// The element starts at the top of a right-hand (odd-numbered) page. When that page would be
    /// an even one, a blank page is added first. The blank page carries no header or footer and no
    /// content, and it counts in <c>{page}</c>, <c>{pages}</c> and the page tree.
    /// </summary>
    Recto = 2,
}

internal static class PageBreakModes
{
    /// <summary>
    /// Returns <paramref name="value"/> when the enumeration names it. Called from each
    /// <c>BreakBefore</c> accessor so the refusal is raised at the call that wrote the value.
    /// </summary>
    internal static PageBreakMode Validate(PageBreakMode value) =>
        value is PageBreakMode.None or PageBreakMode.Page or PageBreakMode.Recto
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(value), (int)value, "The page break mode is not None, Page or Recto.");
}
