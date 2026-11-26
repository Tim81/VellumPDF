// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// An immutable run of text with a uniform style, used to compose mixed-style paragraphs.
/// </summary>
/// <remarks>
/// <b>A null text is refused.</b> The constructor throws <see cref="ArgumentNullException"/> naming
/// <c>Text</c>.
/// <para>A null style is stored, and refused where the paragraph is laid out, with <see
/// cref="ArgumentNullException"/> naming <c>Style</c>. It is refused only for a run that yields a
/// word, which is a character other than a space, a tab, a line break or another white-space
/// character; U+00A0, U+202F and U+2007 count as characters of a word. A run whose text is empty or
/// holds only those separators saves. <b>Do not pass a null style.</b> 3.0 refuses it from the
/// constructor, whatever the text. The saved cases are reported as <see
/// cref="LayoutDiagnosticCode.NullTextRunStyle"/> in <see cref="Document.Diagnostics"/>, and <see
/// cref="Document.StrictDiagnostics"/> refuses them.</para>
/// Refusals on size, leading, font and
/// text colour are on <see cref="TextStyle"/> and are raised from the save. A table cell built from
/// runs has its own colour check in its constructor; see the constructor of <see
/// cref="VellumPdf.Layout.Elements.Table.Cell"/> that takes runs. A space is drawn between this run
/// and the next only where the text has a space, tab, line break or other white-space character,
/// except the no-break spaces, at the boundary. Without one, the two runs hold one word; see <see
/// cref="Paragraph"/>.
/// </remarks>
/// <exception cref="ArgumentNullException">
/// The text is <see langword="null"/>. <c>ParamName</c> is <c>Text</c>, the name of the
/// constructor's parameter. A null style raises it from the save, not from the constructor, when the
/// run yields a word; <c>ParamName</c> is <c>Style</c>, and the document cannot be saved again.
/// </exception>
/// <exception cref="ArgumentException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the text holds an unpaired surrogate and is measured in an embedded font;
/// see <see cref="TextStyle.FontRef"/>. A pair split between this run and the next is joined, and
/// does not throw, when both runs share one style instance.
/// </exception>
/// <exception cref="InvalidOperationException">
/// Raised from <see
/// cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from the constructor,
/// when the style's size or leading is refused, or a channel of its text colour is not finite. A
/// table cell built from runs has its own colour check in its constructor; see the constructor of
/// <see cref="VellumPdf.Layout.Elements.Table.Cell"/> that takes runs. See <see
/// cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and <see cref="TextStyle.Color"/>.
/// It is also raised, with <see cref="Document.StrictDiagnostics"/> set, when the run has a null
/// style and yields no word; see <see cref="LayoutDiagnosticCode.NullTextRunStyle"/>. Text in this
/// run that its font cannot encode makes a strict save throw the same way; see <see
/// cref="TextStyle.FontRef"/>.
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
    /// <inheritdoc cref="Heading.Text" path="/exception[@cref='T:System.ArgumentException']"/>
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
    /// <inheritdoc cref="Heading.Style" path="/exception[@cref='T:System.InvalidOperationException' or @cref='T:System.IndexOutOfRangeException']"/>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this property, when <see cref="Document.StrictDiagnostics"/>
    /// is set and this style is null on a run that yields no word, which saves otherwise; see
    /// <see cref="LayoutDiagnosticCode.NullTextRunStyle"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
    /// the constructor or this property, by the layout of the paragraph that holds the run, when the run
    /// has text that yields a word (a non-white-space character, U+00A0 included). <c>ParamName</c> is
    /// <c>Style</c>. A run with empty or white-space-only text saves.
    /// </exception>
    public TextStyle Style { get; } = Style;
}
