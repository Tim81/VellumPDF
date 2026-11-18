// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// An immutable run of text with a uniform style, used to compose mixed-style paragraphs.
/// </summary>
/// <remarks>
/// **A null text is refused.** The constructor throws <see cref="ArgumentNullException"/> naming
/// <c>Text</c> (#547). Before that it was stored, and the save threw
/// <see cref="NullReferenceException"/> when it laid out the paragraph holding the run.
/// <para>A null style is not refused. The run stores <see cref="TextStyle.Default"/> in its
/// place, the style a <see cref="Paragraph"/> or a list item without a style of its own falls back
/// to, so the run is measured and drawn in Helvetica 12 pt, black. Before #547 a null style made the
/// save throw when the run's text held a character other than white space, and a run of white space
/// alone saved. Refusing it now would turn a document that saves today into an exception, which a
/// release before 3.0 must not do (#484).</para>
/// <para><b>Do not pass a null style.</b> It is replaced by <see cref="TextStyle.Default"/> today and
/// not reported, because there is no diagnostics channel yet. A later major version will refuse it
/// with <see cref="ArgumentNullException"/> naming <c>Style</c>, and the diagnostics channel will
/// report it before then. Pass <see cref="TextStyle.Default"/> to get the same result.</para>
/// Refusals on size, leading and font are on <see cref="TextStyle"/> and are raised from the save.
/// The boundary between this run and the next is drawn as a space unless a line break falls on it;
/// see <see cref="Paragraph"/>.
/// </remarks>
/// <exception cref="ArgumentNullException">
/// The text is <see langword="null"/>. <c>ParamName</c> is <c>Text</c>, the name of the
/// constructor's parameter.
/// </exception>
/// <exception cref="ArgumentException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the text holds an unpaired surrogate and is measured in an embedded font;
/// see <see cref="TextStyle.FontRef"/>.
/// </exception>
/// <exception cref="InvalidOperationException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the style's size or leading is refused; see
/// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
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
    /// <b>Never null.</b> A null passed to the constructor is replaced by
    /// <see cref="TextStyle.Default"/>; see the type remarks, which also say not to pass one.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the run's text holds an unpaired surrogate and is measured in an
    /// embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public TextStyle Style { get; } = Style ?? TextStyle.Default;
}
