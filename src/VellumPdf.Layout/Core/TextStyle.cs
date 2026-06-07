// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Layout.Core;

/// <summary>Typography properties applied to a run of text.</summary>
public sealed class TextStyle
{
    public static readonly TextStyle Default = new();

    public Standard14 Font     { get; init; } = Standard14.Helvetica;
    public double     FontSize { get; init; } = 12;
    public double     Leading  { get; init; } = 0;  // 0 = auto (font-size * 1.2)
    public ColorRgb   Color    { get; init; } = ColorRgb.Black;

    public double EffectiveLeading => Leading > 0 ? Leading : FontSize * 1.2;
}
