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
/// <para>The mode is read when the document places a top-level element: a
/// <see cref="Elements.Paragraph"/>, <see cref="Elements.Heading"/>,
/// <see cref="Elements.Table.TableElement"/>, <see cref="Elements.ListElement"/>,
/// <see cref="Elements.Preformatted"/>, <see cref="Elements.NoteBlock"/> or
/// <see cref="Elements.LayoutImage"/> added with its own
/// <c>Document.Add</c> overload, or the public renderer of a paragraph, table, list or image added
/// with
/// <see cref="Document.Add(IRenderer)"/> or
/// <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>. A heading's renderer is internal, so a
/// heading has no other route.</para>
/// <para>The mode is ignored in three places: an element whose renderer a custom
/// <see cref="IRenderer"/> wraps, the overflow pieces a split element returns, and the paragraph a
/// heading draws its text with. A renderer you wrote yourself cannot ask for a break; add a
/// <see cref="Elements.PageBreak"/> before it. A renderer built to continue an element, such as
/// <c>new ParagraphRenderer(paragraph, startLine: 2)</c>, reads the same element and breaks
/// like the first piece when you add it directly. Do not add one expecting it not to break.</para>
/// <para>The page a mode counts from is the physical position in the kernel document, with page 1
/// on the right. A page label does not change it: a document whose front matter is numbered
/// i, ii, iii still starts a <see cref="Recto"/> chapter on an odd physical page.</para>
/// <para>Do not rely on a form feed (U+000C) in text to break the page. It does not start a page in
/// any built-in element, and 2.x does not change that. A paragraph, a heading and a list item draw
/// it as a space, like most white-space characters other than a line feed. A preformatted element
/// refuses it. A table cell and a
/// running band pass it to the font unchanged, so the content stream holds the raw 0x0C character.
/// Use <see cref="Page"/> or a <see cref="Elements.PageBreak"/>.</para>
/// </remarks>
public enum PageBreakMode
{
    /// <summary>The element starts where the previous element ended.</summary>
    None = 0,

    /// <summary>
    /// The element starts at the top of a page. When nothing is laid out on the current page yet,
    /// as at the start of the document or straight after another break, no page is added to end
    /// it. An element of zero height still counts as laid out.
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
