// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>Normalised DeviceCMYK colour (0.0–1.0 per channel).</summary>
/// <remarks>
/// No Layout member takes a <see cref="ColorCmyk"/>, so a value of this type reaches a page only
/// through <see cref="ToRgbApproximate"/>, or when you pass its channels to a
/// <see cref="VellumPdf.Canvas.PdfCanvas"/> colour method yourself, such as
/// <see cref="VellumPdf.Canvas.PdfCanvas.SetFillColorCmyk"/>. Channels are not checked. ISO
/// 32000-2, 8.6.4.4, requires each DeviceCMYK component to be a number from 0.0 to 1.0.
/// <para>Do not pass a non-finite channel; #509 plans to refuse one, as for <see cref="ColorRgb"/>.
/// Keep each channel within 0 to 1, the range that clause requires.</para>
/// </remarks>
public readonly record struct ColorCmyk
{
    /// <summary>The cyan channel (0.0–1.0).</summary>
    /// <remarks>Any value is stored.</remarks>
    public double C { get; init; }

    /// <summary>The magenta channel (0.0–1.0).</summary>
    /// <remarks>Any value is stored.</remarks>
    public double M { get; init; }

    /// <summary>The yellow channel (0.0–1.0).</summary>
    /// <remarks>Any value is stored.</remarks>
    public double Y { get; init; }

    /// <summary>The key (black) channel (0.0–1.0).</summary>
    /// <remarks>Any value is stored.</remarks>
    public double K { get; init; }

    /// <summary>Creates a colour from the given channels.</summary>
    /// <remarks>Nothing is refused. The four values are stored as passed.</remarks>
    public ColorCmyk(double C, double M, double Y, double K)
    {
        this.C = C;
        this.M = M;
        this.Y = Y;
        this.K = K;
    }

    /// <summary>Process black (0, 0, 0, 1).</summary>
    /// <remarks>Equal to <c>new ColorCmyk(0, 0, 0, 1)</c>.</remarks>
    public static readonly ColorCmyk Black = new(0, 0, 0, 1);

    /// <summary>White / no ink (0, 0, 0, 0).</summary>
    /// <remarks>
    /// Equal to <c>new ColorCmyk(0, 0, 0, 0)</c> and to <c>default(ColorCmyk)</c>.
    /// </remarks>
    public static readonly ColorCmyk White = new(0, 0, 0, 0);

    /// <summary>Copies the four channels into the given variables.</summary>
    /// <remarks>Order is C, M, Y, K, the order of the constructor.</remarks>
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
    /// Computed without a check. A channel outside 0 to 1 gives an RGB channel outside 0 to 1: (0,
    /// 0, 0, 2) gives -1 in all three. A non-finite channel gives a non-finite result in each
    /// channel it feeds, and <see cref="K"/> feeds all three.
    /// </remarks>
    public ColorRgb ToRgbApproximate() =>
        new((1 - C) * (1 - K), (1 - M) * (1 - K), (1 - Y) * (1 - K));

    /// <summary>
    /// Naive (non-colour-managed) conversion from RGB to CMYK.
    /// Uses the standard max-based GCR (Grey Component Replacement) formula.
    /// </summary>
    /// <remarks>
    /// Computed without a check. An input returns <see cref="Black"/> when 1 minus its largest
    /// channel comes out at 1 or more, which includes a largest channel of 0 or less. Otherwise any
    /// channel outside 0 to 1 can give a result outside 0 to 1: (2, 0, 0) gives a <see cref="K"/>
    /// of -1, and (-1, 0.5, 0.5) a <see cref="C"/> of 3, and a non-finite channel gives a
    /// non-finite result.
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
