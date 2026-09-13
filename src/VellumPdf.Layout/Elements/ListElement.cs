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
    /// The gutter an item's text starts at is the larger of this value and that item's own marker
    /// width, decided per item rather than once for the list. One case overrides it: where
    /// widening the gutter would leave less room than the item's longest word, the gutter reverts
    /// to this value unchanged. Those two rules decide everything below.
    /// <para><b>Attention</b>: an indent at or beyond the page's content width is <b>not</b>
    /// refused on a flat list. The marker is drawn and the item text is discarded, so the list
    /// renders as a column of markers with no content and nothing reports the loss. A list with
    /// nested children is refused at that boundary instead, as the exception records. Keep the
    /// indent positive and well below the content width (#476).</para>
    /// <para>Because the override passes this value through untouched, whatever you set can reach
    /// the content stream as a coordinate. A negative indent then places text left of the margin,
    /// off the page. <c>NaN</c> and negative infinity are worse: both are written literally into
    /// the text matrix, and neither is a PDF number, so a reader has no coordinate to place the
    /// item at (#532). A nested child takes this value directly and reaches the same place without
    /// needing the override. None of that throws and none of it is reported.</para>
    /// <para>Positive infinity is the exception to the exception: on a flat list it draws the
    /// marker and drops the item text, with no text-showing operator after it, and on a nested
    /// list it meets the content-width boundary above and throws.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when a list with nested children has an
    /// indent at or beyond the page's content width. Positive infinity is included; <c>NaN</c>
    /// and negative infinity are not, and neither is a flat list at any indent. The message
    /// reports an element too tall to fit and names neither this property nor the list.
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
