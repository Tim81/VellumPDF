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
    /// edge instead of overhanging both sides. See <see cref="TextStyle.FontRef"/> for fonts that
    /// measure every character as zero.
    /// </remarks>
    Center,

    /// <summary>Align content to the right edge.</summary>
    /// <remarks>
    /// In paragraph, heading and table-cell text, a line wider than its area starts at the left
    /// edge instead of overhanging it. See <see cref="TextStyle.FontRef"/> for fonts that measure
    /// every character as zero.
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
    /// <para>In the standard-14 text faces (every standard-14 font but Symbol and ZapfDingbats) the
    /// space left on the line is divided evenly over the line's space characters, and a line ends at
    /// the right edge of its box, including a line that holds text in more than one
    /// <see cref="VellumPdf.Layout.Core.TextStyle"/> instance, where each instance starts at the
    /// width the one before it is drawn to (#548, #555). Before that, the divisor counted every
    /// space twice, so the line stopped short of the right edge, and each instance was placed at its
    /// unstretched width, so it could overprint the text after it. Only the space character
    /// (U+0020) is stretched; a no-break space and other white space are not.</para>
    /// <para><b>Attention</b>: <see cref="VellumPdf.Fonts.Standard14.Symbol"/> and
    /// <see cref="VellumPdf.Fonts.Standard14.ZapfDingbats"/> measure every glyph as zero (#470), so
    /// a justified line in either is stretched as though it held no text, and runs past the right
    /// edge by the width of the text it does hold.</para>
    /// </remarks>
    Justify,
}
