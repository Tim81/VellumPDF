// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// An immutable run of text with a uniform style, used to compose mixed-style paragraphs.
/// </summary>
/// <remarks>
/// <b>A null text is refused.</b> The constructor throws <see cref="ArgumentNullException"/> naming
/// <c>Text</c> (#547). Before that it was stored, and the save threw
/// <see cref="NullReferenceException"/> when it laid out the paragraph holding the run.
/// <para>A null style is stored, and refused where the paragraph is laid out, with
/// <see cref="ArgumentNullException"/> naming <c>Style</c>. That is where 2.3.3 dereferenced it, and
/// it did so only for a run that yields a word: a non-white-space character, U+00A0 included, so the
/// run is refused for the text <c>Hello</c> or <c>a\nb</c> and saves for empty text, white space, a
/// tab or line breaks alone. The save throws <see cref="NullReferenceException"/> there on 2.3.3.
/// <b>Do not pass a null style.</b> 3.0 refuses it from the constructor, whatever the text, and the
/// diagnostics channel (#607) will report the saved cases before then.</para>
/// Refusals on size, leading, font and text colour are on <see cref="TextStyle"/> and are raised
/// from the save. The boundary between this run and the next is drawn as a space unless a line
/// break falls on it; see <see cref="Paragraph"/>.
/// </remarks>
/// <exception cref="ArgumentNullException">
/// The text is <see langword="null"/>. <c>ParamName</c> is <c>Text</c>, the name of the
/// constructor's parameter. A null style raises it from the save, not from the constructor, when the
/// run yields a word; <c>ParamName</c> is <c>Style</c>, and the document cannot be saved again.
/// </exception>
/// <exception cref="ArgumentException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the text holds an unpaired surrogate and is measured in an embedded font;
/// see <see cref="TextStyle.FontRef"/>.
/// </exception>
/// <exception cref="InvalidOperationException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the style's size or leading is refused, or a channel of its text colour is
/// not finite; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and
/// <see cref="TextStyle.Color"/>.
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
    /// <b>Never null; the constructor refuses a null.</b> See the type remarks.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public string Text { get; } = Text ?? throw new ArgumentNullException(nameof(Text));

    /// <summary>The run's text style.</summary>
    /// <remarks>
    /// Null when the constructor was given a null; see the type remarks for when that is refused.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the run's text holds an unpaired surrogate and is measured in an
    /// embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused, or a channel of its text
    /// colour is not finite; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/>
    /// and <see cref="TextStyle.Color"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public TextStyle Style { get; } = Style;
}
