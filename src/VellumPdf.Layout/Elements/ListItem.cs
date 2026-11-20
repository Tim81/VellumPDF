// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A single item in a <see cref="ListElement"/>, with optional nested children.
/// </summary>
/// <remarks>
/// Nesting is one level. An item reached only as a child of a child is not laid out or drawn, so
/// nothing it holds makes the save throw; see <see cref="Children"/>.
/// </remarks>
public sealed class ListItem
{
    private List<ListItem>? _children;

    /// <summary>The item's text.</summary>
    /// <remarks>
    /// <b>Never null.</b> A null passed to the constructor is stored as an empty string and
    /// remembered; see the constructor for what the save does with it. White space is drawn as on
    /// <see cref="Paragraph"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>. It is not raised for an item reached only as a
    /// child of a child.
    /// </exception>
    public string Text { get; }

    /// <summary>Optional text style; when null, the item inherits one.</summary>
    /// <remarks>
    /// On a top-level item, null means the list's <see cref="ListElement.DefaultStyle"/>, then
    /// <see cref="TextStyle.Default"/>. On a child, null means the parent item's style after that
    /// same fallback. Refusals on size, leading, font and text colour are on
    /// <see cref="TextStyle"/> and are raised from the save.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused, or a channel of its text
    /// colour is not finite; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/>
    /// and <see cref="TextStyle.Color"/>. It is not raised for an item reached only as a
    /// child of a child.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// It is not raised for an item reached only as a child of a child.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when text in this style holds an unpaired surrogate and is measured in
    /// an embedded font; see <see cref="TextStyle.FontRef"/>.
    /// It is not raised for an item reached only as a child of a child.
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
    /// are neither laid out nor drawn, so a child of a child is absent from the document. The save
    /// does look at them for one thing: a null text or a null child on such an item is reported as
    /// <see cref="LayoutDiagnosticCode.NullGrandchildText"/> or
    /// <see cref="LayoutDiagnosticCode.NullGrandchild"/>, and
    /// <see cref="Document.StrictDiagnostics"/> refuses the save. Any other content there is not
    /// reported. The save walks the items below each second-level item once, however many lists
    /// reach them, so the cost is linear in the distinct items you built there.
    /// <para>Be aware that you have to handle this yourself: flatten a tree deeper than two
    /// levels, or compose separate lists, until arbitrary nesting lands (#479).</para>
    /// <para>In an <see cref="ListStyle.Unordered"/> list a child is marked with U+25E6, the
    /// white bullet. The standard-14 fonts cannot encode it, so with a standard-14 style the
    /// child's marker is written as the character code for <c>?</c> and recorded in
    /// <see cref="Document.TextEncodingWarnings"/>. When the document draws the list it is not
    /// listed in <see cref="Document.Diagnostics"/>, because the marker is the library's text and
    /// not yours, so <see cref="Document.StrictDiagnostics"/> does not refuse a list for it. A
    /// list that your own renderer draws through a <see cref="Core.DrawContext"/> you built can
    /// list it. ZapfDingbats
    /// draws that code as a glyph other than <c>?</c>.</para>
    /// </remarks>
    public IReadOnlyList<ListItem>? Children => _children;

    /// <summary>Creates a list item with the given text and optional style.</summary>
    /// <remarks>
    /// A null <paramref name="style"/> is stored; <see cref="Style"/> says which style the item
    /// then inherits. An empty string is not null and is accepted.
    /// <para>A null <paramref name="text"/> is not refused by this call, because the call cannot
    /// know where the item will sit. <see cref="Text"/> becomes an empty string and the item is
    /// marked. On a top-level item, or a child of one, the save throws
    /// <see cref="ArgumentNullException"/> naming <c>text</c> when it lays the list out. On an item
    /// deeper than that, a child of a child, the text is never laid out and the list
    /// renders.</para>
    /// <para><b>Do not pass a null text.</b> On a child of a child it is accepted today and
    /// reported as <see cref="LayoutDiagnosticCode.NullGrandchildText"/>, which
    /// <see cref="Document.StrictDiagnostics"/> refuses. 3.0 will throw
    /// <see cref="ArgumentNullException"/> from this constructor whatever the position.
    /// <see cref="ListElement.Add(string, TextStyle?)"/> has no such leniency: its item is always
    /// top-level, so it throws from the call.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when <paramref name="text"/> is <see langword="null"/> and the item is
    /// laid out as a top-level item or as a child of one. <c>ParamName</c> is <c>text</c>. It is not
    /// raised for an item reached only as a child of a child.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the text holds an unpaired surrogate and is measured in an
    /// embedded font; see <see cref="TextStyle.FontRef"/>.
    /// It is not raised for an item reached only as a child of a child.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style's size or leading is refused, or a channel of its text
    /// colour is not finite; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/>
    /// and <see cref="TextStyle.Color"/>. It is not raised for an item reached only as a
    /// child of a child, except that with <see cref="Document.StrictDiagnostics"/> set, a null
    /// <paramref name="text"/> there throws it; see
    /// <see cref="LayoutDiagnosticCode.NullGrandchildText"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// It is not raised for an item reached only as a child of a child.
    /// </exception>
    public ListItem(string text, TextStyle? style = null)
    {
        HasNullText = text is null;
        Text = text ?? string.Empty;
        Style = style;
    }

    /// <summary>True when the constructor was given a null text.</summary>
    internal bool HasNullText { get; }

    /// <summary>Adds a nested child item. Returns this item.</summary>
    /// <remarks>
    /// Children of <paramref name="child"/> are ignored; see <see cref="Children"/>.
    /// <para>A null <paramref name="child"/> is not refused by this call, because the call cannot
    /// know where this item will sit in the list. It is not added: <see cref="Children"/> never holds
    /// a null. When this item is top-level the save throws <see cref="ArgumentNullException"/>
    /// naming <c>child</c> when it lays the list out. When this item is itself another item's
    /// child, or deeper, its children are never laid out and the list renders.</para>
    /// <para><b>Do not pass a null child.</b> Held by a child item it is skipped today and reported
    /// as <see cref="LayoutDiagnosticCode.NullGrandchild"/>, which
    /// <see cref="Document.StrictDiagnostics"/> refuses. 3.0 will throw
    /// <see cref="ArgumentNullException"/> from this call whatever the position.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="child"/> is <see langword="null"/> and this item is laid
    /// out as a top-level item. <c>ParamName</c> is <c>child</c>. It is not raised when this item is
    /// itself another item's child, whose own children are never laid out.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this call, when <see cref="Document.StrictDiagnostics"/> is
    /// set and <paramref name="child"/> is <see langword="null"/> on an item that is another
    /// item's child, or deeper; see <see cref="LayoutDiagnosticCode.NullGrandchild"/>.
    /// </exception>
    public ListItem AddChild(ListItem child)
    {
        if (child is null)
        {
            HasNullChild = true;
            return this;
        }

        _children ??= [];
        _children.Add(child);
        return this;
    }

    /// <summary>True when <see cref="AddChild(ListItem)"/> was given a null.</summary>
    internal bool HasNullChild { get; private set; }

    /// <summary>Adds a nested child item with the given text and optional style. Returns this item.</summary>
    /// <remarks>
    /// Creates the child with <see cref="ListItem(string, TextStyle?)"/> and adds it, so a null
    /// <paramref name="text"/> is handled as that constructor handles it, and the same advice
    /// applies. <b>Do not pass a null text.</b>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="text"/> is <see langword="null"/> and this item is laid
    /// out as a top-level item, which makes the new item a child. <c>ParamName</c> is <c>text</c>. It
    /// is not raised when this item is itself another item's child, which makes the new item a child
    /// of a child, and that is never laid out.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>.
    /// It is raised only when this item is laid out as a top-level item.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when the style's size or leading is refused, or a channel of its text colour
    /// is not finite; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and
    /// <see cref="TextStyle.Color"/>. It is raised only when this item is laid out as a top-level
    /// item. With <see cref="Document.StrictDiagnostics"/> set it is also raised for a null
    /// <paramref name="text"/> on an item that is itself another item's child; see
    /// <see cref="LayoutDiagnosticCode.NullGrandchildText"/>.
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
