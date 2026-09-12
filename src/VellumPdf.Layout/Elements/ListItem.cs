// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A single item in a <see cref="ListElement"/>, with optional nested children.
/// </summary>
public sealed class ListItem
{
    private List<ListItem>? _children;

    /// <summary>The item's text.</summary>
    public string Text { get; }

    /// <summary>Optional text style; when null the list's default style is used.</summary>
    public TextStyle? Style { get; init; }

    /// <summary>
    /// Optional per-element language override (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>).
    /// When set and the document is tagged, written as <c>/Lang</c> on the struct element.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>The nested child items, or null if this item has no children.</summary>
    /// <remarks>
    /// <b>One level of nesting, and only one.</b> A child's own
    /// <see cref="Children"/> is read by nothing: a grandchild is not measured, not drawn, and
    /// not reported. Its text is simply absent from the document. Scope of that claim: the
    /// renderer reads <c>item.Children</c> and takes each child's style, marker and content, and
    /// never reads <c>child.Children</c>.
    /// <para><b>So do not build a tree deeper than two levels</b> and expect the whole of it to
    /// appear. Flatten it, or compose separate lists, until arbitrary nesting lands (#479).</para>
    /// </remarks>
    public IReadOnlyList<ListItem>? Children => _children;

    /// <summary>Creates a list item with the given text and optional style.</summary>
    public ListItem(string text, TextStyle? style = null)
    {
        Text = text;
        Style = style;
    }

    /// <summary>Adds a nested child item. Returns this item.</summary>
    public ListItem AddChild(ListItem child)
    {
        _children ??= [];
        _children.Add(child);
        return this;
    }

    /// <summary>Adds a nested child item with the given text and optional style. Returns this item.</summary>
    public ListItem AddChild(string text, TextStyle? style = null)
        => AddChild(new ListItem(text, style));
}
