// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A single item in a <see cref="ListElement"/>, with optional nested children.
/// </summary>
/// <remarks>
/// Nesting is one level. An item reached only as a grandchild is not measured or drawn, so nothing
/// it holds makes the save throw; see <see cref="Children"/>.
/// </remarks>
public sealed class ListItem
{
    private List<ListItem>? _children;

    /// <summary>The item's text.</summary>
    /// <remarks>
    /// A null value makes the save throw on a top-level item or a child; see the constructor. White
    /// space is drawn as on <see cref="Paragraph"/>.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text is <see langword="null"/> on a top-level item or a child;
    /// a grandchild is ignored (see <see cref="Children"/>).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>. It is not raised for an item reached only as a
    /// grandchild.
    /// </exception>
    public string Text { get; }

    /// <summary>Optional text style; when null, the item inherits one.</summary>
    /// <remarks>
    /// On a top-level item, null means the list's <see cref="ListElement.DefaultStyle"/>, then
    /// <see cref="TextStyle.Default"/>. On a child, null means the parent item's style after that
    /// same fallback. Refusals on size, leading and font are on <see cref="TextStyle"/> and are
    /// raised from the save.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// It is not raised for an item reached only as a grandchild.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// It is not raised for an item reached only as a grandchild.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when text in this style holds an unpaired surrogate and is measured in
    /// an embedded font; see <see cref="TextStyle.FontRef"/>.
    /// It is not raised for an item reached only as a grandchild.
    /// </exception>
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
    /// child's marker is written as the character code for <c>?</c> and recorded in
    /// <see cref="Document.TextEncodingWarnings"/>. ZapfDingbats draws that code as a glyph other
    /// than <c>?</c>.</para>
    /// </remarks>
    public IReadOnlyList<ListItem>? Children => _children;

    /// <summary>Creates a list item with the given text and optional style.</summary>
    /// <remarks>
    /// A null <paramref name="style"/> is stored; <see cref="Style"/> says which style the item
    /// then inherits. A null <paramref name="text"/> is stored, and the save throws when it lays
    /// out the item as a top-level item or a child.
    /// <para>Do not pass null text. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when <paramref name="text"/> is <see langword="null"/>.
    /// It is not raised for an item reached only as a grandchild.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the text holds an unpaired surrogate and is measured in an
    /// embedded font; see <see cref="TextStyle.FontRef"/>.
    /// It is not raised for an item reached only as a grandchild.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// It is not raised for an item reached only as a grandchild.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// It is not raised for an item reached only as a grandchild.
    /// </exception>
    public ListItem(string text, TextStyle? style = null)
    {
        Text = text;
        Style = style;
    }

    /// <summary>Adds a nested child item. Returns this item.</summary>
    /// <remarks>
    /// Children of <paramref name="child"/> are ignored; see <see cref="Children"/>. A null
    /// <paramref name="child"/> is stored, and the save throws when this item is laid out as a
    /// top-level item.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="child"/> is <see langword="null"/> and this item is
    /// laid out as a top-level item.
    /// </exception>
    public ListItem AddChild(ListItem child)
    {
        _children ??= [];
        _children.Add(child);
        return this;
    }

    /// <summary>Adds a nested child item with the given text and optional style. Returns this item.</summary>
    /// <remarks>
    /// Creates the child with <see cref="ListItem(string, TextStyle?)"/> and adds it. A null
    /// <paramref name="text"/> is stored, and the save throws when it lays out the new child, which
    /// happens only when this item is laid out as a top-level item.
    /// <para>Do not pass null text. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="text"/> is <see langword="null"/>.
    /// It is raised only when this item is laid out as a top-level item.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// It is raised only when this item is laid out as a top-level item.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// It is raised only when this item is laid out as a top-level item.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value the
    /// enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// It is raised only when this item is laid out as a top-level item.
    /// </exception>
    public ListItem AddChild(string text, TextStyle? style = null)
        => AddChild(new ListItem(text, style));
}
