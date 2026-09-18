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
    /// <remarks>A null value makes the save throw; see the constructor.</remarks>
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
    /// <remarks>
    /// The string is not validated: it is trimmed and written, so an ill-formed tag reaches the
    /// file. An empty or whitespace-only string is not written, and nothing is written when the
    /// document is not tagged.
    /// <para>Do not pass a tag that is not well-formed BCP 47. A later major version will refuse
    /// one.</para>
    /// </remarks>
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
    /// <para>In an <see cref="ListStyle.Unordered"/> list a child is marked with U+25E6, the
    /// white bullet. The standard-14 fonts cannot encode it, so with a standard-14 style the
    /// child's marker is drawn as <c>?</c> and recorded in
    /// <see cref="Document.TextEncodingWarnings"/>.</para>
    /// </remarks>
    public IReadOnlyList<ListItem>? Children => _children;

    /// <summary>Creates a list item with the given text and optional style.</summary>
    /// <remarks>
    /// A null <paramref name="style"/> is stored and means the list's style; see
    /// <see cref="Style"/>. A null <paramref name="text"/> is stored, and the save throws when it
    /// lays out the list.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    public ListItem(string text, TextStyle? style = null)
    {
        Text = text;
        Style = style;
    }

    /// <summary>Adds a nested child item. Returns this item.</summary>
    /// <remarks>
    /// Children of <paramref name="child"/> are ignored; see <see cref="Children"/>.
    /// A null <paramref name="child"/> is stored, and the save throws when it lays out the list.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="child"/> is <see langword="null"/>.
    /// </exception>
    public ListItem AddChild(ListItem child)
    {
        _children ??= [];
        _children.Add(child);
        return this;
    }

    /// <summary>Adds a nested child item with the given text and optional style. Returns this item.</summary>
    /// <remarks>
    /// Creates the child with <see cref="ListItem(string, TextStyle?)"/> and adds it.
    /// A null <paramref name="text"/> is stored, and the save throws when it lays out the list.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    public ListItem AddChild(string text, TextStyle? style = null)
        => AddChild(new ListItem(text, style));
}
