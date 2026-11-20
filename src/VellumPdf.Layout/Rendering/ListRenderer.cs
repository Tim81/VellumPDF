// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Renders a <see cref="ListElement"/> by expanding each item into a marker paragraph
/// and a content paragraph, then paginating item-by-item.
/// The marker is drawn in the gutter (left of the indent); the content is indented.
/// Nested items get an additional indent level.
/// </summary>
/// <remarks>
/// Grandchildren are ignored; see <see cref="ListItem.Children"/>.
/// </remarks>
public sealed class ListRenderer : IRenderer
{
    private readonly ListElement _list;

    // Flattened (marker, content-renderer) pairs, built once during first Layout.
    private List<(ParagraphRenderer Marker, ParagraphRenderer Content)>? _items;

    // Pagination window
    private readonly int _startItem;
    private int _endItem;   // exclusive

    // When non-null, the content renderer for _startItem is replaced with this
    // overflow renderer (carries the mid-item state from the previous page).
    private readonly ParagraphRenderer? _partialContentOverrideAtStart;

    private LayoutBox _occupied;

    // Items below the second level that hold a null text or a null child. Nothing below the second
    // level is laid out or drawn, so these save; they are counted where the items are built and
    // reported from Draw, by the piece that starts at the first item.
    private int _nullGrandchildText;
    private int _nullGrandchild;

    // The height Layout offered each item it visited, indexed by the item's offset from the start
    // item. Draw offers the same height: it lays the item out a second time, a paragraph's line
    // count is a floor of the height it is given, and Bottom - y can come out one rounding step
    // short of the height Layout tracked, which drops the item's last line. Null until Layout has
    // run on this renderer or the one it was split from.
    private List<double>? _offered;

    /// <summary>Creates a renderer for the list, optionally starting at <paramref name="startItem"/> for pagination.</summary>
    /// <remarks>
    /// <paramref name="startItem"/> counts each child as an entry after its parent, so a start of 1
    /// on a list whose first item has children begins at that item's first child. A negative start
    /// throws <see cref="ArgumentOutOfRangeException"/> from <see cref="Layout"/> only when the
    /// area is not empty and the list has items. An empty area returns
    /// <see cref="LayoutResult.Nothing"/> first. A start past the last entry returns
    /// <see cref="LayoutResult.Full"/> occupying no height. A negative start on an empty list also
    /// returns <see cref="LayoutResult.Full"/> occupying no height, and <see cref="Draw"/> then
    /// throws <see cref="ArgumentOutOfRangeException"/>.
    /// A null <paramref name="list"/> is refused here.
    /// <para>Do not pass a start outside the list's entries, which count each child after its
    /// parent. 3.0 will throw from this constructor.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="list"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from <see cref="Layout"/>, when <paramref name="startItem"/> is negative, the
    /// list has items, and the area is not empty. On an empty list it is raised from
    /// <see cref="Draw"/> instead.
    /// </exception>
    public ListRenderer(ListElement list, int startItem = 0)
    {
        ArgumentNullException.ThrowIfNull(list);
        _list = list;
        _startItem = startItem;
    }

    private ListRenderer(ListElement list, int startItem, ParagraphRenderer? partialContentOverride)
    {
        _list = list;
        _startItem = startItem;
        _partialContentOverrideAtStart = partialContentOverride;
    }

    // ── Phase 1: Layout ───────────────────────────────────────────────────────

    /// <summary>Paginates the list item-by-item, splitting at item boundaries on overflow; handles mid-item splits by chaining content overflow renderers.</summary>
    /// <remarks>
    /// See the constructor for a negative or past-end start. Overflow splits at an item, or
    /// mid-item when a paragraph does. When the document calls this method, the exceptions it lists
    /// reach you from the save. A list added with <see cref="Document.Add(ListElement)"/> never
    /// starts at a negative item, so <see cref="ArgumentOutOfRangeException"/> reaches the save
    /// only from a renderer you construct with a negative start and add yourself.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The start item is negative, the list has items, and the area is not empty. The parameter
    /// name is <c>startItem</c>, the name of the constructor parameter.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An item's <see cref="TextStyle.FontSize"/> or <see cref="TextStyle.Color"/> is refused; see
    /// those members.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Text this method measures in an embedded font holds an unpaired surrogate; see
    /// <see cref="TextStyle.FontRef"/>. It is also raised, by <see cref="VellumPdf.Canvas.PdfCanvas"/>,
    /// when a <c>NaN</c> or negative-infinity <see cref="ListElement.Indent"/> or a non-finite edge of
    /// <see cref="ListElement.Margins"/> would be written; see <see cref="Document"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// An item has a null child or a null text where it is drawn; see <see cref="ListItem"/>.
    /// </exception>
    public LayoutResult Layout(LayoutContext context)
    {
        var area = context.Area.Deflate(_list.Margins);
        if (area.IsEmpty) return LayoutResult.Nothing();

        _items ??= BuildItems(area.Width, context.ListWalk ?? new ListWalkMemo());

        if (_items.Count == 0)
        {
            _endItem = 0;
            _occupied = area.WithHeight(0);
            return LayoutResult.Full(_occupied);
        }

        // Before anything indexes by the start. A negative start would also throw from _items[i], an
        // ArgumentOutOfRangeException with ParamName "index"; this check documents the start item
        // as the cause (#556).
        ArgumentOutOfRangeException.ThrowIfNegative(_startItem, "startItem");

        // Walk items and accumulate height, handling overflow.
        var y = area.Y;
        var remaining = area.Height;
        // One entry per item this call visits, indexed by i - _startItem. Sizing it to the whole list
        // allocated the full item count on every page, which is quadratic over a long list.
        var offered = new List<double>();

        for (var i = _startItem; i < _items.Count; i++)
        {
            offered.Add(remaining);
            var itemArea = new LayoutBox(area.X, y, area.Width, remaining);
            var itemCtx = context.WithArea(itemArea);

            // For the first item, use the partial content override if present.
            var contentRenderer = (i == _startItem && _partialContentOverrideAtStart is not null)
                ? _partialContentOverrideAtStart
                : _items[i].Content;
            var markerRenderer = _items[i].Marker;

            // Layout both marker and content at the same Y (side-by-side).
            var markerResult = markerRenderer.Layout(itemCtx);
            var contentResult = contentRenderer.Layout(itemCtx);

            if (markerResult.Status == LayoutResult.Outcome.Nothing
                && contentResult.Status == LayoutResult.Outcome.Nothing)
            {
                // Nothing fits
                if (i == _startItem)
                    return LayoutResult.Nothing();

                // Commit what fit so far
                _endItem = i;
                _occupied = area.WithHeight(area.Height - remaining);
                _offered = offered;
                var overflow = new ListRenderer(_list, i)
                {
                    _items = _items,
                    _nullGrandchildText = _nullGrandchildText,
                    _nullGrandchild = _nullGrandchild,
                };
                var split = new ListRenderer(_list, _startItem, _partialContentOverrideAtStart)
                {
                    _items = _items,
                    _endItem = i,
                    _occupied = _occupied,
                    _offered = offered,
                    _nullGrandchildText = _nullGrandchildText,
                    _nullGrandchild = _nullGrandchild,
                };
                return LayoutResult.Partial(_occupied, split, overflow);
            }

            // Item height = max of marker/content occupied height
            var itemH = Math.Max(
                markerResult.OccupiedArea?.Height ?? 0,
                contentResult.OccupiedArea?.Height ?? 0);

            if (contentResult.Status == LayoutResult.Outcome.Partial)
            {
                // Content is split mid-item. The split renderer draws item i partially.
                // The overflow renderer resumes item i from where the content left off.
                _endItem = i + 1;
                var usedH = area.Height - remaining + itemH;
                _occupied = area.WithHeight(usedH);
                _offered = offered;

                // The overflow for item i carries the content's OverflowRenderer.
                var contentOverflow = (ParagraphRenderer)contentResult.OverflowRenderer!;
                var overflowList = new ListRenderer(_list, i, contentOverflow)
                {
                    _items = _items,
                    _nullGrandchildText = _nullGrandchildText,
                    _nullGrandchild = _nullGrandchild,
                };

                var split = new ListRenderer(_list, _startItem, _partialContentOverrideAtStart)
                {
                    _items = _items,
                    _endItem = i + 1,
                    _occupied = _occupied,
                    _offered = offered,
                    _nullGrandchildText = _nullGrandchildText,
                    _nullGrandchild = _nullGrandchild,
                };
                return LayoutResult.Partial(_occupied, split, overflowList);
            }

            y += itemH;
            remaining -= itemH;
        }

        _endItem = _items.Count;
        _occupied = area.WithHeight(area.Height - remaining);
        _offered = offered;
        return LayoutResult.Full(_occupied);
    }

    // ── Phase 2: Draw ─────────────────────────────────────────────────────────

    /// <summary>Draws each item's marker and content, building the tagged L → LI → Lbl/LBody hierarchy when tagging is enabled.</summary>
    /// <remarks>See <see cref="IRenderer.Draw"/>.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The start item is negative and the list has no items, which <see cref="Layout"/> accepts;
    /// see the constructor.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A position written here is <c>NaN</c> or infinite, which
    /// <see cref="VellumPdf.Canvas.PdfCanvas"/> refuses (<c>ParamName</c> is null).
    /// </exception>
    public void Draw(DrawContext ctx)
    {
        if (_items is null) return;

        if (_startItem == 0 && _partialContentOverrideAtStart is null)
        {
            if (_nullGrandchildText > 0)
                ctx.Diagnostics?.Report(
                    LayoutDiagnosticCode.NullGrandchildText,
                    0,
                    "A list item below the second level has a null text and is not drawn.",
                    _nullGrandchildText,
                    perElement: true);
            if (_nullGrandchild > 0)
                ctx.Diagnostics?.Report(
                    LayoutDiagnosticCode.NullGrandchild,
                    0,
                    "A list item below the first level was given a null child, which is not drawn.",
                    _nullGrandchild,
                    perElement: true);
        }

        // _occupied is already the margin-deflated area from Layout; do not deflate again.
        var area = _occupied;
        var y = area.Y;

        // Tagged PDF: build L → LI → (Lbl + LBody → P) hierarchy.
        // Each ParagraphRenderer draws tagged content using ParentStructElem so the
        // struct elems nest correctly instead of registering at the document root.
        PdfStructElem? listElem = null;
        if (ctx.Tagged)
            listElem = new PdfStructElem("L");

        for (var i = _startItem; i < _endItem; i++)
        {
            var (marker, baseContent) = _items[i];

            // On a continuation page the start item resumes mid-content; its marker
            // was already drawn on the page where the item began, so suppress it here
            // (otherwise the bullet/number would repeat at the top of every page).
            var isContinuation = i == _startItem && _partialContentOverrideAtStart is not null;
            var content = isContinuation ? _partialContentOverrideAtStart! : baseContent;

            // Re-layout to get the occupied heights at the current y position, offering the height
            // Layout offered. Bottom - y is the same height up to a rounding step, and one step is
            // enough to drop a line (#556); the fallback is for a renderer Layout never ran on.
            var offeredHeight = _offered is { } o ? o[i - _startItem] : area.Bottom - y;
            var itemArea = new LayoutBox(area.X, y, area.Width, offeredHeight);
            var itemCtx = new LayoutContext(itemArea);

            var markerH = isContinuation ? 0 : marker.Layout(itemCtx).OccupiedArea?.Height ?? 0;
            var contentResult = content.Layout(itemCtx);

            var itemH = Math.Max(markerH, contentResult.OccupiedArea?.Height ?? 0);

            if (ctx.Tagged && listElem is not null)
            {
                // LI groups the label and body for one list item.
                var liElem = new PdfStructElem("LI");
                listElem.AddChild(liElem);

                // Lbl: the marker (bullet or number) — omitted on a continuation.
                if (!isContinuation)
                {
                    var lblElem = new PdfStructElem("Lbl");
                    liElem.AddChild(lblElem);
                    marker.StructType = "P";
                    marker.ParentStructElem = lblElem;
                }

                // LBody: the item content paragraph
                var lbodyElem = new PdfStructElem("LBody");
                liElem.AddChild(lbodyElem);
                content.StructType = "P";
                content.ParentStructElem = lbodyElem;

                if (!isContinuation)
                    DrawMarker(marker, ctx);
                content.Draw(ctx);

                // Reset for safety (layout/draw may be called multiple times in pagination)
                if (!isContinuation)
                {
                    marker.StructType = "P";
                    marker.ParentStructElem = null;
                }
                content.StructType = "P";
                content.ParentStructElem = null;
            }
            else
            {
                if (!isContinuation)
                    DrawMarker(marker, ctx);
                content.Draw(ctx);
            }

            y += itemH;
        }

        if (listElem is not null)
            ctx.RegisterStructElemTree(listElem);
    }

    // A marker is text the library wrote, not the caller's: a '?' it substitutes, such as the
    // nested
    // unordered marker in a Standard 14 font, is not a character the caller can change. What came
    // before it is accounted for first, so the marker's own substitutions are the ones skipped.
    private static void DrawMarker(ParagraphRenderer marker, DrawContext ctx)
    {
        ctx.Diagnostics?.SyncEncodingWarnings(ctx.Canvas, report: true);
        marker.Draw(ctx);
        ctx.Diagnostics?.SyncEncodingWarnings(ctx.Canvas, report: false);
    }

    // ── Item building ─────────────────────────────────────────────────────────

    /// <summary>
    /// The widest whitespace-delimited word in <paramref name="text"/>. This is the width below
    /// which <see cref="ParagraphRenderer"/> stops wrapping and starts hard-breaking words at
    /// glyph granularity, which is the behaviour the gutter must not introduce.
    /// </summary>
    private static double WidestWord(TextStyle style, string text)
    {
        // A no-break space is not a separator here, because it is not one to the wrap this bound
        // exists to predict: ParagraphRenderer.NormaliseWhitespace excludes U+00A0, U+202F and
        // U+2007 from the whitespace it collapses (#562), so a run joined by one is a single token
        // to WordWrap. Both read the one definition, NoBreakSpaces.IsNoBreakSpace. Splitting on it,
        // which String.Split does through char.IsWhiteSpace, understates the widest token and lets
        // the widened gutter hard-break a word the caller's own indent would not have.
        // The set lives in code points written as escapes, in NoBreakSpaces: an invisible literal
        // U+00A0 inverts the predicate if any tool normalises it.
        var widest = 0.0;
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var atBreak = i == text.Length
                || (!NoBreakSpaces.IsNoBreakSpace(text[i]) && char.IsWhiteSpace(text[i]));

            if (!atBreak)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;

            var w = style.MeasureString(text[start..i]);
            if (w > widest) widest = w;
            start = -1;
        }

        return widest;
    }

    private List<(ParagraphRenderer Marker, ParagraphRenderer Content)> BuildItems(
        double areaWidth, ListWalkMemo walk)
    {
        var result = new List<(ParagraphRenderer, ParagraphRenderer)>();
        var indent = _list.Indent;
        var defaultStyle = _list.DefaultStyle ?? TextStyle.Default;
        HashSet<ListItem>? holders = null;
        long nullText = 0, nullChild = 0;

        for (var i = 0; i < _list.Items.Count; i++)
        {
            var item = _list.Items[i];
            if (item.HasNullText)
                throw new ArgumentNullException("text", "A list item has a null text.");
            var itemStyle = item.Style ?? defaultStyle;
            var markerText = _list.FormatMarker(i + 1);
            var markerWidth = itemStyle.MeasureString(markerText);

            // The marker paragraph has zero margins and the content paragraph is indented by
            // _list.Indent, so a marker wider than the indent overprints the item text -- at
            // Helvetica 10pt with the default 20pt indent, the first ordered-roman marker to do
            // that is item 27, "xxvii." at 22.22pt (item 24's "xxiv." is exactly 20pt and still
            // abuts). Widen the gutter to fit the marker, per item rather than per list: widening
            // the whole list to its widest marker would move items 1 through 26, which already
            // render correctly, and leave only a ragged left edge from item 27 on as the cost of
            // the fix.
            var gutter = Math.Max(indent, markerWidth);

            // The bound is the point where ParagraphRenderer stops wrapping and starts
            // hard-breaking words, because a gutter that leaves too little does not overprint,
            // it shreds. ParagraphRenderer.Layout drops the text entirely at a non-positive
            // width, and the zero-margin marker paragraph always fits, so ListRenderer.Layout
            // never bails on the caller's behalf. Measured on a 60pt-wide page with a 58.88pt
            // marker, which leaves 1.12pt: item 38's two-letter text became one glyph per line
            // and pushed the following paragraph 360pt down the page. Widening only while the
            // widest word still fits keeps the fix from introducing a hard break that today's
            // indent does not have, and where it cannot, today's indent and the overprint it
            // carries are the lesser harm.
            if (areaWidth - gutter < WidestWord(itemStyle, item.Text))
            {
                gutter = indent;
            }

            // Marker paragraph: sits in the gutter (left portion of the line).
            var markerPara = new Paragraph(markerText, itemStyle)
            {
                Margins = EdgeInsets.Zero,
                Alignment = HorizontalAlignment.Left,
            };
            var markerRenderer = new ParagraphRenderer(markerPara) { RunOwner = "A list item" };

            // Content paragraph: indented by the (possibly widened) gutter from the left edge.
            var contentPara = new Paragraph(item.Text, itemStyle)
            {
                Margins = new EdgeInsets(0, 0, 0, gutter),
                Alignment = HorizontalAlignment.Left,
            };
            var contentRenderer = new ParagraphRenderer(contentPara) { ElementLanguage = item.Language, RunOwner = "A list item" };

            result.Add((markerRenderer, contentRenderer));

            // A null child of a top-level item is refused here, where the position is known. The
            // same null on a grandchild is counted in the walk below instead, because
            // grandchildren are neither laid out nor drawn.
            if (item.HasNullChild)
                throw new ArgumentNullException("child", "A list item has a null child.");

            // Nested children
            if (item.Children is not null)
            {
                var seq = 1;
                foreach (var child in item.Children)
                {
                    if (child.HasNullText)
                        throw new ArgumentNullException("text", "A list item has a null text.");
                    // A child's own children are neither laid out nor drawn, so a defect in them
                    // saves. What lies below this child is counted by the save's memo, which walks
                    // it once however many lists reach it, and this list adds each distinct child's
                    // counts once.
                    if (child.HasNullChild || child.Children is not null)
                    {
                        holders ??= new HashSet<ListItem>(ReferenceEqualityComparer.Instance);
                        if (holders.Add(child))
                        {
                            var (text, nulls) = walk.CountBelow(child);
                            nullText += text;
                            nullChild += nulls;
                        }
                    }

                    var childStyle = child.Style ?? itemStyle;
                    // Nested unordered items use an open-bullet "◦"; nested ordered items
                    // route through ListElement.FormatMarker so OrderedAlpha/OrderedRoman
                    // are honoured (the default was decimal regardless of the scheme).
                    var childMarker = _list.Style == ListStyle.Unordered
                        ? "◦"
                        : _list.FormatMarker(seq);
                    seq++;

                    // The nested marker is indented by `indent` and the nested content by
                    // `indent * 2`, so the nested gutter is also exactly `indent` and has the
                    // same defect as the top-level one. The form differs because the nested
                    // marker does not start at zero: it starts at `indent`, so its right edge is
                    // `indent + markerWidth` and that is what has to clear the content's left
                    // edge. Math.Max(indent * 2, markerWidth) would compare the wrong pair of
                    // numbers, measuring a width against a position. Reaching this needs a parent
                    // with 27 or more children, since nested ordered markers restart at 1 per
                    // parent rather than continuing the top-level sequence.
                    var childMarkerWidth = childStyle.MeasureString(childMarker);
                    var childGutter = Math.Max(indent * 2, indent + childMarkerWidth);
                    if (areaWidth - childGutter < WidestWord(childStyle, child.Text))
                        childGutter = indent * 2;

                    var childMarkerPara = new Paragraph(childMarker, childStyle)
                    {
                        Margins = new EdgeInsets(0, 0, 0, indent),
                    };
                    var childContentPara = new Paragraph(child.Text, childStyle)
                    {
                        Margins = new EdgeInsets(0, 0, 0, childGutter),
                    };

                    result.Add((
                        new ParagraphRenderer(childMarkerPara) { RunOwner = "A list item" },
                        new ParagraphRenderer(childContentPara) { ElementLanguage = child.Language, RunOwner = "A list item" }));
                }
            }
        }

        _nullGrandchildText = (int)Math.Min(nullText, int.MaxValue);
        _nullGrandchild = (int)Math.Min(nullChild, int.MaxValue);
        return result;
    }
}
