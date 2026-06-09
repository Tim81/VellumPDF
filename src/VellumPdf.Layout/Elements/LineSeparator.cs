// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>A horizontal rule drawn as a full-width line.</summary>
public sealed class LineSeparator
{
    /// <summary>Stroke width of the rule in points.</summary>
    public double LineWidth { get; init; } = 1;

    /// <summary>Stroke color of the rule.</summary>
    public ColorRgb Color { get; init; } = ColorRgb.Black;

    /// <summary>Margins around the rule.</summary>
    public EdgeInsets Margins { get; init; } = new EdgeInsets(6, 0, 6, 0);
}
