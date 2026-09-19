// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>Horizontal alignment of text or content within its available width.</summary>
/// <remarks>
/// Every consumer positions <see cref="Center"/> and <see cref="Right"/> and starts any other
/// value at the left edge. Only paragraph and heading text also stretch <see cref="Justify"/>
/// lines. A value this enumeration does not name is drawn as <see cref="Left"/>.
/// </remarks>
public enum HorizontalAlignment
{
    /// <summary>Align content to the left edge.</summary>
    /// <remarks>Also what a consumer uses for a value it does not handle.</remarks>
    Left,

    /// <summary>Centre content horizontally.</summary>
    /// <remarks>
    /// In paragraph, heading and table-cell text, a line wider than its area starts at the left
    /// edge instead of overhanging both sides.
    /// </remarks>
    Center,

    /// <summary>Align content to the right edge.</summary>
    /// <remarks>
    /// In paragraph, heading and table-cell text, a line wider than its area starts at the left
    /// edge instead of overhanging it.
    /// </remarks>
    Right,

    /// <summary>
    /// Stretch the spaces in paragraph and heading lines toward both edges.
    /// </summary>
    /// <remarks>
    /// Only paragraph and heading text are justified. They stretch every line except the
    /// paragraph's last, and a line that ends at a hard line break is stretched too. A single
    /// trailing line break is dropped, so the line before it is the last. A break right after a
    /// word too wide for its line is not dropped; see
    /// <see cref="VellumPdf.Layout.Elements.Paragraph"/>. A second trailing break adds an empty
    /// last line, and the line before the empty one is stretched. A line with no space in it is not
    /// stretched. White space is drawn as described on
    /// <see cref="VellumPdf.Layout.Elements.Paragraph"/>. An image, a table cell, a pie chart, a
    /// running band and a barcode draw this value as <see cref="Left"/>.
    /// <para><b>Attention</b>: on a line whose text is all in the standard-14 text faces, which are
    /// every standard-14 font but Symbol and ZapfDingbats, and in one
    /// <see cref="VellumPdf.Layout.Core.TextStyle"/> instance, the spaces are stretched by only
    /// half the space left on the line, so the line stops short of the right edge (#548). On a line
    /// in those faces holding text in more than one instance, each instance's text is placed at its
    /// unstretched width while its spaces are stretched. That text can then overprint the text
    /// after it, even when the two styles hold the same values.
    /// <see cref="VellumPdf.Fonts.Standard14.Symbol"/> and
    /// <see cref="VellumPdf.Fonts.Standard14.ZapfDingbats"/> measure every glyph as zero (#470), so
    /// a justified line in either is stretched by half the whole line width and can run past the
    /// right edge.</para>
    /// </remarks>
    Justify,
}
