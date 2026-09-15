// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// Immutable axis-aligned rectangle in layout space (Y-down, millimetres or points).
/// X increases right, Y increases downward (opposite of PDF's Y-up convention).
/// The Y-flip to PDF coordinates happens in <see cref="DrawContext"/>.
/// </summary>
/// <remarks>
/// No component is refused. A negative or non-finite width or height is stored as given.
/// <see cref="IsEmpty"/> is true when width or height is less than or equal to zero;
/// <c>NaN</c> is not, so a <c>NaN</c> extent leaves <see cref="IsEmpty"/> false.
/// </remarks>
public readonly record struct LayoutBox
{
    /// <summary>The left edge of the box.</summary>
    /// <remarks>Not validated. See the type remarks.</remarks>
    public double X { get; init; }

    /// <summary>The top edge of the box.</summary>
    /// <remarks>Not validated. See the type remarks.</remarks>
    public double Y { get; init; }

    /// <summary>The width of the box.</summary>
    /// <remarks>Not validated. A negative or non-finite value is stored as given. See the type.</remarks>
    public double Width { get; init; }

    /// <summary>The height of the box.</summary>
    /// <remarks>Not validated. A negative or non-finite value is stored as given. See the type.</remarks>
    public double Height { get; init; }

    /// <summary>Creates a box from the given origin and extents.</summary>
    /// <remarks>Components are stored as given. See the type remarks.</remarks>
    public LayoutBox(double X, double Y, double Width, double Height)
    {
        this.X = X;
        this.Y = Y;
        this.Width = Width;
        this.Height = Height;
    }

    /// <summary>Copies the origin and extents into the given variables.</summary>
    /// <remarks>The values are as stored. No validation.</remarks>
    public void Deconstruct(out double X, out double Y, out double Width, out double Height)
    {
        X = this.X;
        Y = this.Y;
        Width = this.Width;
        Height = this.Height;
    }

    /// <summary>The right edge of the box (X + Width).</summary>
    /// <remarks>Arithmetic only. A negative width yields a Right less than X.</remarks>
    public double Right => X + Width;

    /// <summary>The bottom edge of the box (Y + Height).</summary>
    /// <remarks>Arithmetic only. A negative height yields a Bottom less than Y.</remarks>
    public double Bottom => Y + Height;

    /// <summary>Returns a copy of this box with the height replaced.</summary>
    /// <remarks>
    /// A negative or non-finite height is stored as given. <see cref="IsEmpty"/> is true when
    /// width or height is less than or equal to zero. <c>NaN</c> is not less than or equal to
    /// zero, so a <c>NaN</c> height leaves <see cref="IsEmpty"/> false.
    /// </remarks>
    public LayoutBox WithHeight(double height) => new(X, Y, Width, height);

    /// <summary>Returns a copy of this box with the top edge (Y) replaced.</summary>
    /// <remarks>Not refused. A non-finite Y is stored as given.</remarks>
    public LayoutBox WithY(double y) => new(X, y, Width, Height);

    /// <summary>Returns this box shrunk by the given insets.</summary>
    /// <remarks>
    /// Insets larger than the box are not refused. The result can have a negative width or
    /// height; <see cref="IsEmpty"/> is then true.
    /// </remarks>
    public LayoutBox Deflate(double left, double top, double right, double bottom) =>
        new(X + left, Y + top, Width - left - right, Height - top - bottom);

    /// <summary>Returns this box shrunk by the given insets.</summary>
    /// <remarks>
    /// Same as the four-argument overload: insets larger than the box yield a negative
    /// extent and <see cref="IsEmpty"/> is then true.
    /// </remarks>
    public LayoutBox Deflate(EdgeInsets insets) =>
        Deflate(insets.Left, insets.Top, insets.Right, insets.Bottom);

    /// <summary>True when the box has no positive area (zero or negative width or height).</summary>
    /// <remarks>
    /// False when a component is <c>NaN</c>, because <c>NaN &lt;= 0</c> is false.
    /// </remarks>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Returns a compact string of the form <c>(X,Y W×H)</c>.</summary>
    public override string ToString() => $"({X:F1},{Y:F1} {Width:F1}×{Height:F1})";
}
