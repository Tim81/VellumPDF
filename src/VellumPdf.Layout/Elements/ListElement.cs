// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>Ordered or unordered list with optional nesting (one level deep).</summary>
/// <remarks>
/// A value this enumeration does not name is stored. <see cref="ListElement.FormatMarker"/> then
/// emits the bullet.
/// </remarks>
public enum ListStyle
{
    /// <summary>Unordered list rendered with a bullet marker.</summary>
    /// <remarks>
    /// Every index gives the bullet, zero and negative included. A nested child is marked with a
    /// different character; see <see cref="ListItem.Children"/>.
    /// </remarks>
    Unordered,

    /// <summary>Ordered list numbered with decimal digits (1., 2., 3.).</summary>
    /// <remarks>
    /// Zero and negative indices are formatted as numbers in the current culture, so the sign
    /// differs by culture; see <see cref="ListElement.FormatMarker"/>.
    /// </remarks>
    OrderedDecimal,

    /// <summary>Ordered list numbered with lowercase letters (a., b., c.).</summary>
    /// <remarks>Zero and negative indices give a marker that is only the full stop.</remarks>
    OrderedAlpha,

    /// <summary>Ordered list numbered with lowercase Roman numerals (i., ii., iii.).</summary>
    /// <remarks>
    /// Zero and negative indices are formatted as decimal numbers in the current culture; see
    /// <see cref="ListElement.FormatMarker"/>.
    /// </remarks>
    OrderedRoman,
}

/// <summary>
/// A block-level list element. Items are rendered with a gutter marker
/// (bullet or sequence number) followed by indented paragraph content.
/// </summary>
/// <remarks>
/// Nesting is one level, via <see cref="ListItem.Children"/>. Marker formatting of zero and
/// negative indices is on <see cref="FormatMarker"/>.
/// <para>The last item a list draws on a page keeps all of its lines: the draw pass offers each
/// item the height the layout pass did.</para>
/// <para>An item is placed when its marker and its text are both placed. When only one of them
/// fits what is left of a page, or a marker that wraps gets only some of its lines, the item moves
/// whole to the next page, as it does when neither fits. Text that has no width is not a line that
/// did not fit: it is discarded, as <see cref="Indent"/> describes, and is not reported.</para>
/// <para><b>A list is laid out as it was before the clamp of <see cref="Indent"/> (#476) and this
/// moving of items (#685) in six cases, and then none of its items is clamped or moved.</b> Each
/// item stays where it falls, with the part that fits drawn and the other part not.
/// <see cref="LayoutDiagnosticCode.ListEntryPartDropped"/> reports each such item the document
/// placed, and <see cref="LayoutDiagnosticCode.ListIndentClampSkipped"/> reports the list in the
/// last four cases. A list a renderer of yours holds reports neither, nor do its pieces. First, a
/// renderer of yours lays the list out, whatever context it passes on; see
/// <see cref="Rendering.ListRenderer.Layout"/>, which also says which items a layout of a
/// reused renderer reads. Second, no page holds one of its items,
/// because a line of its marker, or of its text at the width the clamp would give it, is taller
/// than a page. Third, laying it out the new way would take it past the continuation limit, as
/// <see cref="Indent"/> describes. Fourth, any list of the document holds an item whose marker or
/// text is in a font the document cannot draw, which is a handle another document returned or a
/// <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name. The save throws
/// where such text is drawn and saves where it is dropped at the bottom of a page, so widening or
/// moving anything ahead of the item can turn one outcome into the other. Fifth, the document
/// holds, after one of its lists, an element that can need about as many page continuations as
/// the limit allows. That element is refused or saved according to where it starts, so moving an
/// item ahead of it can turn one outcome into the other. The document reads the element's size,
/// not its layout: it counts the characters of a paragraph, heading, list item or table cell, and
/// of preformatted text with each tab counted as its <see cref="Preformatted.TabWidth"/>, because
/// a tab can become that many columns and so that many lines. It adds one for an item that holds
/// no characters and one more for each table row, and the element counts when two more than that
/// is over the limit. A page break, an image, a separator and a
/// chart never count, and neither does an element a renderer of yours holds. Sixth, the document
/// holds a renderer that is not one of this library's own, wherever it sits; see
/// <see cref="Rendering.ListRenderer.Layout"/>. In the last three cases every list of the document
/// is laid out that way, so without <see cref="Document.StrictDiagnostics"/> the document keeps
/// the outcome it had while nothing ahead of the font, the element or the renderer moves; with
/// it, a <see cref="LayoutDiagnosticCode.ListIndentClampSkipped"/> report refuses the save.</para>
/// <para><b>Attention</b>: an item whose text holds only white space is laid out at the leading of
/// <see cref="TextStyle.Default"/>, as on <see cref="Paragraph"/>, while its marker is laid out in
/// the item's own style, so the marker's line can be the one that does not fit where the item
/// lands. The rules above then apply to it.</para>
/// </remarks>
public sealed class ListElement
{
    private readonly List<ListItem> _items = [];

    /// <summary>Marker style used for the list (bullet or numbering scheme).</summary>
    /// <remarks>
    /// Stored as given, including a value <see cref="ListStyle"/> does not name.
    /// <see cref="FormatMarker"/> then uses the bullet.
    /// </remarks>
    public ListStyle Style { get; }

    /// <summary>The items contained in this list, in render order.</summary>
    /// <remarks>
    /// Empty is allowed. No item is null: <see cref="Add(ListItem)"/> and the constructor refuse a
    /// null.
    /// </remarks>
    public IReadOnlyList<ListItem> Items => _items;

    /// <summary>Points of indent for each list level.</summary>
    /// <remarks>
    /// Text starts at a gutter decided per item, not once per list, and measured from the list's
    /// own left edge. At the top level that gutter is the larger of this value and that item's own
    /// marker width. One level in, the marker itself starts at this value, so what has to be
    /// cleared is a position rather than a width, and the gutter is the larger of twice this value
    /// and this value plus the child's marker width. Two cases override both. Where the widened
    /// gutter would leave less room than the item's longest word, the gutter reverts to the
    /// unwidened figure, which is this value at the top level and twice it when nested. Where that
    /// gutter leaves the text no width at all, the clamp described below replaces it. Neither
    /// rewrites a marker's own inset, so no marker moves with them.
    /// <para>Every boundary below is measured against the list's own area width, which is the
    /// page's content width narrowed by the left and right edges of <see cref="Margins"/>. The top
    /// and bottom edges do not enter into it. A left or right inset narrows that area, so every
    /// boundary below moves in with it, each in proportion to where it sits.</para>
    /// <para>An indent reaching that area width is <b>not</b> refused on a flat list. An entry
    /// whose gutter would leave its text no width at all, which is a gutter at or beyond the area
    /// width, gets one instead: the width of its widest word, or all the room right of its
    /// marker's inset when the word is wider, where the word is broken. The content then starts at
    /// the area's right edge less that width, and never left of the marker's inset. A marker is
    /// never moved, so the text can overprint it. The entry is then placed as at any indent: when
    /// its marker or its text does not fit what is left of a page it moves to the next page. A
    /// clamped line taller than a page leaves the whole list unclamped; see
    /// <see cref="ListElement"/>. A list the document
    /// draws from its first entry reports its clamped entries as one
    /// <see cref="LayoutDiagnosticCode.ListIndentClamped"/> item.</para>
    /// <para>These keep their earlier output, and are not reported by
    /// <see cref="LayoutDiagnosticCode.ListIndentClamped"/>: a gutter that leaves the text a width,
    /// however narrow; an entry with no word that measures wider than zero, such as one holding
    /// only white space, set at font size 0 or set in Symbol or ZapfDingbats, whose text is
    /// discarded; and an entry with a run that holds text and has a non-finite font size or a
    /// non-finite colour channel, whose text is discarded and which does not make the save throw,
    /// because a width would have let the layout read that style and refuse it. So does every
    /// entry of a list that is laid out as it was before the clamp, which
    /// <see cref="ListElement"/> lists in six cases. An entry whose marker has no room at the top
    /// of a page is refused as before. A nested list throws at the area width, as the exception
    /// records. Below that, from half the area width, a child's text is clamped in the same way,
    /// because the nested gutter reaches the area width at half the indent a top-level gutter
    /// needs.</para>
    /// <para>One element may take at most 50,000 page continuations; see
    /// <see cref="Rendering.DocumentRenderer"/>. Drawing the text of a clamped entry adds lines,
    /// and moving an entry adds pages, so a list the document lays out, and whose layout could
    /// pass that limit, is laid out twice without drawing first: as it was before the clamp and the
    /// moving of entries, and as it is now. It is placed the new way only when neither reaches the
    /// limit. Otherwise it is placed as it was before both: no gutter is narrowed, an entry the
    /// clamp would have changed keeps its marker and loses its text, no entry moves, and
    /// <see cref="LayoutDiagnosticCode.ListIndentClampSkipped"/> reports it. That list then saves,
    /// or throws the limit's <see cref="InvalidOperationException"/>, as a list with no clamp does.
    /// </para>
    /// <para>Do <b>not</b> pass a negative value. It can carry content left of the margin and off
    /// the page, and a finite negative value is not reported. 3.0 will reject it.</para>
    /// <para>A non-finite indent is not checked before the layout; see <see cref="Document"/>. A
    /// nested child's marker and a flat item whose longest word does not fit beside its marker take
    /// the indent directly as their left inset, so there a <c>NaN</c> or negative-infinity indent
    /// writes a token that is not a PDF number and <see cref="VellumPdf.Canvas.PdfCanvas"/> refuses it,
    /// with <see cref="ArgumentException"/>. A flat list whose words all fit saves with a
    /// negative-infinity indent, because the gutter widens to the marker's own width, and a list with
    /// no items saves with any. <b>Do not pass</b> a non-finite indent: 3.0 refuses it.</para>
    /// <para>Positive infinity is not refused here, because it writes no invalid token. A nested list
    /// throws at the area width as described above, and a flat list clamps each entry whose text
    /// has no width, as for any indent at or beyond that width. A finite negative value is not
    /// refused either.</para>
    /// <para><b>Do not pass</b> an indent that leaves an entry's text no width beside its gutter:
    /// the text is given a width and reported as
    /// <see cref="LayoutDiagnosticCode.ListIndentClamped"/>, or left out and reported as
    /// <see cref="LayoutDiagnosticCode.ListIndentClampSkipped"/> when that would take the list past
    /// the continuation limit, as above. 3.0 will refuse the input.</para>
    /// <para>A finite negative value can put linked item text, or a linked nested marker, at a
    /// non-finite position when the gutter taken from it, added to the list's left edge, overflows,
    /// as a nested list with an indent of -1e308 and a left margin of -1e308 does. The save then
    /// throws <see cref="ArgumentException"/> for the link's rectangle; see
    /// <see cref="TextStyle.LinkUri"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save rather than from this property, by <see cref="VellumPdf.Canvas.PdfCanvas"/>,
    /// when a <c>NaN</c> or negative-infinity indent is written as an item's left inset. It is also
    /// raised when linked item text, or a linked nested marker, lands at a non-finite position. A finite negative value
    /// can cause that when the
    /// gutter taken from it, added to the list's left edge, overflows. The message says PDF does not
    /// support NaN or Infinity as a real number.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when a list with nested children has an
    /// indent that reaches the list's own area width, which is the page's content width narrowed by
    /// the left and right edges of <see cref="Margins"/>, positive infinity included. A flat list
    /// at such an indent is not refused for the indent. It is refused for two other reasons, as at
    /// any indent. An entry's marker has no room at the top of a page: the message begins "An
    /// element is too tall to fit on a single page". Or the list would pass the page-continuation
    /// limit: the message begins "A single element needed more than 50000 page continuations".
    /// Neither message names this property or the list.
    /// </exception>
    public double Indent { get; init; } = 20;

    /// <summary>Outer margins applied around the whole list block.</summary>
    /// <inheritdoc cref="Paragraph.Margins" path="/remarks"/>
    /// <inheritdoc cref="Paragraph.Margins" path="/exception"/>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Whether the list starts a new page, or the next right-hand page.</summary>
    /// <remarks>
    /// <b>An undefined value is refused.</b> A number <see cref="PageBreakMode"/> does not name
    /// makes this <c>init</c> accessor throw <see cref="ArgumentOutOfRangeException"/>. The
    /// default, <see cref="PageBreakMode.None"/>, starts the element where the previous one ended.
    /// <para>It is honoured when the list is added with <see cref="Document.Add(ListElement)"/>, or
    /// when its <see cref="Rendering.ListRenderer"/> is added with
    /// <see cref="Document.Add(IRenderer)"/> or
    /// <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>. It applies to the top of the list:
    /// the overflow of a split list never breaks again, and a renderer built to continue the list
    /// from a later item breaks like the first piece when it is added directly. See
    /// <see cref="PageBreakMode"/> for what else is ignored and how parity is counted, and
    /// <see cref="PageBreak"/> for how breaks combine.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is not one of
    /// <see cref="PageBreakMode.None"/>, <see cref="PageBreakMode.Page"/> and
    /// <see cref="PageBreakMode.Recto"/>.
    /// </exception>
    public PageBreakMode BreakBefore
    {
        get => _breakBefore;
        init => _breakBefore = PageBreakModes.Validate(value);
    }

    private PageBreakMode _breakBefore;

    /// <summary>
    /// Whether the list stays on the page the next element starts on. The default,
    /// <see langword="false"/>, places the list without looking at what follows.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused, and a request the pages cannot hold is dropped and reported, not
    /// thrown.</b> The group, the order in which requests are dropped, the diagnostic and the extra
    /// layout are those of <see cref="Paragraph.KeepWithNext"/>.
    /// <para>The flag is read from a list added with <see cref="Document.Add(ListElement)"/>, and
    /// from a <see cref="Rendering.ListRenderer"/> added with
    /// <see cref="Document.Add(IRenderer)"/> or
    /// <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>. It is ignored on the overflow of a
    /// split list.</para>
    /// </remarks>
    public bool KeepWithNext { get; init; }

    /// <summary>
    /// Whether the list moves whole to the next page instead of splitting across two, when it fits
    /// there. The default, <see langword="false"/>, splits it wherever the page ends.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused, and a list that cannot be kept whole is split and reported.</b> A
    /// list that would split below the top of a page moves to the top of the next one when it fits
    /// on an empty page. When it does not fit on an empty page, or already starts at the top of
    /// one, it splits as it would without this flag, and the save lists
    /// <see cref="LayoutDiagnosticCode.KeepTogetherRelaxed"/>, a warning, against it.
    /// <para>The flag is read from a list added with <see cref="Document.Add(ListElement)"/>, and
    /// from a <see cref="Rendering.ListRenderer"/> added with
    /// <see cref="Document.Add(IRenderer)"/> or
    /// <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>, and ignored on the overflow of a
    /// split list. The extra layout it costs is that of
    /// <see cref="Paragraph.KeepTogether"/>.</para>
    /// </remarks>
    public bool KeepTogether { get; init; }

    /// <summary>
    /// A name that a link or an outside reference can jump to: the save adds a named destination at
    /// the first placed line of this list, which is the top of its first item.
    /// <see langword="null"/>, the default, adds none.
    /// </summary>
    /// <remarks>
    /// The rule, the refusals and the position are those of <see cref="Paragraph.Anchor"/>. A list
    /// with no items has no line; its destination is at the top of the box the list would fill.
    /// </remarks>
    /// <inheritdoc cref="Paragraph.Anchor" path="/exception"/>
    public string? Anchor
    {
        get => _anchor;
        init => _anchor = AnchorNames.Validate(value);
    }

    private string? _anchor;

    /// <summary>Text style applied to items that have no explicit style.</summary>
    /// <remarks>
    /// Used by a top-level item with no style of its own, and through that item by its children.
    /// Null means <see cref="TextStyle.Default"/>. Refusals on size, leading, font and text colour
    /// are on <see cref="TextStyle"/> and are raised from the save.
    /// <para>A list with no default style does not take <see cref="Document.SetDefaultFont"/>; its
    /// items, and the marker of each, take <see cref="TextStyle.Default"/>, so each item adds two
    /// font selections, one for its marker and one for its text, to the list's single report. The
    /// font is then Helvetica, which is not embedded. While
    /// <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses an element that falls back to Helvetica under either
    /// claim.</para>
    /// </remarks>
    /// <inheritdoc cref="Heading.Style" path="/exception[@cref='T:System.InvalidOperationException' or @cref='T:System.IndexOutOfRangeException']"/>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when text in this style holds an unpaired surrogate and is measured in
    /// an embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the element is
    /// written as glyph 0, or the document has a conformance claim and the element writes Helvetica
    /// or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
    /// </exception>
    public TextStyle? DefaultStyle { get; init; }

    /// <summary>Creates a list with the given marker style and optional initial items.</summary>
    /// <remarks>
    /// A null <paramref name="items"/> is an empty list. A null entry inside it is refused. Entries before the null one are not left behind, because the list is not returned.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="items"/> contains <see langword="null"/>. <c>ParamName</c> is <c>items</c>.
    /// </exception>
    public ListElement(ListStyle style = ListStyle.Unordered, IEnumerable<ListItem>? items = null)
    {
        Style = style;
        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item is null)
                    throw new ArgumentNullException(nameof(items), "An item in the sequence is null.");
                _items.Add(item);
            }
        }
    }

    /// <summary>Appends an item to the list and returns this instance for chaining.</summary>
    /// <remarks>
    /// A null <paramref name="item"/> is refused here. A save that refuses an item added later
    /// throws from that save, not from this call. Which saves read the item is on
    /// <see cref="Rendering.ListRenderer.Layout"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="item"/> is <see langword="null"/>.
    /// </exception>
    public ListElement Add(ListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
        return this;
    }

    /// <summary>Appends a text item with an optional text style and returns this instance for chaining.</summary>
    /// <remarks>
    /// Creates the item with <see cref="ListItem(string, TextStyle?)"/> and adds it. A null
    /// <paramref name="text"/> is refused here. The item is always
    /// top-level, so unlike the <see cref="ListItem"/> constructor this call knows it will be read.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <inheritdoc cref="Paragraph.Add(string, TextStyle)" path="/exception[@cref='T:System.ArgumentException' or @cref='T:System.IndexOutOfRangeException' or @cref='T:System.InvalidOperationException']"/>
    public ListElement Add(string text, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Add(new ListItem(text, style));
    }

    /// <summary>
    /// Formats the marker at 1-based <paramref name="index"/>. The renderer uses this for nested
    /// ordered children as well, restarting the sequence at 1 under each parent.
    /// </summary>
    /// <remarks>
    /// Zero and negative are not refused. For those, Decimal and Roman give the number in the
    /// current culture followed by a full stop, so <c>-1</c> gives <c>-1.</c> under <c>en-US</c>
    /// and a U+2212 minus sign under <c>sv-SE</c>, and Alpha gives the full stop alone. Unordered,
    /// and a style <see cref="ListStyle"/> does not name, give the bullet for every index.
    /// <para>Do not pass an index below 1. A later major version will refuse one.</para>
    /// </remarks>
    public string FormatMarker(int index) => Style switch
    {
        ListStyle.Unordered => "•",          // •
        ListStyle.OrderedDecimal => $"{index}.",
        ListStyle.OrderedAlpha => $"{ToBijectiveAlpha(index)}.",
        ListStyle.OrderedRoman => $"{ToRoman(index)}.",
        _ => "•",
    };

    /// <summary>
    /// Converts a 1-based index to bijective base-26 lowercase (a..z, aa..az, ba..).
    /// Index 1→"a", 26→"z", 27→"aa", 28→"ab".
    /// </summary>
    private static string ToBijectiveAlpha(int n)
    {
        var result = new System.Text.StringBuilder();
        while (n > 0)
        {
            n--;
            result.Insert(0, (char)('a' + n % 26));
            n /= 26;
        }
        return result.ToString();
    }

    private static string ToRoman(int n)
    {
        if (n <= 0) return n.ToString();
        var result = new System.Text.StringBuilder();
        int[] vals = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] syms = ["m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i"];
        for (var i = 0; i < vals.Length; i++)
        {
            while (n >= vals[i])
            {
                result.Append(syms[i]);
                n -= vals[i];
            }
        }
        return result.ToString();
    }

    private double _spaceAfter;

    /// <summary>
    /// The gap, in points, between this list and the element the document places after it. The
    /// default, 0, leaves none.
    /// </summary>
    /// <remarks>
    /// <b>A value that is not finite or is below zero is refused.</b> This <c>init</c> accessor
    /// throws <see cref="ArgumentOutOfRangeException"/>, so the exception is raised by the
    /// statement that set the value. The rule, the keep requests, where the gap is read and its
    /// removal in 3.0 are those of <see cref="Paragraph.SpaceAfter"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is <c>NaN</c>, infinite or below zero.
    /// </exception>
    public double SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = LayoutValidation.SpaceAfter(value);
    }
}
