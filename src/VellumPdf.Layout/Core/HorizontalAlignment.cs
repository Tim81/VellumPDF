// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>Horizontal alignment of text or content within its available width.</summary>
/// <remarks>
/// <see cref="Left"/>, <see cref="Center"/> and <see cref="Right"/> are honoured by every
/// consumer. <see cref="Justify"/> is not; see that member.
/// </remarks>
public enum HorizontalAlignment
{
    /// <summary>Align content to the left edge.</summary>
    /// <remarks>Honoured by every alignment consumer in this package.</remarks>
    Left,

    /// <summary>Centre content horizontally.</summary>
    /// <remarks>Honoured by every alignment consumer in this package.</remarks>
    Center,

    /// <summary>Align content to the right edge.</summary>
    /// <remarks>Honoured by every alignment consumer in this package.</remarks>
    Right,

    /// <summary>Stretch content to fill the width, flush with both the left and right edges.</summary>
    /// <remarks>
    /// Paragraph and heading text honour this. A consumer that lays out a single unbreakable
    /// box treats it as <see cref="Left"/>: image, cell, pie chart, running band. Nothing
    /// reports the fall-through.
    /// </remarks>
    Justify,
}
