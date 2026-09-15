// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// An immutable run of text with a uniform style, used to compose mixed-style paragraphs.
/// </summary>
public sealed class TextRun(string Text, TextStyle Style)
{
    /// <summary>The run's text.</summary>
    /// <remarks>Stored as given, including null.</remarks>
    public string Text { get; } = Text;

    /// <summary>The run's text style.</summary>
    /// <remarks>Stored as given. A null style is stored; layout then uses it as given.</remarks>
    public TextStyle Style { get; } = Style;
}
