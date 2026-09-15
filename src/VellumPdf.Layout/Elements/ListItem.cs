// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A single item in a <see cref="ListElement"/>, with optional nested children.
/// </summary>
/// <remarks>
/// Nesting is one level. A grandchild is ignored; see <see cref="Children"/>.
/// </remarks>
public sealed class ListItem
{
    private List<ListItem>? _children;

    /// <summary>The item's text.</summary>
    /// <remarks>Stored as given, including null.</remarks>
    public string Text { get; }

    /// <summary>Optional text style; when null the list's default style is used.</summary>
    /// <remarks>
    /// Null means the list's <see cref="ListElement.DefaultStyle"/>, then
    /// <see cref="TextStyle.Default"/>.
    /// </remarks>
    public TextStyle? Style { get; init; }

    /// <summary>
    /// Optional per-element language override (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>).
    /// When set and the document is tagged, written as <c>/Lang</c> on the struct element.
    /// </summary>
    /// <remarks>Not validated. Same as <see cref="Document.Language"/>.</remarks>
    public string? Language { get; init; }

    /// <summary>The nested child items, or null if this item has no children.</summary>
    /// <remarks>
    /// One level of nesting is supported, and only one. A child's own <see cref="Children"/>
    /// is read by <b>nothing</b>. A grandchild is not measured, not drawn and not reported, and
    /// its text is absent from the document. The scope of that claim: the renderer reads
    /// <c>item.Children</c> and takes each child's style, marker and content, and never reads
    /// <c>child.Children</c>.
    /// <para>Be aware that you have to handle this yourself: flatten a tree deeper than two
    /// levels, or compose separate lists, until arbitrary nesting lands (#479).</para>
    /// </remarks>
    public IReadOnlyList<ListItem>? Children => _children;

    /// <summary>Creates a list item with the given text and optional style.</summary>
    /// <remarks>
    /// A null <paramref name="text"/> is stored. A null <paramref name="style"/> is stored.
    /// </remarks>
    public ListItem(string text, TextStyle? style = null)
    {
        Text = text;
        Style = style;
    }

    /// <summary>Adds a nested child item. Returns this item.</summary>
    /// <remarks>
    /// A null <paramref name="child"/> is stored. A grandchild on that child is ignored; see
    /// <see cref="Children"/>.
    /// </remarks>
    public ListItem AddChild(ListItem child)
    {
        _children ??= [];
        _children.Add(child);
        return this;
    }

    /// <summary>Adds a nested child item with the given text and optional style. Returns this item.</summary>
    /// <remarks>
    /// A null <paramref name="text"/> is stored on the child. Same nesting rule as
    /// <see cref="AddChild(ListItem)"/>.
    /// </remarks>
    public ListItem AddChild(string text, TextStyle? style = null)
        => AddChild(new ListItem(text, style));
}
