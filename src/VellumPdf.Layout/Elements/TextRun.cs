// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// An immutable run of text with a uniform style, used to compose mixed-style paragraphs.
/// </summary>
/// <remarks>
/// Both arguments are stored without a check. A null text makes the save throw when it lays out the
/// paragraph holding this run, and so does a null style on a run whose text holds a character other
/// than white space. U+00A0 NO-BREAK SPACE counts as such a character; a tab and other white space
/// do not. Refusals on the style's size also fire from the save; see <see cref="TextStyle"/>. The
/// boundary between this run and the next is drawn as a space unless a line break falls on it; see
/// <see cref="Paragraph"/>.
/// <para>Do not pass null for either argument. A later major version will throw
/// <see cref="ArgumentNullException"/> from the constructor.</para>
/// </remarks>
/// <exception cref="NullReferenceException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the text is <see langword="null"/>, or the style is null and the text
/// holds a character other than white space. U+00A0 NO-BREAK SPACE counts as such a character; a
/// tab and other white space do not.
/// </exception>
/// <exception cref="ArgumentException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the text holds an unpaired surrogate and is measured in an embedded font;
/// see <see cref="TextStyle.FontRef"/>.
/// </exception>
/// <exception cref="InvalidOperationException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the style's size or leading is refused; see
/// <see cref="TextStyle.FontSize"/>.
/// </exception>
/// <exception cref="IndexOutOfRangeException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value the
/// enumeration does not name and the font is selected on a page; see
/// <see cref="TextStyle.FontRef"/>.
/// </exception>
public sealed class TextRun(string Text, TextStyle Style)
{
    /// <summary>The run's text.</summary>
    /// <remarks>
    /// Any value is stored. A null value makes the save throw; see the type remarks.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public string Text { get; } = Text;

    /// <summary>The run's text style.</summary>
    /// <remarks>
    /// Any value is stored. A null value makes the save throw when the run's text holds a character
    /// other than white space; see the type remarks for which characters count.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style is null and the run's text holds a character other than
    /// white space; see the type remarks.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the run's text holds an unpaired surrogate and is measured in an
    /// embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public TextStyle Style { get; } = Style;
}
