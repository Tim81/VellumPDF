// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>Normalised DeviceCMYK colour (0.0–1.0 per channel).</summary>
/// <param name="C">The cyan channel (0.0–1.0).</param>
/// <param name="M">The magenta channel (0.0–1.0).</param>
/// <param name="Y">The yellow channel (0.0–1.0).</param>
/// <param name="K">The key (black) channel (0.0–1.0).</param>
public readonly record struct ColorCmyk(double C, double M, double Y, double K)
{
    /// <summary>Process black (0, 0, 0, 1).</summary>
    public static readonly ColorCmyk Black = new(0, 0, 0, 1);

    /// <summary>White / no ink (0, 0, 0, 0).</summary>
    public static readonly ColorCmyk White = new(0, 0, 0, 0);

    /// <summary>
    /// Naive (non-colour-managed) conversion to RGB for preview or interop.
    /// Uses the standard CMYK-to-RGB formula: channel = (1 − ink) × (1 − K).
    /// </summary>
    public ColorRgb ToRgbApproximate() =>
        new((1 - C) * (1 - K), (1 - M) * (1 - K), (1 - Y) * (1 - K));

    /// <summary>
    /// Naive (non-colour-managed) conversion from RGB to CMYK.
    /// Uses the standard max-based GCR (Grey Component Replacement) formula.
    /// </summary>
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
