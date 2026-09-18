// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Graphics;

namespace VellumPdf.Layout.Core;

/// <summary>Normalised RGB colour (0.0–1.0 per channel).</summary>
/// <remarks>
/// Channels are not checked or clamped. Layout writes them into the content stream rounded to five
/// decimals, so a text colour of (<c>NaN</c>, 2, -1) is written as <c>NaN 2 -1 rg</c>. ISO 32000-2,
/// 8.6.4.3, requires each DeviceRGB component to be a number from 0.0 to 1.0.
/// <para>Do not pass a channel outside 0 to 1, or a non-finite one. A later major version will
/// refuse both (#509).</para>
/// </remarks>
public readonly record struct ColorRgb
{
    /// <summary>The red channel (0.0–1.0).</summary>
    /// <remarks>Any value is stored, and written to the page without clamping.</remarks>
    public double R { get; init; }

    /// <summary>The green channel (0.0–1.0).</summary>
    /// <remarks>Any value is stored, and written to the page without clamping.</remarks>
    public double G { get; init; }

    /// <summary>The blue channel (0.0–1.0).</summary>
    /// <remarks>Any value is stored, and written to the page without clamping.</remarks>
    public double B { get; init; }

    /// <summary>Creates a colour from the given channels.</summary>
    /// <remarks>Nothing is refused. The three values are stored as passed.</remarks>
    public ColorRgb(double R, double G, double B)
    {
        this.R = R;
        this.G = G;
        this.B = B;
    }

    /// <summary>Opaque black (0, 0, 0).</summary>
    /// <remarks>Equal to <c>new ColorRgb(0, 0, 0)</c> and to <c>default(ColorRgb)</c>.</remarks>
    public static readonly ColorRgb Black = new(0, 0, 0);

    /// <summary>Opaque white (1, 1, 1).</summary>
    /// <remarks>Equal to <c>new ColorRgb(1, 1, 1)</c>.</remarks>
    public static readonly ColorRgb White = new(1, 1, 1);

    /// <summary>Creates a colour from a packed 24-bit RGB value (e.g. <c>0xFF8800</c>).</summary>
    /// <remarks>
    /// The top byte is ignored, so <c>0x00FF0000</c> and <c>0xAAFF0000</c> give the same colour.
    /// Each channel is its byte divided by 255, so the result is always in range.
    /// </remarks>
    public static ColorRgb FromHex(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255.0,
        ((rgb >> 8) & 0xFF) / 255.0,
         (rgb & 0xFF) / 255.0);

    /// <summary>Copies the three channels into the given variables.</summary>
    /// <remarks>Order is R, G, B, the order of the constructor.</remarks>
    public void Deconstruct(out double R, out double G, out double B)
    {
        R = this.R;
        G = this.G;
        B = this.B;
    }

    /// <summary>Converts a layout <see cref="ColorRgb"/> to the kernel's <see cref="KernelColor"/>.</summary>
    /// <remarks>Channels are copied without a check.</remarks>
    public static implicit operator KernelColor(ColorRgb c) => new(c.R, c.G, c.B);

    /// <summary>Converts a kernel <see cref="KernelColor"/> to a layout <see cref="ColorRgb"/>.</summary>
    /// <remarks>Channels are copied without a check.</remarks>
    public static implicit operator ColorRgb(KernelColor c) => new(c.R, c.G, c.B);
}
