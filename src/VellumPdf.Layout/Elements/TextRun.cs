// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// An immutable run of text with a uniform style, used to compose mixed-style paragraphs.
/// </summary>
/// <remarks>
/// Both arguments are stored without a check. A null text makes the save throw when it lays out the
/// paragraph holding this run, and so does a null style on a run whose text holds a word. Refusals
/// on the style's size also fire from the save; see <see cref="TextStyle"/>.
/// <para>Do not pass null for either argument. A later major version will throw
/// <see cref="ArgumentNullException"/> from the constructor.</para>
/// </remarks>
/// <exception cref="NullReferenceException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the text is <see langword="null"/>, or the style is null and the text
/// holds a word.
/// </exception>
/// <exception cref="ArgumentException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// this constructor, when the text is drawn in an embedded font and holds an unpaired surrogate;
/// see <see cref="TextStyle.FontRef"/>.
/// </exception>
public sealed class TextRun(string Text, TextStyle Style)
{
    /// <summary>The run's text.</summary>
    /// <remarks>Any value is stored. A null value makes the save throw; see the type remarks.</remarks>
    public string Text { get; } = Text;

    /// <summary>The run's text style.</summary>
    /// <remarks>
    /// Any value is stored. A null value makes the save throw when the run's text holds a word; see
    /// the type remarks.
    /// </remarks>
    public TextStyle Style { get; } = Style;
}
