// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>CSS-style four-sided inset (top, right, bottom, left) in points.</summary>
public readonly struct EdgeInsets
{
    public double Top    { get; }
    public double Right  { get; }
    public double Bottom { get; }
    public double Left   { get; }

    public EdgeInsets(double all) : this(all, all, all, all) { }
    public EdgeInsets(double topBottom, double leftRight) : this(topBottom, leftRight, topBottom, leftRight) { }
    public EdgeInsets(double top, double right, double bottom, double left)
    {
        Top = top; Right = right; Bottom = bottom; Left = left;
    }

    public static readonly EdgeInsets Zero = new(0);

    public double Horizontal => Left + Right;
    public double Vertical   => Top + Bottom;
}
