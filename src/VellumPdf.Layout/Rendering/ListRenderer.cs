// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Renders a <see cref="ListElement"/> by expanding each item into a marker paragraph
/// and a content paragraph, then paginating item-by-item.
/// The marker is drawn in the gutter (left of the indent); the content is indented.
/// Nested items get an additional indent level.
/// </summary>
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

    /// <summary>Creates a renderer for the list, optionally starting at <paramref name="startItem"/> for pagination.</summary>
    public ListRenderer(ListElement list, int startItem = 0)
    {
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
    public LayoutResult Layout(LayoutContext context)
    {
        var area = context.Area.Deflate(_list.Margins);
        if (area.IsEmpty) return LayoutResult.Nothing();

        _items ??= BuildItems(area.Width);

        if (_items.Count == 0)
        {
            _endItem = 0;
            _occupied = area.WithHeight(0);
            return LayoutResult.Full(_occupied);
        }

        // Walk items and accumulate height, handling overflow.
        var y = area.Y;
        var remaining = area.Height;

        for (var i = _startItem; i < _items.Count; i++)
        {
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
                var overflow = new ListRenderer(_list, i) { _items = _items };
                var split = new ListRenderer(_list, _startItem, _partialContentOverrideAtStart)
                {
                    _items = _items,
                    _endItem = i,
                    _occupied = _occupied,
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

                // The overflow for item i carries the content's OverflowRenderer.
                var contentOverflow = (ParagraphRenderer)contentResult.OverflowRenderer!;
                var overflowList = new ListRenderer(_list, i, contentOverflow) { _items = _items };

                var split = new ListRenderer(_list, _startItem, _partialContentOverrideAtStart)
                {
                    _items = _items,
                    _endItem = i + 1,
                    _occupied = _occupied,
                };
                return LayoutResult.Partial(_occupied, split, overflowList);
            }

            y += itemH;
            remaining -= itemH;
        }

        _endItem = _items.Count;
        _occupied = area.WithHeight(area.Height - remaining);
        return LayoutResult.Full(_occupied);
    }

    // ── Phase 2: Draw ─────────────────────────────────────────────────────────

    /// <summary>Draws each item's marker and content, building the tagged L → LI → Lbl/LBody hierarchy when tagging is enabled.</summary>
    public void Draw(DrawContext ctx)
    {
        if (_items is null) return;

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

            // Re-layout to get the occupied heights at the current y position
            var itemArea = new LayoutBox(area.X, y, area.Width, area.Bottom - y);
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
                    marker.Draw(ctx);
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
                    marker.Draw(ctx);
                content.Draw(ctx);
            }

            y += itemH;
        }

        if (listElem is not null)
            ctx.RegisterStructElemTree(listElem);
    }

    // ── Item building ─────────────────────────────────────────────────────────

    /// <summary>
    /// The widest whitespace-delimited word in <paramref name="text"/>. This is the width below
    /// which <see cref="ParagraphRenderer"/> stops wrapping and starts hard-breaking words at
    /// glyph granularity, which is the behaviour the gutter must not introduce.
    /// </summary>
    private static double WidestWord(TextStyle style, string text)
    {
        // A non-breaking space is not a separator here, because it is not one to the wrap this
        // bound exists to predict: ParagraphRenderer.NormaliseWhitespace excludes U+00A0 from the
        // whitespace it collapses, with a comment calling it a word character, so a run joined by
        // one is a single token to WordWrap. Splitting on it, which String.Split does through
        // char.IsWhiteSpace, understates the widest token and lets the widened gutter hard-break a
        // word the caller's own indent would not have. Measured at Helvetica 10pt before this was
        // fixed: "AAAA<NBSP>AAAA" measures 56.14 whole and the bound saw 26.68, so item 38 of a
        // roman list on an 80pt page was split into "AAAA<NBSP>AAA" and "A" while the 26 items
        // whose gutter stayed at the indent, and had less room, stayed intact.
        // Written as an escape rather than as the literal ParagraphRenderer uses, because an
        // invisible literal inverts this predicate if any tool ever normalises it: a plain
        // space would then be treated as non-breaking and nothing would separate a word.
        const char nonBreakingSpace = '\u00A0';

        var widest = 0.0;
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var atBreak = i == text.Length
                || (text[i] != nonBreakingSpace && char.IsWhiteSpace(text[i]));

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

    private List<(ParagraphRenderer Marker, ParagraphRenderer Content)> BuildItems(double areaWidth)
    {
        var result = new List<(ParagraphRenderer, ParagraphRenderer)>();
        var indent = _list.Indent;
        var defaultStyle = _list.DefaultStyle ?? TextStyle.Default;

        for (var i = 0; i < _list.Items.Count; i++)
        {
            var item = _list.Items[i];
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
                gutter = indent;

            // Marker paragraph: sits in the gutter (left portion of the line).
            var markerPara = new Paragraph(markerText, itemStyle)
            {
                Margins = EdgeInsets.Zero,
                Alignment = HorizontalAlignment.Left,
            };
            var markerRenderer = new ParagraphRenderer(markerPara);

            // Content paragraph: indented by the (possibly widened) gutter from the left edge.
            var contentPara = new Paragraph(item.Text, itemStyle)
            {
                Margins = new EdgeInsets(0, 0, 0, gutter),
                Alignment = HorizontalAlignment.Left,
            };
            var contentRenderer = new ParagraphRenderer(contentPara) { ElementLanguage = item.Language };

            result.Add((markerRenderer, contentRenderer));

            // Nested children
            if (item.Children is not null)
            {
                var seq = 1;
                foreach (var child in item.Children)
                {
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

                    result.Add((new ParagraphRenderer(childMarkerPara), new ParagraphRenderer(childContentPara) { ElementLanguage = child.Language }));
                }
            }
        }

        return result;
    }
}
