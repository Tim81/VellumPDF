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
/// <para><b>Attention</b>: an item whose text holds only white space is laid out at the leading of
/// <see cref="TextStyle.Default"/>, as on <see cref="Paragraph"/>, while its marker is laid out in
/// the item's own style. When the marker's line does not fit where the item lands, the item is
/// placed without its marker, and nothing reports it.</para>
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
    /// and this value plus the child's marker width. One case overrides both: where the widened
    /// gutter would leave less room than the item's longest word, the gutter reverts to the
    /// unwidened figure. That is this value at the top level and twice it when nested. The
    /// override rewrites a gutter and never a marker's own inset, so no marker moves with it.
    /// <para>Every boundary below is measured against the list's own area width, which is the
    /// page's content width narrowed by the left and right edges of <see cref="Margins"/>. The top
    /// and bottom edges do not enter into it. A left or right inset narrows that area, so every
    /// boundary below moves in with it, each in proportion to where it sits.</para>
    /// <para>An indent reaching that area width is <b>not</b> refused on a flat list. The marker is
    /// drawn, the item text is discarded, and nothing reports the loss, so the list renders as a
    /// column of markers with no content. A nested list throws at the area width, as the exception
    /// records, but from <b>half</b> that width its child text is already being discarded the same
    /// silent way, because the nested gutter reaches the area width at half the indent a top-level
    /// gutter needs. The child marker, inset by this value alone, is not refused and is still
    /// drawn. Keep this value well below half the area width (#476).</para>
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
    /// throws at the area width as described above, and a flat list discards the item text. A
    /// finite negative value is not refused either.</para>
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
    /// the left and right edges of <see cref="Margins"/>, positive infinity included; a flat list at
    /// any such indent is not refused. The message reports an element too tall to fit and names
    /// neither this property nor the list.
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
    /// A null <paramref name="item"/> is refused here.
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
}
