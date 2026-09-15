// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Graphics;

namespace VellumPdf.Layout.Core;

/// <summary>Normalised RGB colour (0.0–1.0 per channel).</summary>
/// <remarks>
/// Channels are not clamped or checked for finiteness. A value outside 0 to 1, or a
/// non-finite channel, can reach the content stream (#509). <see cref="FromHex"/> ignores
/// the top byte.
/// </remarks>
public readonly record struct ColorRgb
{
    /// <summary>The red channel (0.0–1.0).</summary>
    /// <remarks>Not clamped or checked for finiteness. See the type remarks.</remarks>
    public double R { get; init; }

    /// <summary>The green channel (0.0–1.0).</summary>
    /// <remarks>Not clamped or checked for finiteness. See the type remarks.</remarks>
    public double G { get; init; }

    /// <summary>The blue channel (0.0–1.0).</summary>
    /// <remarks>Not clamped or checked for finiteness. See the type remarks.</remarks>
    public double B { get; init; }

    /// <summary>Creates a colour from the given channels.</summary>
    /// <remarks>Channels are stored as given. See the type remarks.</remarks>
    public ColorRgb(double R, double G, double B)
    {
        this.R = R;
        this.G = G;
        this.B = B;
    }

    /// <summary>Opaque black (0, 0, 0).</summary>
    /// <remarks>Finite and in range. Not a special case of the unclamped constructor.</remarks>
    public static readonly ColorRgb Black = new(0, 0, 0);

    /// <summary>Opaque white (1, 1, 1).</summary>
    /// <remarks>Finite and in range. Not a special case of the unclamped constructor.</remarks>
    public static readonly ColorRgb White = new(1, 1, 1);

    /// <summary>Creates a colour from a packed 24-bit RGB value (e.g. <c>0xFF8800</c>).</summary>
    /// <remarks>
    /// The top byte is ignored. <c>0x00FF0000</c> and <c>0xAAFF0000</c> produce the same
    /// colour. Channels are not clamped: the constructor accepts any double, and a non-finite
    /// channel can reach the content stream (#509).
    /// </remarks>
    public static ColorRgb FromHex(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255.0,
        ((rgb >> 8) & 0xFF) / 255.0,
         (rgb & 0xFF) / 255.0);

    /// <summary>Copies the three channels into the given variables.</summary>
    /// <remarks>The values are as stored. No clamping.</remarks>
    public void Deconstruct(out double R, out double G, out double B)
    {
        R = this.R;
        G = this.G;
        B = this.B;
    }

    /// <summary>Converts a layout <see cref="ColorRgb"/> to the kernel's <see cref="KernelColor"/>.</summary>
    /// <remarks>Channels are copied as stored. No clamping.</remarks>
    public static implicit operator KernelColor(ColorRgb c) => new(c.R, c.G, c.B);

    /// <summary>Converts a kernel <see cref="KernelColor"/> to a layout <see cref="ColorRgb"/>.</summary>
    /// <remarks>Channels are copied as stored. No clamping.</remarks>
    public static implicit operator ColorRgb(KernelColor c) => new(c.R, c.G, c.B);
}
