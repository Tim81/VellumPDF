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
/// </remarks>
public sealed class ListElement
{
    private readonly List<ListItem> _items = [];

    /// <summary>Marker style used for the list (bullet or numbering scheme).</summary>
    /// <remarks>
    /// Stored as given, including a value this enum does not name.
    /// <see cref="FormatMarker"/> then uses the bullet.
    /// </remarks>
    public ListStyle Style { get; }

    /// <summary>The items contained in this list, in render order.</summary>
    /// <remarks>
    /// Empty is allowed. A null item is kept, and the save throws when it lays the list out;
    /// see <see cref="Add(ListItem)"/>.
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
    /// the page, and nothing throws or reports it when it does. Where an item lands follows from
    /// the rules above, the override included; the measured figures are on #476, not here, because
    /// they depend on the level, on which branch the override takes and on
    /// <see cref="TextStyle.FontSize"/>. A later major version will reject a negative value.</para>
    /// <para>Of the non-finite values only positive infinity is refused, and only with nested
    /// children. <c>NaN</c> and negative infinity are accepted, and each can reach the text matrix
    /// as a token that is not a PDF number, leaving a reader no coordinate to place the item at.
    /// Neither throws nor is reported (#532).</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when a list with nested children has an
    /// indent that reaches the list's own area width, which is the page's content width narrowed
    /// by the left and right edges of <see cref="Margins"/>. Positive infinity is included; <c>NaN</c>
    /// and negative infinity are not, and neither is a flat list at any indent. The message
    /// reports an element too tall to fit and names neither this property nor the list.
    /// </exception>
    public double Indent { get; init; } = 20;

    /// <summary>Outer margins applied around the whole list block.</summary>
    /// <remarks>
    /// <b>Attention</b>: no edge is checked. Each edge is taken off the area this element is given,
    /// and the element is laid out in whatever box is left, even when that box is empty, inverted
    /// or <c>NaN</c>. A negative or non-finite edge, or edges wider than the area, can therefore
    /// make the save throw an exception about something else, write a <c>NaN</c> or <c>Infinity</c>
    /// token into the content stream, re-wrap, move or mirror the content, or leave the element off
    /// the page. The bottom edge only limits that box and adds no space before the next element,
    /// though a non-finite one can still disturb the elements placed after it. A negative or
    /// non-finite top edge can also move the elements placed after this one.
    /// <para>Do not pass a negative or non-finite edge. A later major version will refuse
    /// both.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the box the edges leave is too small for the element. The message
    /// says the element is too tall to fit on a page and does not name the margins.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when an edge leaves this element or a later one at a non-finite
    /// position, and the save writes that position outside the content stream, as a heading's
    /// bookmark or the rectangle of a link from <see cref="TextStyle.LinkUri"/>. Negative infinity
    /// can do this, and so can <c>NaN</c> on the left edge of linked text. The message says PDF
    /// does not support NaN or Infinity as a real number.
    /// </exception>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Text style applied to items that have no explicit style.</summary>
    /// <remarks>
    /// Used by a top-level item with no style of its own, and through that item by its children.
    /// Null means <see cref="TextStyle.Default"/>.
    /// </remarks>
    public TextStyle? DefaultStyle { get; init; }

    /// <summary>Creates a list with the given marker style and optional initial items.</summary>
    /// <remarks>
    /// A null <paramref name="items"/> is an empty list. A null entry inside it is kept, and the
    /// save throws when it lays the list out.
    /// <para>Do not put null in <paramref name="items"/>. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="items"/> contains <see langword="null"/>.
    /// </exception>
    public ListElement(ListStyle style = ListStyle.Unordered, IEnumerable<ListItem>? items = null)
    {
        Style = style;
        if (items is not null)
            _items.AddRange(items);
    }

    /// <summary>Appends an item to the list and returns this instance for chaining.</summary>
    /// <remarks>
    /// A null <paramref name="item"/> is stored, and the save throws when it lays out the list.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="item"/> is <see langword="null"/>.
    /// </exception>
    public ListElement Add(ListItem item) { _items.Add(item); return this; }

    /// <summary>Appends a text item with an optional text style and returns this instance for chaining.</summary>
    /// <remarks>
    /// Creates the item with <see cref="ListItem(string, TextStyle?)"/> and adds it. A null
    /// <paramref name="text"/> is stored, and the save throws when it lays out the list.
    /// <para>Do not pass null text. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this call, when <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    public ListElement Add(string text, TextStyle? style = null)
        => Add(new ListItem(text, style));

    /// <summary>
    /// Formats the marker at 1-based <paramref name="index"/>. The renderer uses this for nested
    /// ordered children as well, restarting the sequence at 1 under each parent.
    /// </summary>
    /// <remarks>
    /// Zero and negative are not refused. For those, Decimal and Roman give the number in the
    /// current culture followed by a full stop, so <c>-1</c> gives <c>-1.</c> under <c>en-US</c>
    /// and a U+2212 minus sign under <c>sv-SE</c>, and Alpha gives the full stop alone.
    /// Unordered, and a style this enumeration does not name, give the bullet for every
    /// index.
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
