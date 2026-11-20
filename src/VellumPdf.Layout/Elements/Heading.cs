// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A paragraph that also registers a document bookmark (outline entry) at the
/// position where it is drawn. The bookmark title defaults to the heading text.
/// </summary>
/// <remarks>
/// Laid out by the same code as <see cref="Paragraph"/>, so wrapping and
/// <see cref="HorizontalAlignment.Justify"/> behave the same. Every heading also adds a bookmark
/// and, in a tagged document, an <c>Hn</c> structure element chosen by <see cref="Level"/>.
/// </remarks>
public sealed class Heading
{
    /// <summary>Heading text (also used as the bookmark title unless <see cref="BookmarkTitle"/> is set).</summary>
    /// <remarks>
    /// Never null; the constructor refuses a null. White space is drawn as on
    /// <see cref="Paragraph"/>. An empty string draws no text, but the heading still adds a
    /// bookmark, titled by <see cref="BookmarkTitle"/> or else empty, and, in a tagged document,
    /// its structure element.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public string Text { get; }

    /// <summary>The heading's text style.</summary>
    /// <remarks>
    /// A null style passed to the constructor becomes 14pt Helvetica; any other is stored as given.
    /// Refusals on size, leading, font and text colour are on <see cref="TextStyle"/> and are
    /// raised from the save.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused, or a channel of its text
    /// colour is not finite; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/>
    /// and <see cref="TextStyle.Color"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the heading's text holds an unpaired surrogate and is measured in
    /// an embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public TextStyle Style { get; }

    /// <summary>Outline nesting level: 0 = top-level, 1 = sub-heading, etc.</summary>
    /// <remarks>
    /// Zero-based: level 0 is tagged <c>H1</c>, level 4 is <c>H5</c>, and every level at or
    /// above 5 is tagged <c>H6</c>. That ceiling is this library's, not the format's: ISO 32000-2
    /// Table 366 defines <c>Hn</c> for any unsigned integer from 1 upward. Its NOTE 2 says
    /// <c>H7</c> can be used for a seventh-level heading. That note is informative, not a
    /// requirement.
    /// <para><b>Attention</b>: a negative level is <b>not</b> refused, and it does not clamp the
    /// way you would expect. The mapping's catch-all sends it to <c>H6</c>, the deepest tag, where
    /// you almost certainly meant the shallowest. A later major version will reject it.</para>
    /// <para>The raw value also reaches the outline builder, which hangs this heading's bookmark
    /// under the most recent earlier heading whose level is exactly one less, and puts it at the
    /// top level when there is none; see <see cref="DrawContext.AddOutlineEntry(string, int, double)"/>. So a level
    /// expresses depth only where the heading above it is one shallower: after a level 0 heading,
    /// levels 5 and 500 both land at the top level, beside it rather than under it. Every level
    /// from 5 upward is the same <c>H6</c> tag as well, so assistive technology cannot tell them
    /// apart either. Do not use a large level to express depth.</para>
    /// </remarks>
    public int Level { get; init; }

    /// <summary>
    /// Override the bookmark title. When null the heading text is used.
    /// </summary>
    /// <remarks>Null means use <see cref="Text"/>. Empty is an empty bookmark title.</remarks>
    public string? BookmarkTitle { get; init; }

    /// <summary>Whether the heading's bookmark is shown open (the default) or closed in the outline.</summary>
    /// <remarks>
    /// <b>No value is refused.</b> The value takes effect only when the bookmark gets children: a
    /// later bookmark at exactly <see cref="Level"/> + 1 whose most recent earlier bookmark at
    /// <see cref="Level"/> is this one, whether from a heading or from a custom renderer's
    /// <see cref="DrawContext.AddOutlineEntry(string, int, double, bool)"/>. Level 0 is always
    /// top-level and never has a parent, so a bookmark at level -1 never gets children: the
    /// bookmarks after it at level 0 sit beside it. Without children the bookmark has no
    /// <c>/Count</c>, because ISO 32000-2:2020, Table 151, requires it only for an item with
    /// descendants, and the value is not written. A closed bookmark writes a negative
    /// <c>/Count</c>.
    /// <para>A heading that splits across pages has one bookmark, and it carries this value.</para>
    /// </remarks>
    public bool BookmarkExpanded { get; init; } = true;

    /// <summary>Margins around the heading.</summary>
    /// <inheritdoc cref="Paragraph.Margins" path="/remarks"/>
    /// <inheritdoc cref="Paragraph.Margins" path="/exception"/>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Horizontal alignment of the heading text.</summary>
    /// <remarks>
    /// Same as <see cref="Paragraph.Alignment"/>.
    /// </remarks>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>
    /// Optional per-element language override (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>).
    /// When set and the document is tagged, written as <c>/Lang</c> on the struct element.
    /// </summary>
    /// <remarks>
    /// The string is not validated: it is trimmed and written, so an ill-formed tag reaches the
    /// file. An empty or whitespace-only string is not written, and nothing is written when the
    /// document is not tagged.
    /// <para>Do not pass a tag that is not well-formed BCP 47. A later major version will refuse
    /// one.</para>
    /// </remarks>
    public string? Language { get; init; }

    /// <summary>Creates a heading with the given text and optional style (defaults to 14pt).</summary>
    /// <remarks>
    /// A null <paramref name="style"/> becomes 14pt Helvetica. A null <paramref name="text"/> is
    /// refused here. An empty string is not
    /// null and is accepted.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <inheritdoc cref="Paragraph.Paragraph(string, TextStyle)" path="/exception[@cref='T:System.ArgumentException' or @cref='T:System.IndexOutOfRangeException' or @cref='T:System.InvalidOperationException']"/>
    public Heading(string text, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Style = style ?? new TextStyle { FontSize = 14 };
    }

    internal string ResolvedBookmarkTitle => BookmarkTitle ?? Text;
}
