// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// Immutable axis-aligned rectangle in layout space (Y-down, points).
/// X increases right, Y increases downward (opposite of PDF's Y-up convention).
/// The Y-flip to PDF coordinates happens in <see cref="DrawContext"/>.
/// </summary>
/// <remarks>
/// A box is plain arithmetic. No component is checked, here or in any member of this type, so
/// a negative or non-finite value is stored and carried through every computed member.
/// Whatever consumes the box decides what that value means.
/// </remarks>
public readonly record struct LayoutBox
{
    /// <summary>The left edge of the box.</summary>
    /// <remarks>Any value is stored. <see cref="IsEmpty"/> does not read it.</remarks>
    public double X { get; init; }

    /// <summary>The top edge of the box.</summary>
    /// <remarks>Any value is stored. <see cref="IsEmpty"/> does not read it.</remarks>
    public double Y { get; init; }

    /// <summary>The width of the box.</summary>
    /// <remarks>Any value is stored, including a negative or non-finite one.</remarks>
    public double Width { get; init; }

    /// <summary>The height of the box.</summary>
    /// <remarks>Any value is stored, including a negative or non-finite one.</remarks>
    public double Height { get; init; }

    /// <summary>Creates a box from the given origin and extents.</summary>
    /// <remarks>Nothing is refused. The four values are stored as passed.</remarks>
    public LayoutBox(double X, double Y, double Width, double Height)
    {
        this.X = X;
        this.Y = Y;
        this.Width = Width;
        this.Height = Height;
    }

    /// <summary>Copies the origin and extents into the given variables.</summary>
    /// <remarks>Order is X, Y, Width, Height, the order of the constructor.</remarks>
    public void Deconstruct(out double X, out double Y, out double Width, out double Height)
    {
        X = this.X;
        Y = this.Y;
        Width = this.Width;
        Height = this.Height;
    }

    /// <summary>The right edge of the box (X + Width).</summary>
    /// <remarks>With a negative width, Right is left of X.</remarks>
    public double Right => X + Width;

    /// <summary>The bottom edge of the box (Y + Height).</summary>
    /// <remarks>With a negative height, Bottom is above Y.</remarks>
    public double Bottom => Y + Height;

    /// <summary>Returns a copy of this box with the height replaced.</summary>
    /// <remarks>Any height is accepted, including a negative or non-finite one.</remarks>
    public LayoutBox WithHeight(double height) => new(X, Y, Width, height);

    /// <summary>Returns a copy of this box with the top edge (Y) replaced.</summary>
    /// <remarks>Any value is accepted, including a non-finite one.</remarks>
    public LayoutBox WithY(double y) => new(X, y, Width, Height);

    /// <summary>Returns this box shrunk by the given insets.</summary>
    /// <remarks>
    /// Insets larger than the box are accepted and give a negative width or height. A negative
    /// inset grows the box. Infinite operands follow IEEE 754: an infinite width less an infinite
    /// inset of the same sign is <c>NaN</c>, and <see cref="IsEmpty"/> does not count <c>NaN</c>
    /// as empty.
    /// </remarks>
    public LayoutBox Deflate(double left, double top, double right, double bottom) =>
        new(X + left, Y + top, Width - left - right, Height - top - bottom);

    /// <summary>Returns this box shrunk by the given insets.</summary>
    /// <remarks>
    /// Calls the four-argument overload with the four edges of <paramref name="insets"/>, so the
    /// same arithmetic applies.
    /// </remarks>
    public LayoutBox Deflate(EdgeInsets insets) =>
        Deflate(insets.Left, insets.Top, insets.Right, insets.Bottom);

    /// <summary>True when the box has no positive area (zero or negative width or height).</summary>
    /// <remarks>
    /// Reads <see cref="Width"/> and <see cref="Height"/> only. A <c>NaN</c> extent does not
    /// make a box empty, because <c>NaN &lt;= 0</c> is false. The box is still empty when the
    /// other extent is zero or negative.
    /// </remarks>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Returns a compact string of the form <c>(X,Y W×H)</c>.</summary>
    /// <remarks>
    /// One decimal place in the current culture. Under <c>nl-NL</c>,
    /// <c>new LayoutBox(1, 2, 3, 4)</c> prints as <c>(1,0,2,0 3,0×4,0)</c>. Use it for display,
    /// not for parsing.
    /// </remarks>
    public override string ToString() => $"({X:F1},{Y:F1} {Width:F1}×{Height:F1})";
}
