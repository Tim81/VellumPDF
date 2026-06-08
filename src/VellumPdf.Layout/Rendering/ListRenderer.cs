// Copyright 2026 Timothy van der Ham (@Tim81)
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

    private LayoutBox _occupied;

    public ListRenderer(ListElement list, int startItem = 0)
    {
        _list = list;
        _startItem = startItem;
    }

    // ── Phase 1: Layout ───────────────────────────────────────────────────────

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

            // Layout both marker and content at the same Y (side-by-side).
            var markerResult = _items[i].Marker.Layout(itemCtx);
            var contentResult = _items[i].Content.Layout(itemCtx);

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
                var split = new ListRenderer(_list, _startItem)
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
                // Content is split — commit through this item's partial and overflow the rest
                _endItem = i + 1;
                var usedH = area.Height - remaining + itemH;
                _occupied = area.WithHeight(usedH);

                // For simplicity: treat a partial item as ending the current page at this item.
                // Build overflow starting at i.
                var overflow = new ListRenderer(_list, i) { _items = _items };
                var split = new ListRenderer(_list, _startItem)
                {
                    _items = _items,
                    _endItem = i + 1,
                    _occupied = _occupied,
                };
                return LayoutResult.Partial(_occupied, split, overflow);
            }

            y += itemH;
            remaining -= itemH;
        }

        _endItem = _items.Count;
        _occupied = area.WithHeight(area.Height - remaining);
        return LayoutResult.Full(_occupied);
    }

    // ── Phase 2: Draw ─────────────────────────────────────────────────────────

    public void Draw(DrawContext ctx)
    {
        if (_items is null) return;

        // _occupied is already the margin-deflated area from Layout; do not deflate again.
        var area = _occupied;
        var y = area.Y;

        for (var i = _startItem; i < _endItem; i++)
        {
            var (marker, content) = _items[i];

            // Re-layout to get the occupied heights at the current y position
            var itemArea = new LayoutBox(area.X, y, area.Width, area.Bottom - y);
            var itemCtx = new LayoutContext(itemArea);

            var markerResult = marker.Layout(itemCtx);
            var contentResult = content.Layout(itemCtx);

            var itemH = Math.Max(
                markerResult.OccupiedArea?.Height ?? 0,
                contentResult.OccupiedArea?.Height ?? 0);

            marker.Draw(ctx);
            content.Draw(ctx);

            y += itemH;
        }
    }

    // ── Item building ─────────────────────────────────────────────────────────

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

            // Marker paragraph: sits in the gutter (left portion of the line).
            var markerPara = new Paragraph(markerText, itemStyle)
            {
                Margins = EdgeInsets.Zero,
                Alignment = HorizontalAlignment.Left,
            };
            var markerRenderer = new ParagraphRenderer(markerPara);

            // Content paragraph: indented by _list.Indent from the left edge.
            var contentPara = new Paragraph(item.Text, itemStyle)
            {
                Margins = new EdgeInsets(0, 0, 0, indent),
                Alignment = HorizontalAlignment.Left,
            };
            var contentRenderer = new ParagraphRenderer(contentPara);

            result.Add((markerRenderer, contentRenderer));

            // Nested children
            if (item.Children is not null)
            {
                var seq = 1;
                foreach (var child in item.Children)
                {
                    var childStyle = child.Style ?? itemStyle;
                    var childMarker = _list.Style == ListStyle.Unordered
                        ? "◦"
                        : $"{seq}.";
                    seq++;

                    var childMarkerPara = new Paragraph(childMarker, childStyle)
                    {
                        Margins = new EdgeInsets(0, 0, 0, indent),
                    };
                    var childContentPara = new Paragraph(child.Text, childStyle)
                    {
                        Margins = new EdgeInsets(0, 0, 0, indent * 2),
                    };

                    result.Add((new ParagraphRenderer(childMarkerPara), new ParagraphRenderer(childContentPara)));
                }
            }
        }

        return result;
    }
}
