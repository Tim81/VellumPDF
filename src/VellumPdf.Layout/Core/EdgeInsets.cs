// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>CSS-style four-sided inset (top, right, bottom, left) in points.</summary>
public readonly record struct EdgeInsets(double Top, double Right, double Bottom, double Left)
{
    public EdgeInsets(double all) : this(all, all, all, all) { }
    public EdgeInsets(double topBottom, double leftRight) : this(topBottom, leftRight, topBottom, leftRight) { }

    public static readonly EdgeInsets Zero = new(0);

    public double Horizontal => Left + Right;
    public double Vertical => Top + Bottom;
}
