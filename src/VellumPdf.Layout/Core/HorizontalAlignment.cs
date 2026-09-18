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

    /// <summary>Stretch content to fill the width, flush with both the left and right edges.</summary>
    /// <remarks>
    /// Only paragraph and heading text are justified. They stretch every line except the
    /// paragraph's last, and a line that ends at a hard line break is stretched too. An image, a
    /// table cell, a pie chart, a running band and a barcode draw this value as
    /// <see cref="Left"/>.
    /// </remarks>
    Justify,
}
