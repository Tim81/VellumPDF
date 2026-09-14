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
    /// <b>Attention</b>: an indent at or beyond the page's content width is <b>not</b> refused on
    /// a flat list. The marker is drawn and the item text is discarded, so the list renders as a
    /// column of markers with no content and nothing reports the loss. A list with nested
    /// children is refused at that boundary instead, as the exception records. Keep the indent
    /// positive and well below the content width (#476).
    /// <para>Text starts at a gutter decided per item, not once per list, measured from the
    /// list's own left edge. At the top level that gutter is the larger of this value and the
    /// item's own marker width. One level in, the marker itself starts at this value, so what has
    /// to clear it is a position rather than a width. The nested gutter is therefore the larger
    /// of twice this value and this value plus the marker's width. One case overrides both: where
    /// the widened gutter would leave less room than the item's longest word, it reverts to the
    /// unwidened figure, which is this value at the top level and twice it when nested.</para>
    /// <para>The override rewrites a gutter and never a marker's own inset, so no marker moves
    /// with it: a top-level marker sits at the margin and a nested one at the margin plus this
    /// value, in both of their branches. That margin is the page's own plus the left inset of
    /// <see cref="Margins"/>, so the left inset moves it, and any figure below that is given
    /// relative to the margin moves with it; the one given as a marker's width does not. The left
    /// inset also narrows the width the override measures against, and the right inset narrows it
    /// without moving the margin, so a wide enough inset on either side flips the branch.</para>
    /// <para>Only a negative value carries content left of the margin, and the four figures that
    /// follow need a <b>positive</b> <see cref="TextStyle.FontSize"/>. With the override firing,
    /// a top-level item reaches the page edge at minus the margin; a nested one reaches it at
    /// minus half the margin. With the override quiet, a nested one crosses the margin at minus
    /// its own marker's width and the page edge at minus that width and the margin together,
    /// while a top-level item stays on or right of the margin, because the larger-of cannot
    /// return less than the marker's width. None of it throws or is reported.</para>
    /// <para>At a size of zero or less they do not apply at all. Every word and every marker
    /// then measures zero or less, so the override can never fire, and both gutters are the
    /// larger-of alone. Apply the two rules above to find where an item lands; a marker of zero
    /// or negative width is the only input to them that has changed. Do not read a negative
    /// size as a way to outdent a list.</para>
    /// <para>The two non-finite values other than positive infinity take different routes, so one
    /// is far easier to hit. <c>NaN</c> survives the larger-of at either level and the override
    /// cannot fire against it, so it reaches the text matrix on every geometry. Negative infinity
    /// loses the larger-of at the top level and arrives only where the override fires, but wins
    /// it when nested. Neither is a PDF number, so a reader has no coordinate to place the item
    /// at, and neither throws nor is reported (#532).</para>
    /// <para>Positive infinity is the one value the branches do not separate: both yield it, so
    /// the content width goes non-positive either way. A flat list then drops the text rather
    /// than misplacing it. A nested one throws at the content-width boundary above before that
    /// loss can reach a file.</para>
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
