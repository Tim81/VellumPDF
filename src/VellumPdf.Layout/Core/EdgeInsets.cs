// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>CSS-style four-sided inset (top, right, bottom, left) in points.</summary>
/// <param name="Top">The inset on the top edge.</param>
/// <param name="Right">The inset on the right edge.</param>
/// <param name="Bottom">The inset on the bottom edge.</param>
/// <param name="Left">The inset on the left edge.</param>
public readonly record struct EdgeInsets(double Top, double Right, double Bottom, double Left)
{
    /// <summary>Creates an inset with the same value on all four edges.</summary>
    public EdgeInsets(double all) : this(all, all, all, all) { }

    /// <summary>Creates an inset with one value for the top and bottom edges and another for the left and right edges.</summary>
    public EdgeInsets(double topBottom, double leftRight) : this(topBottom, leftRight, topBottom, leftRight) { }

    /// <summary>An inset of zero on all four edges.</summary>
    public static readonly EdgeInsets Zero = new(0);

    /// <summary>The total horizontal inset (Left + Right).</summary>
    public double Horizontal => Left + Right;

    /// <summary>The total vertical inset (Top + Bottom).</summary>
    public double Vertical => Top + Bottom;
}
