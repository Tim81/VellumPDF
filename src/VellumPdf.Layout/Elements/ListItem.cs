// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A single item in a <see cref="ListElement"/>, with optional nested children.
/// </summary>
public sealed class ListItem
{
    private List<ListItem>? _children;

    public string Text { get; }
    public TextStyle? Style { get; init; }
    public IReadOnlyList<ListItem>? Children => _children;

    public ListItem(string text, TextStyle? style = null)
    {
        Text = text;
        Style = style;
    }

    public ListItem AddChild(ListItem child)
    {
        _children ??= [];
        _children.Add(child);
        return this;
    }

    public ListItem AddChild(string text, TextStyle? style = null)
        => AddChild(new ListItem(text, style));
}
