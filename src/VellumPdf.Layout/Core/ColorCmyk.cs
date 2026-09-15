// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>Normalised DeviceCMYK colour (0.0–1.0 per channel).</summary>
/// <remarks>
/// Channels are not clamped or checked for finiteness. A value outside 0 to 1, or a
/// non-finite channel, can reach the content stream (#509).
/// </remarks>
public readonly record struct ColorCmyk
{
    /// <summary>The cyan channel (0.0–1.0).</summary>
    /// <remarks>Not clamped or checked for finiteness. See the type remarks.</remarks>
    public double C { get; init; }

    /// <summary>The magenta channel (0.0–1.0).</summary>
    /// <remarks>Not clamped or checked for finiteness. See the type remarks.</remarks>
    public double M { get; init; }

    /// <summary>The yellow channel (0.0–1.0).</summary>
    /// <remarks>Not clamped or checked for finiteness. See the type remarks.</remarks>
    public double Y { get; init; }

    /// <summary>The key (black) channel (0.0–1.0).</summary>
    /// <remarks>Not clamped or checked for finiteness. See the type remarks.</remarks>
    public double K { get; init; }

    /// <summary>Creates a colour from the given channels.</summary>
    /// <remarks>Channels are stored as given. See the type remarks.</remarks>
    public ColorCmyk(double C, double M, double Y, double K)
    {
        this.C = C;
        this.M = M;
        this.Y = Y;
        this.K = K;
    }

    /// <summary>Process black (0, 0, 0, 1).</summary>
    /// <remarks>Finite and in range. Not a special case of the unclamped constructor.</remarks>
    public static readonly ColorCmyk Black = new(0, 0, 0, 1);

    /// <summary>White / no ink (0, 0, 0, 0).</summary>
    /// <remarks>Finite and in range. Not a special case of the unclamped constructor.</remarks>
    public static readonly ColorCmyk White = new(0, 0, 0, 0);

    /// <summary>Copies the four channels into the given variables.</summary>
    /// <remarks>The values are as stored. No clamping.</remarks>
    public void Deconstruct(out double C, out double M, out double Y, out double K)
    {
        C = this.C;
        M = this.M;
        Y = this.Y;
        K = this.K;
    }

    /// <summary>
    /// Naive (non-colour-managed) conversion to RGB for preview or interop.
    /// Uses the standard CMYK-to-RGB formula: channel = (1 − ink) × (1 − K).
    /// </summary>
    /// <remarks>
    /// Channels are not clamped or checked for finiteness. A value outside 0 to 1, or a
    /// non-finite channel, is multiplied through and handed to <see cref="ColorRgb"/> as
    /// given (#509).
    /// </remarks>
    public ColorRgb ToRgbApproximate() =>
        new((1 - C) * (1 - K), (1 - M) * (1 - K), (1 - Y) * (1 - K));

    /// <summary>
    /// Naive (non-colour-managed) conversion from RGB to CMYK.
    /// Uses the standard max-based GCR (Grey Component Replacement) formula.
    /// </summary>
    /// <remarks>
    /// Input channels are not clamped. A non-finite RGB channel is not refused; the
    /// <c>k &gt;= 1</c> path can still return <see cref="Black"/> (#509).
    /// </remarks>
    public static ColorCmyk FromRgb(ColorRgb rgb)
    {
        var r = rgb.R;
        var g = rgb.G;
        var b = rgb.B;
        var k = 1.0 - Math.Max(r, Math.Max(g, b));
        // Guard against divide-by-zero when K ≈ 1 (pure black).
        if (k >= 1.0)
            return Black;
        var denom = 1.0 - k;
        return new ColorCmyk(
            (1.0 - r - k) / denom,
            (1.0 - g - k) / denom,
            (1.0 - b - k) / denom,
            k);
    }
}
