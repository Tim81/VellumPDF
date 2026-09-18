// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>CSS-style four-sided inset (top, right, bottom, left) in points.</summary>
/// <remarks>
/// This type checks nothing. No property refuses a finite negative edge. Whether a non-finite edge
/// is refused depends on the property the inset is assigned to, and each such property documents
/// its own rule. <see cref="VellumPdf.Layout.Elements.LineSeparator.Margins"/> and
/// <see cref="VellumPdf.Layout.Elements.Table.Cell.Padding"/> refuse a non-finite edge at save. The
/// other element <c>Margins</c> properties check neither, and say what happens instead. Do not
/// treat construction as validation.
/// </remarks>
public readonly record struct EdgeInsets
{
    /// <summary>The inset on the top edge.</summary>
    /// <remarks>Any value is stored.</remarks>
    public double Top { get; init; }

    /// <summary>The inset on the right edge.</summary>
    /// <remarks>Any value is stored.</remarks>
    public double Right { get; init; }

    /// <summary>The inset on the bottom edge.</summary>
    /// <remarks>Any value is stored.</remarks>
    public double Bottom { get; init; }

    /// <summary>The inset on the left edge.</summary>
    /// <remarks>Any value is stored.</remarks>
    public double Left { get; init; }

    /// <summary>Creates an inset from the four edges.</summary>
    /// <remarks>Nothing is refused. The four values are stored as passed.</remarks>
    public EdgeInsets(double Top, double Right, double Bottom, double Left)
    {
        this.Top = Top;
        this.Right = Right;
        this.Bottom = Bottom;
        this.Left = Left;
    }

    /// <summary>Creates an inset with the same value on all four edges.</summary>
    /// <remarks>Nothing is refused. The value is stored on all four edges.</remarks>
    public EdgeInsets(double all) : this(all, all, all, all) { }

    /// <summary>Creates an inset with one value for the top and bottom edges and another for the left and right edges.</summary>
    /// <remarks>Nothing is refused. Both values are stored as passed.</remarks>
    public EdgeInsets(double topBottom, double leftRight) : this(topBottom, leftRight, topBottom, leftRight) { }

    /// <summary>An inset of zero on all four edges.</summary>
    /// <remarks>Equal to <c>default(EdgeInsets)</c>.</remarks>
    public static readonly EdgeInsets Zero = new(0);

    /// <summary>Copies the four edges into the given variables.</summary>
    /// <remarks>Order is Top, Right, Bottom, Left, the order of the constructor.</remarks>
    public void Deconstruct(out double Top, out double Right, out double Bottom, out double Left)
    {
        Top = this.Top;
        Right = this.Right;
        Bottom = this.Bottom;
        Left = this.Left;
    }

    /// <summary>The total horizontal inset (Left + Right).</summary>
    /// <remarks>
    /// Summed without a check. Infinite edges of opposite sign sum to <c>NaN</c>.
    /// </remarks>
    public double Horizontal => Left + Right;

    /// <summary>The total vertical inset (Top + Bottom).</summary>
    /// <remarks>
    /// Summed without a check. Infinite edges of opposite sign sum to <c>NaN</c>.
    /// </remarks>
    public double Vertical => Top + Bottom;
}
