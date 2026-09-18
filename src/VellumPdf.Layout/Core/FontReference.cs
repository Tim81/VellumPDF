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
/// Reading the accessor for the other kind of font does not throw; it returns a value that is
/// not your font. Check <see cref="IsEmbedded"/> before reading <see cref="Standard14"/> or
/// <see cref="Embedded"/>.
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
    /// <remarks>
    /// Nothing is checked. A value the enumeration does not name, such as <c>(Standard14)99</c>, is
    /// stored: <see cref="MeasureString"/> then returns 0 at any finite size, and the save throws
    /// <see cref="IndexOutOfRangeException"/> once the font is selected on a page, which an empty
    /// table cell or list item in it also does.
    /// <para>Do not pass a value the enumeration does not name. A later major version will throw
    /// <see cref="ArgumentOutOfRangeException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other
    /// save overloads, not from this call, when <paramref name="font"/> is not a named
    /// <see cref="VellumPdf.Fonts.Standard14"/> value and the font is selected on a page.
    /// </exception>
    public FontReference(Standard14 font)
    {
        _standard14 = font;
        _embedded = null;
    }

    /// <summary>Creates a reference to an embedded TrueType font.</summary>
    /// <remarks>
    /// A null handle is stored. <see cref="IsEmbedded"/> is then false, and text in this reference
    /// is drawn in Helvetica.
    /// <para><b>Attention</b>: the handle is not checked against the document that saves it. A
    /// handle from a different <see cref="VellumPdf.Layout.Document"/> saves without an exception.
    /// The page refers to the font by its resource name and gets whatever font the saving document
    /// registered under that name, or none. When that is the same font file, the characters the
    /// saving document's own text also uses are drawn correctly and the rest are lost; otherwise
    /// the text is drawn wrong or not at all (#544). Use handles from the document you add the text
    /// to.</para>
    /// <para>Text drawn in an embedded font that holds an unpaired surrogate makes the save throw
    /// <see cref="ArgumentException"/>; see <see cref="TextStyle.FontRef"/>.</para>
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    public FontReference(EmbeddedFontHandle handle)
    {
        _standard14 = default;
        _embedded = handle;
    }

    /// <summary>Implicit conversion so existing <c>Standard14</c> values work unchanged.</summary>
    /// <remarks>
    /// Same as <see cref="FontReference(Standard14)"/>, including a value the enumeration does not
    /// name.
    /// </remarks>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from a later save, as described on <see cref="FontReference(Standard14)"/>.
    /// </exception>
    public static implicit operator FontReference(Standard14 font) => new(font);

    /// <summary>Implicit conversion from an embedded handle for ergonomic use in TextStyle init.</summary>
    /// <remarks>
    /// Same as <see cref="FontReference(EmbeddedFontHandle)"/>, including a null handle and a
    /// handle from another document.
    /// </remarks>
    public static implicit operator FontReference(EmbeddedFontHandle handle) => new(handle);

    /// <summary>
    /// Measures a string in points. Routes to Standard-14 or embedded metrics.
    /// </summary>
    /// <remarks>
    /// A null string throws <see cref="NullReferenceException"/>. A non-finite
    /// <paramref name="pointSize"/> is multiplied through and is not refused here.
    /// <para><see cref="VellumPdf.Fonts.Standard14.Symbol"/>,
    /// <see cref="VellumPdf.Fonts.Standard14.ZapfDingbats"/> and a Standard-14 value the
    /// enumeration does not name measure 0 for every character, so text in them is never wrapped
    /// (#470).</para>
    /// <para>On an embedded font, every character measured is added to the font's subset, so a
    /// string you measure but never draw still makes the embedded font larger.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    public double MeasureString(string text, double pointSize) => IsEmbedded
        ? _embedded!.MeasureString(text, pointSize)
        : Standard14Metrics.MeasureString(_standard14, text, pointSize);
}
