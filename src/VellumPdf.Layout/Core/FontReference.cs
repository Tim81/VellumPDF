// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Layout.Core;

/// <summary>
/// Discriminated union representing either a Standard-14 font or an embedded
/// TrueType font handle. Used by <see cref="TextStyle"/> to carry either kind
/// of font in a single, AOT-safe value type.
/// </summary>
public readonly struct FontReference
{
    private readonly Standard14 _standard14;
    private readonly EmbeddedFontHandle? _embedded;

    /// <summary>Whether this reference points to an embedded TrueType font.</summary>
    public bool IsEmbedded => _embedded is not null;

    /// <summary>The Standard-14 font (valid only when <see cref="IsEmbedded"/> is false).</summary>
    public Standard14 Standard14 => _standard14;

    /// <summary>The embedded font handle (valid only when <see cref="IsEmbedded"/> is true).</summary>
    public EmbeddedFontHandle Embedded => _embedded!;

    /// <summary>Creates a reference to a Standard-14 font.</summary>
    public FontReference(Standard14 font)
    {
        _standard14 = font;
        _embedded = null;
    }

    /// <summary>Creates a reference to an embedded TrueType font.</summary>
    public FontReference(EmbeddedFontHandle handle)
    {
        _standard14 = default;
        _embedded = handle;
    }

    /// <summary>Implicit conversion so existing <c>Standard14</c> values work unchanged.</summary>
    public static implicit operator FontReference(Standard14 font) => new(font);

    /// <summary>Implicit conversion from an embedded handle for ergonomic use in TextStyle init.</summary>
    public static implicit operator FontReference(EmbeddedFontHandle handle) => new(handle);

    /// <summary>
    /// Measures a string in points. Routes to Standard-14 or embedded metrics.
    /// </summary>
    public double MeasureString(string text, double pointSize) => IsEmbedded
        ? _embedded!.MeasureString(text, pointSize)
        : Standard14Metrics.MeasureString(_standard14, text, pointSize);
}
