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
/// <param name="Top">The inset on the top edge. Not validated; see the type remarks.</param>
/// <param name="Right">The inset on the right edge. Not validated; see the type remarks.</param>
/// <param name="Bottom">The inset on the bottom edge. Not validated; see the type remarks.</param>
/// <param name="Left">The inset on the left edge. Not validated; see the type remarks.</param>
public readonly record struct EdgeInsets(double Top, double Right, double Bottom, double Left)
{
    /// <summary>Creates an inset with the same value on all four edges.</summary>
    public EdgeInsets(double all) : this(all, all, all, all) { }

    /// <summary>Creates an inset with one value for the top and bottom edges and another for the left and right edges.</summary>
    public EdgeInsets(double topBottom, double leftRight) : this(topBottom, leftRight, topBottom, leftRight) { }

    /// <summary>An inset of zero on all four edges.</summary>
    public static readonly EdgeInsets Zero = new(0);

    /// <summary>The total horizontal inset (Left + Right).</summary>
    /// <remarks>Arithmetic only. Non-finite addends yield a non-finite sum.</remarks>
    public double Horizontal => Left + Right;

    /// <summary>The total vertical inset (Top + Bottom).</summary>
    /// <remarks>Arithmetic only. Non-finite addends yield a non-finite sum.</remarks>
    public double Vertical => Top + Bottom;
}
