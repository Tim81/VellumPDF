// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A paragraph that also registers a document bookmark (outline entry) at the
/// position where it is drawn. The bookmark title defaults to the heading text.
/// </summary>
public sealed class Heading
{
    /// <summary>Heading text (also used as the bookmark title unless <see cref="BookmarkTitle"/> is set).</summary>
    public string Text { get; }

    /// <summary>The heading's text style.</summary>
    public TextStyle Style { get; }

    /// <summary>Outline nesting level: 0 = top-level, 1 = sub-heading, etc.</summary>
    /// <remarks>
    /// Zero-based: level 0 is tagged <c>H1</c>, level 4 is <c>H5</c>, and every level at or
    /// above 5 is tagged <c>H6</c>. That ceiling is this library's, not the format's: ISO 32000-2
    /// Table 366 defines <c>Hn</c> for any unsigned integer from 1 upward. Its NOTE 2 says
    /// <c>H7</c> can be used for a seventh-level heading. That note is informative, not a
    /// requirement.
    /// <para><b>Attention</b>: a negative level is <b>not</b> refused, and it does not clamp the way
    /// you would expect. The mapping's catch-all sends it to <c>H6</c>, the deepest tag, where
    /// you almost certainly meant the shallowest. The raw value also reaches the outline builder,
    /// so your bookmark tree nests on it. A later major version will reject it.</para>
    /// <para>Every level from 5 upward is the same <c>H6</c> tag. Assistive technology therefore
    /// cannot tell level 5 from level 500, while the outline still nests on the number you gave.
    /// Do not use a large level to express depth.</para>
    /// </remarks>
    public int Level { get; init; }

    /// <summary>
    /// Override the bookmark title. When null the heading text is used.
    /// </summary>
    public string? BookmarkTitle { get; init; }

    /// <summary>Margins around the heading.</summary>
    /// <remarks>
    /// <b>Attention</b>: this inset is <b>not</b> validated. <see cref="LineSeparator.Margins"/>
    /// and <see cref="Table.Cell.Padding"/> are the only insets this package checks; every other
    /// one, including this, reaches the geometry as given.
    /// <para>So a non-finite inset is not refused on your behalf. What happens instead depends on
    /// where the arithmetic lands, not on which member you set, and none of the outcomes is a
    /// refusal naming this property: the value can reach the content stream as a token no reader
    /// can parse, or trip a later geometry check that blames something else. Nothing reports it
    /// either way.</para>
    /// <para>Do <b>not</b> pass a negative inset either, and do not read one as a way to position
    /// or resize. It is arithmetic on the available area rather than a placement instruction, so
    /// what a renderer then does with that area is what you get: some carry the content off the
    /// page, others absorb the value and draw exactly as they would at zero. Nothing is refused
    /// and nothing is reported. A later major version will reject both.</para>
    /// </remarks>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Horizontal alignment of the heading text.</summary>
    /// <remarks>
    /// Headings are laid out as paragraphs, so <see cref="HorizontalAlignment.Justify"/> is
    /// honoured the same way: wrapped lines justify, the last line stays left.
    /// </remarks>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>
    /// Optional per-element language override (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>).
    /// When set and the document is tagged, written as <c>/Lang</c> on the struct element.
    /// </summary>
    /// <remarks>
    /// The string is not validated. Same as <see cref="Document.Language"/>.
    /// </remarks>
    public string? Language { get; init; }

    /// <summary>Creates a heading with the given text and optional style (defaults to 14pt).</summary>
    public Heading(string text, TextStyle? style = null)
    {
        Text = text;
        Style = style ?? new TextStyle { FontSize = 14 };
    }

    internal string ResolvedBookmarkTitle => BookmarkTitle ?? Text;
}
