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

                    var childMarkerPara = new Paragraph(childMarker, childStyle)
                    {
                        Margins = new EdgeInsets(0, 0, 0, indent),
                    };
                    var childContentPara = new Paragraph(child.Text, childStyle)
                    {
                        Margins = new EdgeInsets(0, 0, 0, indent * 2),
                    };

                    result.Add((new ParagraphRenderer(childMarkerPara), new ParagraphRenderer(childContentPara) { ElementLanguage = child.Language }));
                }
            }
        }

        return result;
    }
}
