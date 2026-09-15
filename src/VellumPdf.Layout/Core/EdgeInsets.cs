// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>CSS-style four-sided inset (top, right, bottom, left) in points.</summary>
/// <remarks>
/// This type does not refuse a non-finite or negative inset. Consumers that write the
/// value into a content stream do:
/// <see cref="VellumPdf.Layout.Elements.LineSeparator.Margins"/> and
/// <see cref="VellumPdf.Layout.Elements.Table.Cell.Padding"/> check finiteness at save.
/// The other margin properties do not. Do not treat construction as validation.
/// </remarks>
public readonly record struct EdgeInsets
{
    /// <summary>The inset on the top edge.</summary>
    /// <remarks>Not validated. See the type remarks.</remarks>
    public double Top { get; init; }

    /// <summary>The inset on the right edge.</summary>
    /// <remarks>Not validated. See the type remarks.</remarks>
    public double Right { get; init; }

    /// <summary>The inset on the bottom edge.</summary>
    /// <remarks>Not validated. See the type remarks.</remarks>
    public double Bottom { get; init; }

    /// <summary>The inset on the left edge.</summary>
    /// <remarks>Not validated. See the type remarks.</remarks>
    public double Left { get; init; }

    /// <summary>Creates an inset from the four edges.</summary>
    /// <remarks>Edges are stored as given. See the type remarks.</remarks>
    public EdgeInsets(double Top, double Right, double Bottom, double Left)
    {
        this.Top = Top;
        this.Right = Right;
        this.Bottom = Bottom;
        this.Left = Left;
    }

    /// <summary>Creates an inset with the same value on all four edges.</summary>
    /// <remarks>The value is stored as given on every edge. See the type remarks.</remarks>
    public EdgeInsets(double all) : this(all, all, all, all) { }

    /// <summary>Creates an inset with one value for the top and bottom edges and another for the left and right edges.</summary>
    /// <remarks>Both values are stored as given. See the type remarks.</remarks>
    public EdgeInsets(double topBottom, double leftRight) : this(topBottom, leftRight, topBottom, leftRight) { }

    /// <summary>An inset of zero on all four edges.</summary>
    /// <remarks>Finite and in range. Not a special case of the unvalidated constructor.</remarks>
    public static readonly EdgeInsets Zero = new(0);

    /// <summary>Copies the four edges into the given variables.</summary>
    /// <remarks>The values are as stored. No validation.</remarks>
    public void Deconstruct(out double Top, out double Right, out double Bottom, out double Left)
    {
        Top = this.Top;
        Right = this.Right;
        Bottom = this.Bottom;
        Left = this.Left;
    }

    /// <summary>The total horizontal inset (Left + Right).</summary>
    /// <remarks>Arithmetic only. Non-finite addends yield a non-finite sum.</remarks>
    public double Horizontal => Left + Right;

    /// <summary>The total vertical inset (Top + Bottom).</summary>
    /// <remarks>Arithmetic only. Non-finite addends yield a non-finite sum.</remarks>
    public double Vertical => Top + Bottom;
}
