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
public sealed class ListRenderer : IRenderer, IPageBreakSource, IKeepSource, IPlacementSource
{
    private readonly ListElement _list;

    // Flattened (marker, content-renderer) pairs, built once during first Layout.
    /// <summary>
    /// An upper bound on the page continuations a placement of this list can take, or zero when no
    /// entry from the start item on had its gutter clamped (or the box leaves no width, so nothing
    /// is built).
    /// </summary>
    /// <remarks>
    /// The bound is the number of units the list holds from its start item, an entry or one wrapped
    /// line of its text, whichever is more for that entry. Every <c>Partial</c> the list returns
    /// consumes at least one unit: a split before an entry has placed at least one whole entry, and
    /// a split inside an entry has placed at least one of its lines, because
    /// <see cref="ParagraphRenderer"/> only splits when <c>maxLines</c> is positive. The piece that
    /// ends the list consumes at least one more. A <c>Nothing</c> the document then retries on the
    /// next page can only come first: the document offers a page top the area the retry was offered
    /// and the list answers the same height the same way, so the call after a <c>Nothing</c> or a
    /// <c>Partial</c>, which is at a page top, is neither. So continuations are at most one
    /// <c>Nothing</c> plus the <c>Partial</c> count, and the count is at most the units less one.
    /// </remarks>
    internal long ContinuationBound(LayoutBox offered)
    {
        if (_unclamped) return 0;
        var area = offered.Deflate(_list.Margins);
        if (area.Width <= 0) return 0;

        _items ??= BuildItems(area.Width);
        var any = false;
        long units = 0;
        for (var i = _startItem; i < _items.Count; i++)
        {
            any |= _items[i].Clamped;
            units += Math.Max(1, _items[i].Content.LineCountAt(area.Width));
        }

        return any ? units : 0;
    }

    /// <summary>
    /// The list laid out as it was before the clamp: no gutter is narrowed, so an entry whose text
    /// has no width keeps its marker and loses its text. The same object on every call.
    /// </summary>
    internal ListRenderer Unclamped() => _twin ??= new ListRenderer(_list, _startItem, _partialContentOverrideAtStart)
    {
        _unclamped = true,
        _clampSkipped = _indentClamped,
    };

    // The last answer of a continuation probe and the cursor it was made at; see
    // DocumentRenderer.
    internal bool TryGetProbe(double y, double height, out bool reaches)
    {
        reaches = _probe is { } p && p.Y == y && p.Height == height && p.Reaches;
        return _probe is { } q && q.Y == y && q.Height == height;
    }

    internal void RememberProbe(double y, double height, bool reaches) => _probe = (y, height, reaches);

    private List<(ParagraphRenderer Marker, ParagraphRenderer Content, ListItem Item, bool Clamped)>? _items;

    // Pagination window
    private readonly int _startItem;
    private int _endItem;   // exclusive

    // When non-null, the content renderer for _startItem is replaced with this
    // overflow renderer (carries the mid-item state from the previous page).
    private readonly ParagraphRenderer? _partialContentOverrideAtStart;

    private LayoutBox _occupied;

    // The number of entries whose gutter BuildItems clamped, which Draw reports when it draws the
    // list from its first entry. The pieces a split returns carry it, because the piece that
    // starts the list is the one that is drawn.
    private int _indentClamped;

    // True on the twin Unclamped returns: BuildItems then leaves every gutter as it was before the
    // clamp existed, so the twin lays the list out as it was laid out without it.
    private bool _unclamped;

    // The number of entries the clamp would have narrowed on the list this one is the unclamped
    // twin of, which Draw reports as ListIndentClampSkipped. Carried by splits like _indentClamped.
    private int _clampSkipped;

    // The twin Unclamped hands out, built once so the measuring and the drawing pass place the same
    // object, and the answer of the last continuation probe, which depends on where the cursor was.
    private ListRenderer? _twin;
    private (double Y, double Height, bool Reaches)? _probe;

    // The height Layout offered each item it visited, indexed by the item's offset from the start
    // item. Draw offers the same height: it lays the item out a second time, a paragraph's line
    // count is a floor of the height it is given, and Bottom - y can come out one rounding step
    // short of the height Layout tracked, which drops the item's last line. Null until Layout has
    // run on this renderer or the one it was split from.
    private List<double>? _offered;

    // The y each visited item starts at, indexed like _offered. It is where an item's anchor lands.
    private List<double>? _tops;

    /// <summary>Creates a renderer for the list, optionally starting at <paramref name="startItem"/> for pagination.</summary>
    /// <remarks>
    /// <paramref name="startItem"/> counts each child as an entry after its parent, so a start of 1
    /// on a list whose first item has children begins at that item's first child. A negative start
    /// throws <see cref="ArgumentOutOfRangeException"/> from <see cref="Layout"/> only when the
    /// area is not empty and the list has items. An empty area returns
    /// <see cref="LayoutResult.Nothing()"/> first. A start past the last entry returns
    /// <see cref="LayoutResult.Full"/> occupying no height. A negative start on an empty list also
    /// returns <see cref="LayoutResult.Full"/> occupying no height, and <see cref="Draw"/> then
    /// throws <see cref="ArgumentOutOfRangeException"/>.
    /// A null <paramref name="list"/> is refused here. A renderer that starts after the first
    /// entry still clamps an entry's gutter, and reports nothing; see
    /// <see cref="LayoutDiagnosticCode.ListIndentClamped"/>.
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

    // Explicit, because the renderer is a public class and an implicit member would be public API.
    // The mode belongs to the element, so a renderer added through Document.Add(IRenderer) honours
    // it as well.
    PageBreakMode IPageBreakSource.BreakBefore => _list.BreakBefore;

    bool IKeepSource.KeepWithNext => _list.KeepWithNext;

    bool IKeepSource.KeepTogether => _list.KeepTogether;

    // Explicit, as above. The list's anchor is its first placed line and an item's anchor is the
    // line it starts on, so a piece reports an item only when that item starts in it: the item a
    // continuation resumes began on an earlier page and was reported there, and the pieces that
    // follow report from the item they start at.
    void IPlacementSource.ReportPlacements(PlacementSink sink)
    {
        if (_items is null) return;

        if (_startItem == 0 && _partialContentOverrideAtStart is null && _list.Anchor is { } listAnchor)
            sink.ReportAnchor(_list, listAnchor, _occupied);

        for (var i = _startItem; i < _endItem; i++)
        {
            if (i == _startItem && _partialContentOverrideAtStart is not null) continue;
            if (_items[i].Item.Anchor is not { } anchor) continue;
            sink.ReportAnchor(
                _items[i].Item, anchor, new LayoutBox(_occupied.X, _tops![i - _startItem], _occupied.Width, 0));
        }
    }

    internal ListElement Element => _list;

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

        _items ??= BuildItems(area.Width);

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
        var tops = new List<double>();

        for (var i = _startItem; i < _items.Count; i++)
        {
            offered.Add(remaining);
            tops.Add(y);
            var itemArea = new LayoutBox(area.X, y, area.Width, remaining);
            var itemCtx = context.WithArea(itemArea);

            // For the first item, use the partial content override if present.
            var contentRenderer = (i == _startItem && _partialContentOverrideAtStart is not null)
                ? _partialContentOverrideAtStart
                : _items[i].Content;
            var markerRenderer = _items[i].Marker;

            // Layout both marker and content at the same Y (side-by-side).
            var markerResult = markerRenderer.Layout(itemCtx);

            // The text of a clamped entry was discarded before the clamp, so an entry whose marker
            // has no room was placed by that outcome: both Nothing, which splits the list before
            // the entry or, at a page top, refuses it. Giving the text a width must not turn that
            // refusal into a save that draws the text and drops the marker.
            var contentResult = _items[i].Clamped && markerResult.Status == LayoutResult.Outcome.Nothing
                ? LayoutResult.Nothing()
                : contentRenderer.Layout(itemCtx);

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
                _tops = tops;
                var overflow = new ListRenderer(_list, i)
                {
                    _items = _items,
                };
                var split = new ListRenderer(_list, _startItem, _partialContentOverrideAtStart)
                {
                    _items = _items,
                    _indentClamped = _indentClamped,
                    _clampSkipped = _clampSkipped,
                    _endItem = i,
                    _occupied = _occupied,
                    _offered = offered,
                    _tops = tops,
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
                _tops = tops;

                // The overflow for item i carries the content's OverflowRenderer.
                var contentOverflow = (ParagraphRenderer)contentResult.OverflowRenderer!;
                var overflowList = new ListRenderer(_list, i, contentOverflow)
                {
                    _items = _items,
                };

                var split = new ListRenderer(_list, _startItem, _partialContentOverrideAtStart)
                {
                    _items = _items,
                    _indentClamped = _indentClamped,
                    _clampSkipped = _clampSkipped,
                    _endItem = i + 1,
                    _occupied = _occupied,
                    _offered = offered,
                    _tops = tops,
                };
                return LayoutResult.Partial(_occupied, split, overflowList);
            }

            y += itemH;
            remaining -= itemH;
        }

        _endItem = _items.Count;
        _occupied = area.WithHeight(area.Height - remaining);
        _offered = offered;
        _tops = tops;
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

        // Only the piece that starts the list, drawn through the document's context, takes part in
        // the walk, so a list that is measured and never drawn, or that starts at a later item,
        // leaves the items it did not report for the list that does.
        if (_startItem == 0 && _partialContentOverrideAtStart is null && ctx.Diagnostics is { } sink)
        {
            ReportBelowSecondLevel(sink);
            if (_indentClamped > 0)
                sink.Report(
                    LayoutDiagnosticCode.ListIndentClamped,
                    0,
                    "A list indent left an entry's text no width beside its gutter, so the text was given a width, that of its widest word or the room right of its marker, and may overprint the marker.",
                    _indentClamped,
                    perElement: true);
            if (_clampSkipped > 0)
                sink.Report(
                    LayoutDiagnosticCode.ListIndentClampSkipped,
                    0,
                    "A list indent left an entry's text no width beside its gutter, and giving that text a width would have taken more page continuations than one element may use, so the list was laid out without it and that text is not drawn.",
                    _clampSkipped,
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
            var (marker, baseContent, _, _) = _items[i];

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
    // nested unordered marker in a Standard 14 font, is not a character the caller can change. What
    // came before it is accounted for first, so the marker's own substitutions are the ones
    // skipped.
    private static void DrawMarker(ParagraphRenderer marker, DrawContext ctx)
    {
        ctx.Diagnostics?.SyncEncodingWarnings(ctx.Canvas, report: true);
        marker.Draw(ctx);
        ctx.Diagnostics?.SyncEncodingWarnings(ctx.Canvas, report: false);
    }

    // A child's own children are neither laid out nor drawn, so a defect in them saves. The walk
    // visits each item below the first level once per save, so this list reports only the nulls
    // that no list drawn before it, and no earlier child of this one, already reached.
    private void ReportBelowSecondLevel(LayoutDiagnosticSink sink)
    {
        long nullText = 0, nullChild = 0;
        foreach (var item in _list.Items)
        {
            if (item.Children is not { } children) continue;
            foreach (var child in children)
            {
                if (!child.HasNullChild && child.Children is null) continue;
                var (text, nulls) = sink.ListWalk.Visit(child);
                nullText += text;
                nullChild += nulls;
            }
        }

        if (nullText > 0)
            sink.Report(
                LayoutDiagnosticCode.NullGrandchildText,
                0,
                "A list item below the second level has a null text and is not drawn.",
                (int)Math.Min(nullText, int.MaxValue),
                perElement: true);
        if (nullChild > 0)
            sink.Report(
                LayoutDiagnosticCode.NullGrandchild,
                0,
                "A list item below the first level was given a null child, which is not drawn.",
                (int)Math.Min(nullChild, int.MaxValue),
                perElement: true);
    }

    // ── Item building ─────────────────────────────────────────────────────────

    /// <summary>
    /// The widest whitespace-delimited word across <paramref name="runs"/>. This is the width below
    /// which <see cref="ParagraphRenderer"/> stops wrapping and starts hard-breaking words at
    /// glyph granularity, which is the behaviour the gutter must not introduce.
    /// </summary>
    /// <remarks>
    /// A word that continues over a run boundary is one word, as the paragraph's own wrap reads it,
    /// so its width is the sum of its pieces. Consecutive pieces in one style instance are measured
    /// as one string, because the paragraph draws them as one piece and a surrogate pair split
    /// between them is whole there.
    /// </remarks>
    private static double WidestWord(IReadOnlyList<TextRun> runs)
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
        var wordWidth = 0.0;
        var inWord = false;
        TextStyle? pieceStyle = null;
        var piece = new System.Text.StringBuilder();

        void FlushPiece()
        {
            if (piece.Length == 0) return;
            wordWidth += pieceStyle!.MeasureString(piece.ToString());
            piece.Clear();
        }

        void EndWord()
        {
            FlushPiece();
            if (inWord && wordWidth > widest) widest = wordWidth;
            wordWidth = 0;
            inWord = false;
        }

        foreach (var run in runs)
        {
            var text = run.Text;
            for (var i = 0; i < text.Length; i++)
            {
                if (!NoBreakSpaces.IsNoBreakSpace(text[i]) && char.IsWhiteSpace(text[i]))
                {
                    EndWord();
                    continue;
                }

                if (!ReferenceEquals(pieceStyle, run.Style))
                {
                    FlushPiece();
                    pieceStyle = run.Style;
                }

                piece.Append(text[i]);
                inWord = true;
            }
        }

        EndWord();
        return widest;
    }

    /// <summary>
    /// Narrows a gutter that would leave an entry's text no width to the one that gives the text
    /// exactly the width of its widest word.
    /// </summary>
    /// <remarks>
    /// The gutter is changed only when <paramref name="areaWidth"/> minus it is zero or negative
    /// (the text would be discarded), the entry's own marker starts inside the area, and the text
    /// has a word to measure. In every other case it is returned as given, which is what keeps a
    /// width that is positive but short of the widest word, a <c>NaN</c> gutter and an entry with
    /// no measurable word on their earlier output. The new gutter is never left of
    /// <paramref name="markerInset"/>, so the content does not start left of its own marker.
    /// </remarks>
    internal static bool TryClampGutter(
        double areaWidth, double gutter, double markerInset, double widestWord, out double clamped)
    {
        clamped = gutter;

        // Written so that a NaN in any operand makes a test false and leaves the gutter alone.
        if (!(areaWidth - gutter <= 0)) return false;
        var room = areaWidth - markerInset;
        if (!(room > 0) || !(widestWord > 0)) return false;

        // The paragraph deflates the area by the gutter, so it sees areaWidth - g, which is not
        // always the widest word again once g = areaWidth - widest has been rounded: one step short
        // makes WordWrap read the word as wider than the line and hard-break it.
        if (widestWord >= room)
        {
            clamped = markerInset;
            return true;
        }

        // The result is never left of markerInset, so no clamp is needed after the loop. The word is
        // narrower than room = fl(areaWidth - markerInset), so fl(areaWidth - word) is at least
        // fl(areaWidth - BitDecrement(room)), which is at least markerInset: the exact difference
        // sits one spacing of room above it, and rounding moves it by at most half that. The loop
        // steps only while g is above markerInset, and a double above one steps down to it at
        // worst.
        var g = areaWidth - widestWord;
        while (g > markerInset && areaWidth - g < widestWord) g = Math.BitDecrement(g);
        clamped = g;
        return true;
    }

    private List<(ParagraphRenderer Marker, ParagraphRenderer Content, ListItem Item, bool Clamped)> BuildItems(double areaWidth)
    {
        var result = new List<(ParagraphRenderer, ParagraphRenderer, ListItem, bool)>();
        var indent = _list.Indent;
        var defaultStyle = _list.DefaultStyle ?? TextStyle.Default;

        for (var i = 0; i < _list.Items.Count; i++)
        {
            var item = _list.Items[i];
            if (item.HasNullText)
                throw new ArgumentNullException("text", "A list item has a null text.");
            var itemStyle = item.Style ?? defaultStyle;
            var contentRuns = item.ContentRuns(itemStyle);
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
            var widest = WidestWord(contentRuns);
            if (areaWidth - gutter < widest)
            {
                gutter = indent;
            }

            // An indent that leaves the content no width at all discards the text and keeps the
            // marker, because ParagraphRenderer.Layout returns Nothing for a non-positive width.
            // Only that case is changed; a width that is positive but short of the widest word
            // keeps the hard-breaking it has always had.
            var clampedGutter = gutter;
            var clamped = !_unclamped && TryClampGutter(areaWidth, gutter, 0, widest, out clampedGutter);
            if (clamped)
            {
                gutter = clampedGutter;
                _indentClamped++;
            }

            // Marker paragraph: sits in the gutter (left portion of the line).
            var markerPara = new Paragraph(markerText, itemStyle)
            {
                Margins = EdgeInsets.Zero,
                Alignment = HorizontalAlignment.Left,
            };
            var markerRenderer = new ParagraphRenderer(markerPara) { RunOwner = "A list item" };

            // Content paragraph: indented by the (possibly widened) gutter from the left edge.
            var contentPara = new Paragraph(contentRuns)
            {
                Margins = new EdgeInsets(0, 0, 0, gutter),
                Alignment = HorizontalAlignment.Left,
            };
            var contentRenderer = new ParagraphRenderer(contentPara) { ElementLanguage = item.Language, RunOwner = "A list item" };

            result.Add((markerRenderer, contentRenderer, item, clamped));

            // A null child of a top-level item is refused here, where the position is known. The
            // same null on a grandchild is counted when the list is drawn instead, because
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
                    var childStyle = child.Style ?? itemStyle;
                    var childRuns = child.ContentRuns(childStyle);
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
                    var childWidest = WidestWord(childRuns);
                    if (areaWidth - childGutter < childWidest)
                        childGutter = indent * 2;

                    // The child's marker starts at `indent`, so that is the leftmost the clamped
                    // content may start.
                    var clampedChildGutter = childGutter;
                    var childClamped = !_unclamped
                        && TryClampGutter(areaWidth, childGutter, indent, childWidest, out clampedChildGutter);
                    if (childClamped)
                    {
                        childGutter = clampedChildGutter;
                        _indentClamped++;
                    }

                    var childMarkerPara = new Paragraph(childMarker, childStyle)
                    {
                        Margins = new EdgeInsets(0, 0, 0, indent),
                    };
                    var childContentPara = new Paragraph(childRuns)
                    {
                        Margins = new EdgeInsets(0, 0, 0, childGutter),
                    };

                    result.Add((
                        new ParagraphRenderer(childMarkerPara) { RunOwner = "A list item" },
                        new ParagraphRenderer(childContentPara) { ElementLanguage = child.Language, RunOwner = "A list item" },
                        child,
                        childClamped));
                }
            }
        }

        return result;
    }
}
