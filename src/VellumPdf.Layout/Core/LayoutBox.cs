// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// Immutable axis-aligned rectangle in layout space (Y-down, millimetres or points).
/// X increases right, Y increases downward (opposite of PDF's Y-up convention).
/// The Y-flip to PDF coordinates happens in <see cref="DrawContext"/>.
/// </summary>
/// <param name="X">The left edge of the box.</param>
/// <param name="Y">The top edge of the box.</param>
/// <param name="Width">The width of the box.</param>
/// <param name="Height">The height of the box.</param>
public readonly record struct LayoutBox(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge of the box (X + Width).</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge of the box (Y + Height).</summary>
    public double Bottom => Y + Height;

    /// <summary>Returns a copy of this box with the height replaced.</summary>
    public LayoutBox WithHeight(double height) => new(X, Y, Width, height);

    /// <summary>Returns a copy of this box with the top edge (Y) replaced.</summary>
    public LayoutBox WithY(double y) => new(X, y, Width, Height);

    /// <summary>Returns this box shrunk by the given insets.</summary>
    public LayoutBox Deflate(double left, double top, double right, double bottom) =>
        new(X + left, Y + top, Width - left - right, Height - top - bottom);

    /// <summary>Returns this box shrunk by the given insets.</summary>
    public LayoutBox Deflate(EdgeInsets insets) =>
        Deflate(insets.Left, insets.Top, insets.Right, insets.Bottom);

    /// <summary>True when the box has no positive area (zero or negative width or height).</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Returns a compact string of the form <c>(X,Y W×H)</c>.</summary>
    public override string ToString() => $"({X:F1},{Y:F1} {Width:F1}×{Height:F1})";
}
