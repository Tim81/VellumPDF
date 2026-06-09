// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Graphics;

namespace VellumPdf.Layout.Core;

/// <summary>Normalised RGB colour (0.0–1.0 per channel).</summary>
/// <param name="R">The red channel (0.0–1.0).</param>
/// <param name="G">The green channel (0.0–1.0).</param>
/// <param name="B">The blue channel (0.0–1.0).</param>
public readonly record struct ColorRgb(double R, double G, double B)
{
    /// <summary>Opaque black (0, 0, 0).</summary>
    public static readonly ColorRgb Black = new(0, 0, 0);

    /// <summary>Opaque white (1, 1, 1).</summary>
    public static readonly ColorRgb White = new(1, 1, 1);

    /// <summary>Creates a colour from a packed 24-bit RGB value (e.g. <c>0xFF8800</c>).</summary>
    public static ColorRgb FromHex(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255.0,
        ((rgb >> 8) & 0xFF) / 255.0,
         (rgb & 0xFF) / 255.0);

    /// <summary>Converts a layout <see cref="ColorRgb"/> to the kernel's <see cref="KernelColor"/>.</summary>
    public static implicit operator KernelColor(ColorRgb c) => new(c.R, c.G, c.B);

    /// <summary>Converts a kernel <see cref="KernelColor"/> to a layout <see cref="ColorRgb"/>.</summary>
    public static implicit operator ColorRgb(KernelColor c) => new(c.R, c.G, c.B);
}
