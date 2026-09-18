// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// An immutable run of text with a uniform style, used to compose mixed-style paragraphs.
/// </summary>
/// <remarks>
/// Both arguments are stored without a check. A null text or style makes the save throw when it
/// lays out the paragraph holding this run. Refusals on the style's size and leading also fire
/// from the save; see <see cref="TextStyle"/>.
/// <para>Do not pass null for either argument. A later major version will throw
/// <see cref="ArgumentNullException"/> from the constructor.</para>
/// </remarks>
/// <exception cref="NullReferenceException">
/// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
/// the constructor, when the text or the style is <see langword="null"/>.
/// </exception>
public sealed class TextRun(string Text, TextStyle Style)
{
    /// <summary>The run's text.</summary>
    /// <remarks>Any value is stored. A null value makes the save throw; see the type remarks.</remarks>
    public string Text { get; } = Text;

    /// <summary>The run's text style.</summary>
    /// <remarks>Any value is stored. A null value makes the save throw; see the type remarks.</remarks>
    public TextStyle Style { get; } = Style;
}
