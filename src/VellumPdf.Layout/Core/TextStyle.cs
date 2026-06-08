// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Layout.Core;

/// <summary>Typography properties applied to a run of text.</summary>
public sealed class TextStyle
{
    public static readonly TextStyle Default = new();

    /// <summary>
    /// The font to use. Accepts a <see cref="Standard14"/> value (implicit conversion)
    /// or an <see cref="EmbeddedFontHandle"/> returned by <c>Document.UseTrueTypeFont</c>.
    /// </summary>
    public FontReference FontRef { get; init; } = Standard14.Helvetica;

    /// <summary>
    /// Convenience accessor for the Standard-14 font value.
    /// Valid only when <see cref="FontRef"/> is not an embedded font.
    /// Preserved for backward compatibility with existing code.
    /// </summary>
    public Standard14 Font
    {
        get => FontRef.Standard14;
        init => FontRef = value;
    }

    public double FontSize { get; init; } = 12;
    public double Leading { get; init; } = 0;  // 0 = auto (font-size * 1.2)
    public ColorRgb Color { get; init; } = ColorRgb.Black;

    public double EffectiveLeading => Leading > 0 ? Leading : FontSize * 1.2;

    /// <summary>Measures a string using whichever font this style references.</summary>
    public double MeasureString(string text) => FontRef.MeasureString(text, FontSize);
}
