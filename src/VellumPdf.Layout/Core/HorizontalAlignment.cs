// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>Horizontal alignment of text or content within its available width.</summary>
public enum HorizontalAlignment
{
    /// <summary>Align content to the left edge.</summary>
    Left,

    /// <summary>Centre content horizontally.</summary>
    Center,

    /// <summary>Align content to the right edge.</summary>
    Right,

    /// <summary>Stretch content to fill the width, flush with both the left and right edges.</summary>
    Justify,
}
