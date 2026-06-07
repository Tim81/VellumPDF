// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>Normalised RGB colour (0.0–1.0 per channel).</summary>
public readonly record struct ColorRgb(double R, double G, double B)
{
    public static readonly ColorRgb Black = new(0, 0, 0);
    public static readonly ColorRgb White = new(1, 1, 1);

    public static ColorRgb FromHex(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255.0,
        ((rgb >> 8) & 0xFF) / 255.0,
         (rgb & 0xFF) / 255.0);
}
