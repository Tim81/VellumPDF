// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A paragraph that also registers a document bookmark (outline entry) at the
/// position where it is drawn. The bookmark title defaults to the heading text.
/// </summary>
public sealed class Heading
{
    /// <summary>Heading text (also used as the bookmark title unless <see cref="BookmarkTitle"/> is set).</summary>
    public string Text { get; }

    public TextStyle Style { get; }

    /// <summary>Outline nesting level: 0 = top-level, 1 = sub-heading, etc.</summary>
    public int Level { get; init; }

    /// <summary>
    /// Override the bookmark title. When null the heading text is used.
    /// </summary>
    public string? BookmarkTitle { get; init; }

    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    public Heading(string text, TextStyle? style = null)
    {
        Text = text;
        Style = style ?? new TextStyle { FontSize = 14 };
    }

    internal string ResolvedBookmarkTitle => BookmarkTitle ?? Text;
}
