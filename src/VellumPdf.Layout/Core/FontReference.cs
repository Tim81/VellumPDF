// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Layout.Core;

/// <summary>
/// Discriminated union representing either a Standard-14 font or an embedded
/// TrueType font handle. Used by <see cref="TextStyle"/> to carry either kind
/// of font in a single, AOT-safe value type.
/// </summary>
/// <remarks>
/// The two accessors are not exclusive. Reading <see cref="Standard14"/> on an embedded
/// reference, or <see cref="Embedded"/> on a Standard-14 reference, is not refused; see
/// those members.
/// </remarks>
public readonly struct FontReference
{
    private readonly Standard14 _standard14;
    private readonly EmbeddedFontHandle? _embedded;

    /// <summary>Whether this reference points to an embedded TrueType font.</summary>
    /// <remarks>
    /// False for the default value, a Standard-14 constructor, and a null handle.
    /// True only when constructed from a non-null <see cref="EmbeddedFontHandle"/>.
    /// </remarks>
    public bool IsEmbedded => _embedded is not null;

    /// <summary>The Standard-14 font (valid only when <see cref="IsEmbedded"/> is false).</summary>
    /// <remarks>
    /// An invalid read is not refused. On an embedded reference this returns
    /// <see cref="Standard14.Helvetica"/>, the default of the unused field, with nothing
    /// reported. Check <see cref="IsEmbedded"/> first.
    /// </remarks>
    public Standard14 Standard14 => _standard14;

    /// <summary>The embedded font handle (valid only when <see cref="IsEmbedded"/> is true).</summary>
    /// <remarks>
    /// An invalid read is not refused. On a Standard-14 reference this returns
    /// <see langword="null"/> behind a non-nullable return type. Check
    /// <see cref="IsEmbedded"/> first.
    /// </remarks>
    public EmbeddedFontHandle Embedded => _embedded!;

    /// <summary>Creates a reference to a Standard-14 font.</summary>
    /// <remarks>Every <see cref="Standard14"/> value is accepted.</remarks>
    public FontReference(Standard14 font)
    {
        _standard14 = font;
        _embedded = null;
    }

    /// <summary>Creates a reference to an embedded TrueType font.</summary>
    /// <remarks>
    /// A null handle is stored. <see cref="IsEmbedded"/> is then false, and
    /// <see cref="Embedded"/> returns null.
    /// </remarks>
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
    /// <remarks>
    /// A null string throws <see cref="NullReferenceException"/>. A non-finite
    /// <paramref name="pointSize"/> is multiplied through and is not refused here.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    public double MeasureString(string text, double pointSize) => IsEmbedded
        ? _embedded!.MeasureString(text, pointSize)
        : Standard14Metrics.MeasureString(_standard14, text, pointSize);
}
