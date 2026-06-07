// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// Immutable axis-aligned rectangle in layout space (Y-down, millimetres or points).
/// X increases right, Y increases downward (opposite of PDF's Y-up convention).
/// The Y-flip to PDF coordinates happens in <see cref="DrawContext"/>.
/// </summary>
public readonly struct LayoutBox
{
    public double X      { get; }
    public double Y      { get; }
    public double Width  { get; }
    public double Height { get; }

    public double Right  => X + Width;
    public double Bottom => Y + Height;

    public LayoutBox(double x, double y, double width, double height)
    {
        X = x; Y = y; Width = width; Height = height;
    }

    public LayoutBox WithHeight(double height) => new(X, Y, Width, height);
    public LayoutBox WithY(double y)            => new(X, y, Width, Height);

    /// <summary>Returns this box shrunk by the given insets.</summary>
    public LayoutBox Deflate(double left, double top, double right, double bottom) =>
        new(X + left, Y + top, Width - left - right, Height - top - bottom);

    public LayoutBox Deflate(EdgeInsets insets) =>
        Deflate(insets.Left, insets.Top, insets.Right, insets.Bottom);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"({X:F1},{Y:F1} {Width:F1}×{Height:F1})";
}
