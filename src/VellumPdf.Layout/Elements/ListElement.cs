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
    /// <b>Attention</b>: an indent that reaches the list's own area width is <b>not</b> refused on
    /// a flat list. The marker is drawn and the item text is discarded, so the list renders as a
    /// column of markers with no content and nothing reports the loss. That area is the page's
    /// content width narrowed by <see cref="Margins"/>, not the content width itself, so an inset
    /// on either side brings every boundary here in by its own size.
    /// <para>Nesting does not buy you out of that. It only moves it. A nested list is refused at
    /// the area width, as the exception records, but it loses its child text silently from
    /// <b>half</b> of it. The nested content gutter is twice this value, by the rule below, so the
    /// child's own width reaches zero at half the area while the child marker, inset by this value
    /// alone, still fits and is still drawn. Between those two points a nested list fails as
    /// quietly as a flat one. Keep the indent positive and well below half the area width
    /// (#476).</para>
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
    /// <para>At a <see cref="TextStyle.FontSize"/> of zero or less those four figures do not
    /// apply. Every word and every marker then measures zero or less, so nothing the override can
    /// do changes a gutter, and both are the larger-of alone. Apply the two larger-of rules above to
    /// find where an item lands; a marker of zero or negative width is the only input to them
    /// that has changed. Do not read a negative font size as a way to outdent a list.</para>
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
    /// indent that reaches the list's own area width, which is the page's content width narrowed
    /// by <see cref="Margins"/>. Positive infinity is included; <c>NaN</c>
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

    /// <summary>
    /// Formats the marker at 1-based <paramref name="index"/>. The renderer uses this for nested
    /// ordered children as well, restarting the sequence at 1 under each parent.
    /// </summary>
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
