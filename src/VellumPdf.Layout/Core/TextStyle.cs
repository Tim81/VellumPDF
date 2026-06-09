// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Layout.Core;

/// <summary>Typography properties applied to a run of text.</summary>
public sealed class TextStyle
{
    /// <summary>A style with default values (Helvetica, 12 pt, auto leading, black).</summary>
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

    /// <summary>The font size in points. Defaults to 12.</summary>
    public double FontSize { get; init; } = 12;

    /// <summary>The line leading in points; 0 (the default) means auto (font size × 1.2).</summary>
    public double Leading { get; init; } = 0;  // 0 = auto (font-size * 1.2)

    /// <summary>The text colour. Defaults to <see cref="ColorRgb.Black"/>.</summary>
    public ColorRgb Color { get; init; } = ColorRgb.Black;

    /// <summary>The resolved leading: <see cref="Leading"/> when positive, otherwise font size × 1.2.</summary>
    public double EffectiveLeading => Leading > 0 ? Leading : FontSize * 1.2;

    /// <summary>
    /// When non-null, text rendered with this style will be wrapped in a /Link
    /// annotation pointing to this URI. Use a full URI string (e.g. "https://example.com").
    /// </summary>
    public string? LinkUri { get; init; }

    /// <summary>Measures a string using whichever font this style references.</summary>
    public double MeasureString(string text) => FontRef.MeasureString(text, FontSize);
}
