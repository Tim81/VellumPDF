// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>Ordered or unordered list with optional nesting (one level deep).</summary>
public enum ListStyle
{
    /// <summary>Unordered list rendered with a bullet marker.</summary>
    Unordered,

    /// <summary>Ordered list numbered with decimal digits (1., 2., 3.).</summary>
    OrderedDecimal,

    /// <summary>Ordered list numbered with lowercase letters (a., b., c.).</summary>
    OrderedAlpha,

    /// <summary>Ordered list numbered with lowercase Roman numerals (i., ii., iii.).</summary>
    OrderedRoman,
}

/// <summary>
/// A block-level list element. Items are rendered with a gutter marker
/// (bullet or sequence number) followed by indented paragraph content.
/// </summary>
public sealed class ListElement
{
    private readonly List<ListItem> _items = [];

    /// <summary>Marker style used for the list (bullet or numbering scheme).</summary>
    public ListStyle Style { get; }

    /// <summary>The items contained in this list, in render order.</summary>
    public IReadOnlyList<ListItem> Items => _items;

    /// <summary>Points of indent for each list level.</summary>
    /// <remarks>
    /// <b>Attention</b>: on a flat list, an indent at or beyond the content width is <b>not</b>
    /// refused. The marker is drawn and the item text is discarded. The list then renders as a
    /// column of bullets with no content, and nothing reports the loss. Keep the indent well
    /// below the content width (#476).
    /// <para>A list with nested children is refused at that same boundary instead.
    /// <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> about an element too tall to fit, which names
    /// neither this property nor the list. Measured on three geometries, the boundary is exactly
    /// the content width: on a 300 by 300pt page at 10pt margins an indent of 279.99 saves and
    /// <b>280</b> throws; at 50pt margins the pair is 199.99 and <b>200</b>. Positive infinity
    /// throws the same way. So the same value that silently empties a flat list stops a nested
    /// one from rendering at all.</para>
    /// <para>A marker wider than the indent does not overprint the item text. The gutter is
    /// widened to the marker's own width, per item, not to the widest marker seen so far. With
    /// roman numerals at the default style the first marker to exceed a 20-point indent is item
    /// <b>17</b>, item 18 widens further still, and items 19 to 21 are back at the plain indent.
    /// Each item's own numeral decides, and a later item widens again exactly when its own
    /// numeral needs it. One case still overprints: where widening the gutter would leave less
    /// room than the item's longest word, the gutter reverts to the indent.</para>
    /// <para>Three further inputs are accepted, and each does something different. <c>NaN</c>
    /// saves a 1,546-byte file whose item line reads <c>1 0 0 1 <b>NaN</b> 757.89 Tm</c>, and
    /// <c>NaN</c> is not a PDF number, so the coordinate a reader needs is not there. Positive
    /// infinity draws the marker and drops the item text, with no text-showing operator following
    /// it. A negative indent falls back to the marker's own width, because the gutter is the
    /// larger of the two. At the default text style a bullet marker is <b>4.2pt</b> wide, so an
    /// unordered list at an indent of -50 starts its text 4.2pt after the left margin rather than
    /// 20pt after it. An ordered list falls back less far, because every ordered marker is wider
    /// than the bullet: <c>1.</c> measures <b>10pt</b> and <c>i.</c> <b>6pt</b> at the same
    /// style. Do not read that as growth with the number. Decimal markers hold one width from
    /// <c>1.</c> to <c>9.</c>, and alphabetic and roman ones both narrow again at some items, as
    /// the paragraph above describes for roman.</para>
    /// <para>On a flat list none of those three throws and none is reported, so check the value
    /// before you set it rather than expecting the save to tell you. On a nested list, positive
    /// infinity throws, as above.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when a list with nested children has
    /// an indent at or beyond the page's content width, positive infinity included. A flat list
    /// is not refused at any indent. The message reports an element too tall to fit and names
    /// neither this property nor the list.
    /// </exception>
    public double Indent { get; init; } = 20;

    /// <summary>Outer margins applied around the whole list block.</summary>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Text style applied to items that have no explicit style.</summary>
    public TextStyle? DefaultStyle { get; init; }

    /// <summary>Creates a list with the given marker style and optional initial items.</summary>
    public ListElement(ListStyle style = ListStyle.Unordered, IEnumerable<ListItem>? items = null)
    {
        Style = style;
        if (items is not null)
            _items.AddRange(items);
    }

    /// <summary>Appends an item to the list and returns this instance for chaining.</summary>
    public ListElement Add(ListItem item) { _items.Add(item); return this; }

    /// <summary>Appends a text item with an optional text style and returns this instance for chaining.</summary>
    public ListElement Add(string text, TextStyle? style = null)
        => Add(new ListItem(text, style));

    /// <summary>Formats the marker for a top-level item at 1-based <paramref name="index"/>.</summary>
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
