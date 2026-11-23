// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A single item in a <see cref="ListElement"/>, with optional nested children.
/// </summary>
/// <remarks>
/// Nesting is one level. An item reached only as a child of a child is not laid out or drawn, so
/// nothing it holds makes the save throw; see <see cref="Children"/>.
/// <para>An item holds one string, or a sequence of <see cref="TextRun"/>s that style and link
/// parts of it, as a <see cref="Paragraph"/> does; see <see cref="Runs"/>. Runs are drawn as the
/// <see cref="Paragraph"/> remarks describe, including that a run boundary draws a space only
/// where the text has one.</para>
/// </remarks>
public sealed class ListItem
{
    private List<ListItem>? _children;

    /// <summary>The item's text.</summary>
    /// <remarks>
    /// <b>Never null.</b> A null passed to the string constructor is stored as an empty string and
    /// remembered; see the constructor for what the save does with it. White space is drawn as on
    /// <see cref="Paragraph"/>.
    /// <para>On an item built from runs this is the text of every run, concatenated in order and as
    /// stored, as <see cref="Paragraph.Text"/> is. Nothing is added at a run boundary and nothing
    /// is collapsed, whatever is drawn there.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>. A pair split between two runs that share one
    /// style instance is joined and does not throw. It is not raised for an item reached only as a
    /// child of a child.
    /// </exception>
    public string Text { get; }

    /// <summary>Optional text style; when null, the item inherits one.</summary>
    /// <remarks>
    /// On a top-level item, null means the list's <see cref="ListElement.DefaultStyle"/>, then
    /// <see cref="TextStyle.Default"/>. On a child, null means the parent item's style after that
    /// same fallback. Refusals on size, leading, font and text colour are on
    /// <see cref="TextStyle"/> and are raised from the save.
    /// <para>An item that resolves to no style of its own does not take
    /// <see cref="Document.SetDefaultFont"/>. The font is then Helvetica, which is not embedded.
    /// While <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses an element that falls back to Helvetica under either
    /// claim.</para>
    /// <para>On an item built from runs this styles the marker and the children that set no style
    /// of their own, and none of this item's runs, because each run has its own style. The
    /// marker of a child item takes its own style, or its parent's. A run is a link only through
    /// its own <see cref="TextStyle.LinkUri"/>; one in this style links the marker, and the
    /// marker and text of a child that inherits it.</para>
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
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the element is
    /// written as glyph 0, or the document has a conformance claim and the element writes Helvetica
    /// or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
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

    /// <summary>
    /// A name that a link or an outside reference can jump to: the save adds a named destination at
    /// the first line of this list item, its marker line, whether the item is in the list or one of
    /// its children. <see langword="null"/>, the default, adds none.
    /// </summary>
    /// <remarks>
    /// The rule, the refusals and the position are those of <see cref="Paragraph.Anchor"/>. An item
    /// below the second level is never drawn, so an anchor on one is refused after the layout.
    /// </remarks>
    /// <inheritdoc cref="Paragraph.Anchor" path="/exception"/>
    public string? Anchor
    {
        get => _anchor;
        init => _anchor = AnchorNames.Validate(value);
    }

    private string? _anchor;

    /// <summary>The nested child items, or null if this item has no children.</summary>
    /// <remarks>
    /// One level of nesting is supported, and only one. A child's own <see cref="Children"/>
    /// are neither laid out nor drawn, so a child of a child is absent from the document. The save
    /// does look at them for one thing, in a list the document draws from its first item: a null
    /// text or a null child on such an item is reported as
    /// <see cref="LayoutDiagnosticCode.NullGrandchildText"/> or
    /// <see cref="LayoutDiagnosticCode.NullGrandchild"/>, and
    /// <see cref="Document.StrictDiagnostics"/> refuses the save. Any other content there is not
    /// reported. A list that starts at a later item, or that a renderer of yours lays out and does
    /// not draw, reports nothing, and its items are left for a list that does report. The save
    /// visits each item below the first level of a list it reports once, however many lists and
    /// second-level items reach it, so the work is linear in the items and child links you built
    /// there. An item with a null text, or a null child, is counted once in the save, under the
    /// first such list, in drawing order, that reaches it.
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

    /// <summary>
    /// Creates a list item from a sequence of runs, each with its own style. <see cref="Style"/>
    /// styles the marker and the children without a style, not the runs.
    /// </summary>
    /// <remarks>
    /// <b>A null sequence, a null run and a run with a null style are refused here.</b> The first
    /// two throw <see cref="ArgumentNullException"/> and the third <see cref="ArgumentException"/>;
    /// <c>ParamName</c> is <c>runs</c> and the message of the last two names the index of the run.
    /// <see cref="TextRun"/> stores a null style, and a paragraph saves one on a run that draws no
    /// text; this constructor refuses it whatever the run's text, so an empty run needs a style
    /// too. The refusal is made here, whatever place the item later takes in a list. An empty
    /// sequence is accepted and builds an item with one empty run, as an empty string does. The
    /// sequence is copied: changing it afterwards changes nothing. A <see langword="null"/>
    /// literal binds to the string constructor, not to this one.
    /// <para>Each run is drawn in its own style. A run is a link only through its own
    /// <see cref="TextStyle.LinkUri"/>. The annotation covers that run and also a space that opens
    /// the next run, because the space belongs to the run before it, as in a
    /// <see cref="Paragraph"/>. The marker is drawn in <see cref="Style"/>, and when that is null,
    /// in the style <see cref="Style"/> says is inherited, so set it to the style of the runs for
    /// a marker that matches them: <c>new ListItem(runs) { Style = style }</c>.</para>
    /// <para>The style of a run that draws no text, one that is empty or holds only white space,
    /// is not checked by the save: a size or colour it would refuse on a run that draws text does
    /// not throw here.</para>
    /// <para>A tagged document gets one structure element for the item content, and
    /// <see cref="Language"/> applies to all of it. <b>Per-run language is not written.</b> Do not
    /// build an item from runs in other languages expecting each to be marked as such.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="runs"/> is <see langword="null"/> or contains <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A run's <see cref="TextRun.Style"/> is <see langword="null"/>. It is also raised from
    /// <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// constructor, when a run's text holds an unpaired surrogate and is measured in an embedded
    /// font; see <see cref="TextStyle.FontRef"/>. A pair split between two runs that share one
    /// style instance is joined and does not throw. The save does not raise it for an item
    /// reached only as a child of a child.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style of a run that draws text has its size or leading
    /// refused, or a channel of its text colour is not finite; see
    /// <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and
    /// <see cref="TextStyle.Color"/>. It is not raised for an item reached only as a child of a
    /// child.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when a run's style holds a <see cref="VellumPdf.Fonts.Standard14"/>
    /// value the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>. It is not raised for an item reached only as a child of a
    /// child.
    /// </exception>
    [OverloadResolutionPriority(-1)]
    public ListItem(IEnumerable<TextRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = TextRunList.Copy(runs, nameof(runs), TextStyle.Default);
        Text = TextRunList.Concat(_runs);
    }

    /// <summary>The runs the item's text is drawn from, in order.</summary>
    /// <remarks>
    /// Never empty and never holds a null. An item built from runs returns them, each with its own
    /// style. An item built from a string returns one run of its text in <see cref="Style"/>, or
    /// in <see cref="TextStyle.Default"/> when that is null; that run does not show the style the
    /// item inherits from the list, which the list applies when it is laid out. The list is a
    /// read-only view of a copy: changing the sequence the constructor was given does not change
    /// it, and a cast of this property cannot change it either.
    /// <para><b>Per-run language is not written.</b> Every run is part of the one structure
    /// element of the item, and <see cref="Language"/> is the only language it carries.</para>
    /// </remarks>
    public IReadOnlyList<TextRun> Runs =>
        _runs?.AsReadOnly() ?? [new TextRun(Text, Style ?? TextStyle.Default)];

    private readonly List<TextRun>? _runs;

    /// <summary>
    /// The runs the content is drawn from: the item's own, or its text in
    /// <paramref name="inherited"/>, the style a string-built item takes from the list or its
    /// parent.
    /// </summary>
    internal IReadOnlyList<TextRun> ContentRuns(TextStyle inherited) =>
        _runs ?? [new TextRun(Text, inherited)];

    /// <summary>
    /// The runs the item was built from, or null for an item built from a string. It is the list
    /// itself, not a copy, so a caller must not change it; it exists so that a walk over every
    /// run allocates nothing. Removed in 3.0 (#688), with the check of what a list can draw.
    /// </summary>
    internal IReadOnlyList<TextRun>? OwnRuns => _runs;

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
