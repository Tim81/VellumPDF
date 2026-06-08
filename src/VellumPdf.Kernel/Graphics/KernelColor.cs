// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Graphics;

/// <summary>
/// A kernel-local RGB colour value (components in [0, 1]).
/// Keeps the kernel independent of the layout layer's <c>ColorRgb</c> type.
/// </summary>
public readonly struct KernelColor(double r, double g, double b)
{
    /// <summary>Red component in the range [0, 1].</summary>
    public double R { get; } = r;

    /// <summary>Green component in the range [0, 1].</summary>
    public double G { get; } = g;

    /// <summary>Blue component in the range [0, 1].</summary>
    public double B { get; } = b;

    /// <summary>Fully opaque black.</summary>
    public static KernelColor Black { get; } = new(0, 0, 0);

    /// <summary>Fully opaque white.</summary>
    public static KernelColor White { get; } = new(1, 1, 1);
}
