// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
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
public sealed class ListRenderer : IRenderer, IPageBreakSource, IKeepSource, IPlacementSource, IContinuationTwin
{
    private readonly ListElement _list;

    // A list is placed two ways. The new layout gives an entry's text a width where the indent left
    // it none (#476) and moves an entry that does not fit what is left of a page whole to the next
    // one (#685). The twin is the layout it had before both: no gutter narrowed, no entry moved.
    // The document uses the new layout only for a call it addresses to this instance, once it has
    // decided the continuation limit (#686). Under any other context, for a list that holds an
    // entry no page holds, for a list a caller laid out before the save, and for every list of a
    // document that holds an entry whose font it cannot draw or an element after a list near the
    // limit (ListLayoutRun), the list lays out as its twin. The twin, the delegation below and
    // everything that exists for them are 2.x compatibility devices, removed in 3.0 (#688), which
    // lays a list out one way.
    /// <summary>
    /// The list laid out as it was before the clamp and the moving of entries: no gutter is
    /// narrowed, so an entry whose text has no width keeps its marker and loses its text, and no
    /// entry is moved. The same object on every call.
    /// </summary>
    internal ListRenderer Unclamped()
    {
        _twin ??= new ListRenderer(_list, _startItem, _partialContentOverrideAtStart) { _unclamped = true };

        // The entries that fit no page are known once this list has built its items, which the
        // twin is asked for only after. The entries the clamp would have narrowed the twin counts
        // itself, from the same gutters.
        _twin._unplaceable = _unplaceable;
        return _twin;
    }

    IRenderer IContinuationTwin.Twin() => Unclamped();

    // One entry of the flattened list: the marker and the text paragraph of an item or a child.
    // Clamped is set when BuildItems narrowed the gutter, and UnclampedGutter is the gutter the
    // entry has without the clamp. A twin that keeps the entries an earlier layout built rebuilds
    // the text of an entry the clamp narrowed from the paragraph and that gutter, and not from the
    // list, which may have changed since.
    private readonly record struct Entry(
        ParagraphRenderer Marker, ParagraphRenderer Content, ListItem Item, bool Clamped,
        double UnclampedGutter);

    private List<Entry>? _items;

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

    // True on the twin Unclamped returns, and on every piece a split of it hands on: BuildItems
    // then leaves every gutter as it was before the clamp existed and Layout moves no entry, so the
    // twin lays the list out as it was laid out without either.
    private bool _unclamped;

    // On the twin, the number of entries the clamp would have narrowed, counted from the same
    // gutters, which Draw reports as ListIndentClampSkipped when the document chose the twin for
    // that reason. Carried by splits like _indentClamped.
    private int _clampSkipped;

    // Why a list is the twin, as far as the document is concerned. A twin that a caller's renderer
    // holds, one that the caller laid out before the save, and one a continuation probe lays out
    // were not placed by the document and report nothing. AllLists is the document-wide rule of
    // ListLayoutRun with the causes that applied added as bits: the document held an entry it
    // cannot draw, an element after a list near the limit, or a renderer of the caller's.
    private enum TwinPlacement : byte
    {
        Held = 0,
        Document,
        Limit,
        AllLists = 8,
    }

    private const byte AllListsFont = 1;
    private const byte AllListsNearLimit = 2;
    private const byte AllListsRenderer = 4;

    // How the document placed this twin, read from the context of the last Layout and carried by
    // the pieces a split hands on. Document is a list the document placed as its twin without
    // deciding it: a list that holds an entry no page holds, which reports its dropped entries.
    // Limit is the continuation limit's decision, and the three AllLists values the document-wide
    // rules of ListLayoutRun, named for the rule that applied (an entry in a font the document
    // cannot draw, an element after a list near the limit, or both); all of them also report
    // ListIndentClampSkipped.
    private TwinPlacement _placement;

    // What the list that built this twin tells it before each call: how that list was called. Set
    // by LayOutAsTwin, and read by the twin's own Layout.
    private TwinPlacement _handoff;

    // The twin Unclamped hands out, built once so the measuring and the drawing pass place the same
    // object.
    private ListRenderer? _twin;

    // Set when the last Layout call was answered by a twin: because the context was not one the
    // document addressed to this list, because an entry of the list fits no page, because the
    // document keeps every list as its twin, or because the entries were built before the save. The
    // document draws and asks for placements on the instance it called, so Draw and
    // ReportPlacements hand the call on to it. It is the twin Unclamped returns.
    private ListRenderer? _delegate;

    // Which layout built the entries. 2.3.3 builds a list's entries at its first layout, at that
    // layout's width, and keeps them for every later one, whatever document or call that is: a
    // caller who laid the list out before the save, another document, or a save before this one.
    // This list builds its own entries new and its twin builds the ones 2.3.3 builds, so a layout
    // that finds entries built by another layout than the document's own call in this run lays the
    // list out as its twin, which holds or builds the entries 2.3.3 keeps. Those are entries built
    // under another run than the one in progress (or none), and entries the twin built under a
    // call the document did not address to this list (_builtForCaller). Every call the document
    // makes within one run counts as one layout: the measuring and the drawing pass, the
    // continuation probes, the pieces of a split, a second placement of the same renderer.
    // _builtUnder is carried by splits, which share the items. Removed in 3.0 (#688).
    private ListLayoutRun? _builtUnder;

    // On a twin, whether it built its entries under a context that did not name the twin: a
    // renderer of the caller's holds the list and laid it out, or the document called the list and
    // the twin built them for it. Within one run only the first kind can make a later call the
    // document does address count as laying out entries an earlier layout built, because the
    // document's calls on an empty list go to the twin from then on (AllAsTwin, ModeFor). Entries
    // built under another run count whatever this holds (_builtUnder).
    private bool _builtForCaller;

    // On a piece a split hands on, set when the piece takes the placement of the call that made it
    // (_placement) and does not read it again from the context. A piece the document calls itself
    // is addressed to the document whether or not the list it came from was, so reading the
    // context turns a piece of a held list into a placed one. Held then stays what the call that
    // made the piece said, and is not confused with a twin nobody handed a placement.
    private bool _placementInherited;

    // On a twin, the entries the list it answers for built before the twin's first layout,
    // whichever run that was: another document's, or this one's, when a list added to the document
    // is also held by a renderer of the caller's. The twin has built none of its own then, and
    // does not build any: it takes those entries whole, whatever the list holds now, as 2.3.3
    // keeps them, with the wrap each paragraph cached. For an entry the clamp narrowed it keeps the
    // marker, which the clamp does not change, and rebuilds the text as 2.3.3 built it, without
    // the clamp, because on 2.3.3 that text had no width and was never wrapped. Let go once the
    // twin holds the entries.
    private List<Entry>? _earlier;

    // Whether the entries have been tried on an empty page, and what that found. When any entry
    // fits no page the list lays out as its twin (_wholeAsMain). _unplaceable marks the entries
    // that fit none, by index, and the twin reports them from Draw. Carried by splits, which share
    // the items.
    private bool _placeabilityKnown;
    private bool _wholeAsMain;
    private bool[]? _unplaceable;

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
    /// entry still clamps an entry's gutter, and does not report that; see
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
        // The document asks the instance it called, and a list the twin laid out is that twin's.
        // Removed in 3.0 (#688).
        if (_delegate is { } delegated)
        {
            ((IPlacementSource)delegated).ReportPlacements(sink);
            return;
        }

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
    /// mid-item when a paragraph does. An item whose marker or text does not fit what is left of a
    /// page moves whole to the next page; see <see cref="ListElement"/>.
    /// <para><b>The two fixes of #476 and #685, the clamp of an entry's text width and the moving
    /// of entries, apply only to a list the document lays out itself.</b> A list laid out through a
    /// context the document did not build for this instance is laid out as it was before them, with
    /// no gutter narrowed and no entry moved. That is a context from the public
    /// <see cref="LayoutContext"/> constructor, one from <see cref="LayoutContext.WithArea"/>, and
    /// the one a renderer of yours passes on or makes when it lays a <see cref="ListRenderer"/> out
    /// from its own <see cref="IRenderer.Layout"/>. It keeps the verdict of the page-continuation
    /// limit on such a list: 2.3.3 saved it or refused it, and it does so again. A list that holds
    /// an entry no page holds is laid out that way too, so is a list the document would otherwise
    /// take past that limit, and so is every list of a document that holds a renderer of yours, a
    /// font it cannot draw, or an element after a list that can need about as many continuations
    /// as the limit allows; see <see cref="ListElement"/>,
    /// <see cref="LayoutDiagnosticCode.ListEntryPartDropped"/> and
    /// <see cref="LayoutDiagnosticCode.ListIndentClampSkipped"/>. A list that your renderer holds
    /// reports neither: the document did not place it. That holds for the pieces of the list too,
    /// when your renderer returns the list's own result and the document calls a piece
    /// itself.</para>
    /// <para><b>A list builds its entries at its first layout and keeps them, whole and at the
    /// width that layout used, as it did.</b> The calls the document makes in one save do not
    /// count as an earlier layout of each other. When the first layout was another one, which is a
    /// layout of yours before the save, another document's, or a renderer of yours that holds the
    /// list, the document lays the list out as it was before the fixes, with those entries. That
    /// holds for a list changed since: an item or a child added after that layout is not laid out
    /// or drawn, and an entry the clamp narrowed is laid out without the clamp, from the text it
    /// had then. Such a list reports nothing, unless the document lays every list out as it was
    /// before the fixes (a renderer of yours, a font it cannot draw, an element near the limit): it
    /// then reports
    /// <see cref="LayoutDiagnosticCode.ListIndentClampSkipped"/>, and
    /// <see cref="LayoutDiagnosticCode.ListEntryPartDropped"/> for an entry placed with a part not
    /// drawn, as any list does.</para>
    /// <para><b>A document that holds a renderer of yours lays every list out as it was before the
    /// fixes.</b> That is any renderer that is not one of this library's own, added with
    /// <see cref="Document.Add(IRenderer)"/>, wherever it sits and whether or not it holds a list.
    /// A <see cref="ListRenderer"/>, a <see cref="ParagraphRenderer"/> or another renderer
    /// this library ships is the library's own, and so is the barcode renderer. The document decides
    /// this once, from what was added, before it lays anything out, so the pass that counts pages
    /// for a header or footer and the pass that draws lay each list out the same way. The library
    /// cannot read where your renderer starts or what it lays out, and a list that moved ahead of
    /// it can change whether the document saves. A list the document places this way reports
    /// <see cref="LayoutDiagnosticCode.ListIndentClampSkipped"/> and names this cause. <b>Do
    /// not</b> rely on where a list lands in such a document: 3.0 lays a list out one way, whatever
    /// called it (#688).</para>
    /// <para>When the document calls this method, the exceptions it lists reach you from the
    /// save. A list added with <see cref="Document.Add(ListElement)"/> never starts at a negative
    /// item, so <see cref="ArgumentOutOfRangeException"/> reaches the save only from a renderer
    /// you construct with a negative start and add yourself.</para>
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
        // Removed in 3.0 (#688), which lays a list out one way under every context. A context the
        // document did not address to this instance gets the twin, which is what the list was
        // before the fixes, and so does every list once the document holds an entry it cannot
        // draw. The pieces a split of the twin returns are twins, so the rest of the placement is
        // as well.
        if (!_unclamped)
        {
            var run = ListLayoutRun.Current;
            if (run is { AllAsTwin: true }) return LayOutAsTwin(context, AllListsFor(run));
            if (context.ModeFor(this) == ContinuationMode.Twin)
                return LayOutAsTwin(context, TwinPlacement.Document);

            // 2.3.3 keeps the entries an earlier layout built, at the width it used. This list's
            // own are built new, so for an earlier layout the twin holds the ones to keep.
            if (BuiltByAnEarlierLayout(run)) return LayOutAsTwin(context, TwinPlacement.Held);
        }

        _delegate = null;
        return LayOut(context);
    }

    private static TwinPlacement AllListsFor(ListLayoutRun run) =>
        (TwinPlacement)((byte)TwinPlacement.AllLists
            | (run.FoundUndrawable ? AllListsFont : 0)
            | (run.FoundNearTheLimit ? AllListsNearLimit : 0)
            | (run.FoundCallerRenderer ? AllListsRenderer : 0));

    // The 207 of a list the document-wide rule turned into its twin names every cause that applied.
    private static string AllListsMessage(TwinPlacement placement)
    {
        var causes = new List<string>(3);
        var bits = (byte)placement & ~(byte)TwinPlacement.AllLists;
        if ((bits & AllListsFont) != 0) causes.Add("a list entry in a font it cannot draw");
        if ((bits & AllListsNearLimit) != 0)
            causes.Add("an element after a list that can need about as many page continuations as one element may use");
        if ((bits & AllListsRenderer) != 0) causes.Add("a renderer of yours");
        var named = causes.Count == 1
            ? causes[0]
            : string.Join(", ", causes.GetRange(0, causes.Count - 1)) + " and " + causes[^1];
        return "The document holds " + named + ", so this list was laid out as it was before the text of its "
            + "entries was given a width and the entries that do not fit were moved to the next page.";
    }

    // The twin lays the list out and this instance answers for it afterwards: the document draws
    // and asks for placements on the instance it called, and a Full result names no renderer. The
    // twin reports only for a list the document placed, so a list a caller's renderer holds, which
    // this instance is told apart from by the context, is handed over as held.
    // Removed in 3.0 (#688).
    private LayoutResult LayOutAsTwin(LayoutContext context, TwinPlacement placement)
    {
        var twin = Unclamped();
        _delegate = twin;
        if (_items is not null && twin._items is null) twin._earlier = _items;

        twin._handoff = context.IsAddressedByDocument(this) ? placement : TwinPlacement.Held;
        return twin.Layout(context);
    }

    // True when this list or its twin holds entries that a layout built other than the document's
    // own calls in this run: under another run than this one, which is another document or no
    // document at all, or by the twin under a call the document did not address to the list, which
    // is a renderer of the caller's. A call of the document's own is one the document addressed to
    // this list or to its twin, in any pass or probe, and the list's own entries are built only
    // by such a call.
    private bool BuiltByAnEarlierLayout(ListLayoutRun? run) =>
        (_items is not null && !ReferenceEquals(_builtUnder, run))
        || (_twin is { _items: not null } twin && (!ReferenceEquals(twin._builtUnder, run) || twin._builtForCaller));

    // Whether the document called this instance with the context. The session names the instance
    // the document called, in probes as well.
    private bool IsCalledByDocument(LayoutContext context) =>
        context.Session is { } session && ReferenceEquals(session.Called, this);

    // The context of a child that this list lays out in its own mode: the new layout hands a child
    // a context addressed to it, and the twin hands it the one a narrowed area gives, which is what
    // the twin did before the document addressed anything.
    private LayoutContext ChildContext(LayoutContext context, ParagraphRenderer child, LayoutBox area) =>
        _unclamped ? context.WithArea(area) : context.Address(this, child, area);

    private LayoutResult LayOut(LayoutContext context)
    {
        var area = context.Area.Deflate(_list.Margins);
        if (area.IsEmpty) return LayoutResult.Nothing();

        // How the document placed this instance is read on every call, for the pieces the call
        // returns. A twin the document called itself was chosen by its limit decision; one a list
        // handed the call to carries what that list was told.
        if (_unclamped)
        {
            _placement = context.IsPlacedAsTwin(this) ? TwinPlacement.Limit
                : _handoff != TwinPlacement.Held ? _handoff
                : _placementInherited ? _placement
                : context.IsAddressedByDocument(this) ? TwinPlacement.Document
                : TwinPlacement.Held;
        }

        if (_items is null)
        {
            var run = ListLayoutRun.Current;
            _builtUnder = run;
            if (_earlier is { } earlier)
            {
                // 2.3.3 never reads the list again once it has built its entries, so a list that
                // was changed since has none of its new items here, whatever it holds now.
                _items = AdoptEarlier(earlier, run);
                _earlier = null;
            }
            else
            {
                _builtForCaller = _unclamped && !IsCalledByDocument(context);
                _items = BuildItems(area.Width, run);
            }
        }

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

        if (!_unclamped)
        {
            // An entry no page holds is placed where it falls, as it always was, and moving the
            // entries around it would change what is drawn of it. So the whole list keeps that
            // layout, and the twin reports the entry.
            if (!_placeabilityKnown) TryEntriesOnAnEmptyPage(context, area);
            if (_wholeAsMain) return LayOutAsTwin(context, TwinPlacement.Document);

            // The list lays out its new way, which can differ from its twin's, so the document
            // decides before it uses this result (#686). The bound covers both layouts.
            if (context.Session is { Mode: ContinuationMode.Undecided, Probing: false } session)
                session.Report(this, UnitBound(area.Width));
        }

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
            var entry = _items[i];

            // For the first item, use the partial content override if present.
            var contentRenderer = (i == _startItem && _partialContentOverrideAtStart is not null)
                ? _partialContentOverrideAtStart
                : entry.Content;
            var markerRenderer = entry.Marker;

            // Layout both marker and content at the same Y (side-by-side).
            var markerResult = markerRenderer.Layout(ChildContext(context, markerRenderer, itemArea));

            // The text of a clamped entry was discarded before the clamp, so an entry whose marker
            // has no room was placed by that outcome: both Nothing, which splits the list before
            // the entry or, at a page top, refuses it. Giving the text a width must not turn that
            // refusal into a save that draws the text and drops the marker.
            var contentResult = entry.Clamped && markerResult.Status == LayoutResult.Outcome.Nothing
                ? LayoutResult.Nothing()
                : contentRenderer.Layout(ChildContext(context, contentRenderer, itemArea));

            // An entry is placed when its marker and its content are both placed. When one of them
            // is not, because the marker returned Nothing or only some of its lines, or the content
            // returned Nothing, the entry moves whole to the next page, as an entry does when
            // neither fits. Every entry fits a page of its own here, because a list that holds one
            // that does not was handed to the twin above. At the top of a page nothing is gained by
            // moving it, and an entry that cannot be drawn keeps the part that fits, as it always
            // has; Draw reports the part that does not. Text that has no width is not a part that
            // did not fit: it is discarded, as it always was, and the clamp reports the entries it
            // can give a width. The twin moves no entry.
            var moves = !_unclamped
                && PartUnplaced(markerResult, contentResult, contentRenderer, area.Width)
                && !(i == _startItem && context.AtPageTop);

            if ((markerResult.Status == LayoutResult.Outcome.Nothing
                && contentResult.Status == LayoutResult.Outcome.Nothing) || moves)
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
                    _builtUnder = _builtUnder,
                    _unclamped = _unclamped,
                    _placement = _placement,
                    _placementInherited = _unclamped,
                    _placeabilityKnown = _placeabilityKnown,
                    _unplaceable = _unplaceable,
                };
                var split = new ListRenderer(_list, _startItem, _partialContentOverrideAtStart)
                {
                    _items = _items,
                    _builtUnder = _builtUnder,
                    _unclamped = _unclamped,
                    _placement = _placement,
                    _placementInherited = _unclamped,
                    _placeabilityKnown = _placeabilityKnown,
                    _unplaceable = _unplaceable,
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
                    _builtUnder = _builtUnder,
                    _unclamped = _unclamped,
                    _placement = _placement,
                    _placementInherited = _unclamped,
                    _placeabilityKnown = _placeabilityKnown,
                    _unplaceable = _unplaceable,
                };

                var split = new ListRenderer(_list, _startItem, _partialContentOverrideAtStart)
                {
                    _items = _items,
                    _builtUnder = _builtUnder,
                    _unclamped = _unclamped,
                    _placement = _placement,
                    _placementInherited = _unclamped,
                    _placeabilityKnown = _placeabilityKnown,
                    _unplaceable = _unplaceable,
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

    // An upper bound on the page continuations a placement of this list can take, in either
    // layout, from its start item: the number of units it holds, an entry or one wrapped line of
    // its text, whichever is more for that entry.
    //
    // Every Partial the list returns consumes at least one unit. A split before an entry has
    // placed at least one whole entry, and a split inside an entry has placed at least one of its
    // lines, because ParagraphRenderer only splits when maxLines is positive. An entry that moves
    // to the next page leaves the entries before it as the piece that ends. The piece that ends
    // the list consumes at least one more. A Nothing the document retries on the next page can
    // only come first: the document offers a page top the area the retry was offered and the list
    // answers the same height the same way, so the call after a Nothing or a Partial, which is at a
    // page top, is neither. So continuations are at most one Nothing plus the Partial count, and
    // the Partial count is at most the units less one. The twin's entries hold no more units than
    // these: an entry whose text the clamp widened has a line or more here and none there, and an
    // entry the clamp left alone is the same in both.
    private long UnitBound(double areaWidth)
    {
        // Wrapping an entry here is what its layout does first, at the width it would use, and the
        // wrap refuses nothing that BuildItems has not already refused, so it cannot raise a
        // refusal the layout would not.
        long units = 0;
        for (var i = _startItem; i < _items!.Count; i++)
            units += Math.Max(1, _items[i].Content.LineCountAt(areaWidth));
        return units;
    }

    // Lays each entry's marker and text out on a page of its own and records whether both place.
    // An entry that fits no page, because a line of its marker or of its text is taller than a
    // page, makes the whole list lay out as its twin: where such an entry falls is where it was
    // always placed, and moving the entries around it would show fewer of its lines than before.
    // A context the document built names the page a Nothing is retried on, which is that page.
    // Removed in 3.0 (#688), with the twin that keeps such a list as it was.
    //
    // This reads every entry before the first is placed, and the layout reads them one at a time,
    // as the twin does. A refusal the layout raises for an entry it reaches late, and not for one
    // before it (a style, a surrogate), would then come first here and replace the refusal the
    // twin raises where it raised it. So a refusal in this walk makes the list lay out as the
    // twin, whose own walk raises it where it always did.
    private void TryEntriesOnAnEmptyPage(LayoutContext context, LayoutBox area)
    {
        _placeabilityKnown = true;
        if (context.FreshPageArea is not { } fresh) return;

        var page = fresh.Deflate(_list.Margins);
        var freshArea = new LayoutBox(area.X, page.Y, area.Width, page.Height);
        try
        {
            for (var i = _startItem; i < _items!.Count; i++)
            {
                var entry = _items[i];

                // A paragraph keeps where its last layout put it, and Draw reads that when the
                // layout it makes for itself answers Nothing. An entry that is not placed where
                // the list lays it out must be drawn as it was, so the page of its own that is
                // tried here leaves no trace.
                var markerWas = entry.Marker.Placement;
                var contentWas = entry.Content.Placement;
                var markerFits = entry.Marker.Layout(ChildContext(context, entry.Marker, freshArea)).Status
                    == LayoutResult.Outcome.Full;

                // Text with no width is discarded, not unplaced, as PartUnplaced reads it.
                var contentFits = entry.Content.Layout(ChildContext(context, entry.Content, freshArea)).Status
                    != LayoutResult.Outcome.Nothing
                    || entry.Content.LineCountAt(area.Width) == 0;
                entry.Marker.RestorePlacement(markerWas);
                entry.Content.RestorePlacement(contentWas);
                if (markerFits && contentFits) continue;

                (_unplaceable ??= new bool[_items.Count])[i] = true;
                _wholeAsMain = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _unplaceable = null;
            _wholeAsMain = true;
        }
    }

    // True when exactly one side of an entry is not placed: the marker did not get all its lines,
    // or the text got none although it has a width. Both Nothing is the case that splits the list
    // before the entry, and text with no width is discarded, not unplaced.
    private static bool PartUnplaced(
        LayoutResult marker, LayoutResult content, ParagraphRenderer contentRenderer, double areaWidth)
    {
        var markerNothing = marker.Status == LayoutResult.Outcome.Nothing;
        var contentNothing = content.Status == LayoutResult.Outcome.Nothing;
        if (markerNothing && contentNothing) return false;

        return marker.Status != LayoutResult.Outcome.Full
            || (contentNothing && contentRenderer.LineCountAt(areaWidth) != 0);
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
        // The document draws the instance it called. When the twin laid the list out, this
        // instance answers with the twin's drawing. Removed in 3.0 (#688).
        if (_delegate is { } delegated)
        {
            delegated.Draw(ctx);
            return;
        }

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
            if (_placement is not (TwinPlacement.Held or TwinPlacement.Document) && _items.Count > 0)
                sink.Report(
                    LayoutDiagnosticCode.ListIndentClampSkipped,
                    0,
                    _placement == TwinPlacement.Limit
                        ? "Laying the list out with the text of its entries given a width, or with the entries that do not fit moved to the next page, would have taken more page continuations than one element may use, so the list was laid out as it was before both."
                        : AllListsMessage(_placement),
                    Math.Max(1, _clampSkipped),
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

        var partDropped = 0;

        // The children are laid out again here in the mode this list was laid out in. The twin was
        // laid out as the twin, and a list the document addressed was laid out the new way.
        var childMode = _unclamped ? ContinuationMode.Twin : ContinuationMode.New;
        for (var i = _startItem; i < _endItem; i++)
        {
            var marker = _items[i].Marker;
            var baseContent = _items[i].Content;

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
            var markerResult = isContinuation ? null : marker.Layout(LayoutContext.Addressed(itemArea, marker, childMode));
            var markerH = markerResult?.OccupiedArea?.Height ?? 0;
            var contentResult = content.Layout(LayoutContext.Addressed(itemArea, content, childMode));
            if (markerResult is not null
                && (PartUnplaced(markerResult, contentResult, content, area.Width)
                    || (_unplaceable is { } unplaceable && unplaceable[i])))
                partDropped++;

            var itemH = Math.Max(markerH, contentResult.OccupiedArea?.Height ?? 0);

            // A clamped entry whose line does not fit where it is placed has its text dropped, as
            // it was when the text had no width. Its paragraph has been wrapped, so drawing it
            // would write an empty text object where that left none (#685).
            var placedWithoutText = _items[i].Clamped && contentResult.Status == LayoutResult.Outcome.Nothing;

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
                if (!placedWithoutText) content.Draw(ctx);

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
                if (!placedWithoutText) content.Draw(ctx);
            }

            y += itemH;
        }

        if (listElem is not null)
            ctx.RegisterStructElemTree(listElem);

        // An entry reaches this count only at the top of a page that cannot hold both its marker
        // and its text, or in the list laid out as it was before entries moved. A twin that a
        // caller's renderer holds reports nothing: the document did not place that list.
        if (partDropped > 0 && ctx.Diagnostics is { } dropSink
            && (!_unclamped || _placement != TwinPlacement.Held))
            dropSink.Report(
                LayoutDiagnosticCode.ListEntryPartDropped,
                0,
                "A list entry was placed with its marker or its text not drawn, because it does not fit where it falls and was not moved to the next page.",
                partDropped,
                perElement: true);
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

        // The result is never left of markerInset, so no clamp is needed after the loop. The word
        // is narrower than room = fl(areaWidth - markerInset), so fl(areaWidth - word) is at least
        // fl(areaWidth - BitDecrement(room)), which is at least markerInset: the exact difference
        // sits one spacing of room above it, and rounding moves it by at most half that. The loop
        // steps only while g is above markerInset, and a double above one steps down to it at
        // worst.
        var g = areaWidth - widestWord;
        while (g > markerInset && areaWidth - g < widestWord) g = Math.BitDecrement(g);
        clamped = g;
        return true;
    }

    // Whether everything an entry would draw can be drawn in the document: its marker, which is
    // set in the style the entry inherits, and each run that holds text (ListLayoutRun.CanDraw
    // says what that asks of a style). A run with no text draws nothing, so its style is not read.
    // A white-space-only run is read although it may yield no fragment, so this is stricter than
    // the draw, in the safe direction. An item built from a string has one run, in the style it
    // inherits, so that style is the only one to read.
    // Removed in 3.0 (#688), which refuses such a font where the style is built.
    private static bool CanDrawEntry(ListLayoutRun? run, TextStyle markerStyle, ListItem item)
    {
        if (!ListLayoutRun.CanDraw(run, markerStyle)) return false;
        if (item.OwnRuns is not { } runs) return true;

        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].Text.Length == 0) continue;
            if (runs[i].Style is not { } style || !ListLayoutRun.CanDraw(run, style)) return false;
        }

        return true;
    }

    /// <summary>
    /// Whether any entry of <paramref name="list"/> holds something the document cannot draw. It
    /// reads the entries BuildItems builds, in the styles it builds them in, and allocates nothing.
    /// </summary>
    /// <remarks>
    /// A null text stops nothing here: the layout refuses it where it reaches it, and a list that
    /// is refused is refused whatever this says. Removed in 3.0 (#688).
    /// </remarks>
    internal static bool HoldsUndrawableEntry(ListElement list, ListLayoutRun run)
    {
        var defaultStyle = list.DefaultStyle ?? TextStyle.Default;
        var items = list.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var itemStyle = item.Style ?? defaultStyle;
            if (!CanDrawEntry(run, itemStyle, item)) return true;
            if (item.Children is not { } children) continue;
            for (var c = 0; c < children.Count; c++)
            {
                if (!CanDrawEntry(run, children[c].Style ?? itemStyle, children[c])) return true;
            }
        }

        return false;
    }

    // The entries an earlier layout of the list built, as 2.3.3 keeps them: the whole list as that
    // layout saw it, whatever the list holds now. An entry the clamp narrowed is not one 2.3.3
    // built. It built that entry's text with the gutter the entry has without the clamp, at the
    // width the earlier layout used, and so does this, from the text and the gutter the entry
    // recorded. The marker is the same either way. Removed in 3.0 (#688), with the twin.
    private List<Entry> AdoptEarlier(List<Entry> earlier, ListLayoutRun? run)
    {
        var items = new List<Entry>(earlier.Count);
        for (var i = 0; i < earlier.Count; i++)
        {
            var entry = earlier[i];
            // The entries here are the list's own, so Clamped says the clamp would narrow the
            // entry, which is what the twin counts.
            if (entry.Clamped) _clampSkipped++;
            if (!CanDrawEntry(run, entry.Marker, entry.Content)) run?.NoteUndrawable();
            items.Add(entry.Clamped ? entry with { Content = WithoutClamp(entry), Clamped = false } : entry);
        }

        return items;
    }

    private static ParagraphRenderer WithoutClamp(Entry entry)
    {
        var narrowed = entry.Content.Paragraph;
        var paragraph = new Paragraph(narrowed.Runs)
        {
            Margins = narrowed.Margins with { Left = entry.UnclampedGutter },
        };
        return new ParagraphRenderer(paragraph)
        {
            ElementLanguage = entry.Content.ElementLanguage,
            RunOwner = entry.Content.RunOwner,
        };
    }

    // The same question for an entry already built: its marker is set in the style the entry
    // inherits and each run of its text that holds any is read, which is what the other overload
    // reads from the item.
    internal static bool CanDrawEntry(ListLayoutRun? run, ParagraphRenderer marker, ParagraphRenderer content)
    {
        if (!ListLayoutRun.CanDraw(run, marker.Paragraph.Runs[0].Style)) return false;
        var runs = content.Paragraph.Runs;
        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].Text.Length == 0) continue;
            if (!ListLayoutRun.CanDraw(run, runs[i].Style)) return false;
        }

        return true;
    }

    /// <summary>
    /// The times this thread built the entries of a list, which a test reads to see that a piece of
    /// a list does not build them again. Removed in 3.0 (#688), with the twin.
    /// </summary>
    [ThreadStatic]
    internal static int BuildCalls;

    private List<Entry> BuildItems(double areaWidth, ListLayoutRun? run)
    {
        BuildCalls++;
        var result = new List<Entry>();
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
            // keeps the hard-breaking it has always had. That Nothing is also why no run's style
            // was read: the wrap and the line-height walk come after it. Giving the text a width
            // starts them, so an entry is clamped only when every run that holds text passes the
            // walk's checks (a finite font size, a finite colour). The content paragraph has no tab
            // stops, so the leader and stop checks cannot fire, and a null style is refused by
            // ListItem; a surrogate the wrap refuses in an embedded font is one WidestWord has
            // already measured.
            // The twin asks the same question and only counts the answer, which is what it reports
            // when the document chose it. The gutter test comes first because it is the one that
            // fails for almost every entry.
            if (!CanDrawEntry(run, itemStyle, item)) run?.NoteUndrawable();
            var unclampedGutter = gutter;
            var wouldClamp = TryClampGutter(areaWidth, gutter, 0, widest, out var clampedGutter)
                && LayoutValidation.AreRunStylesAccepted(contentRuns);
            var clamped = !_unclamped && wouldClamp;
            if (clamped)
            {
                gutter = clampedGutter;
                _indentClamped++;
            }
            else if (wouldClamp)
            {
                _clampSkipped++;
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

            result.Add(new Entry(markerRenderer, contentRenderer, item, clamped, unclampedGutter));

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
                    if (!CanDrawEntry(run, childStyle, child)) run?.NoteUndrawable();
                    var unclampedChildGutter = childGutter;
                    var childWouldClamp = TryClampGutter(
                            areaWidth, childGutter, indent, childWidest, out var clampedChildGutter)
                        && LayoutValidation.AreRunStylesAccepted(childRuns);
                    var childClamped = !_unclamped && childWouldClamp;
                    if (childClamped)
                    {
                        childGutter = clampedChildGutter;
                        _indentClamped++;
                    }
                    else if (childWouldClamp)
                    {
                        _clampSkipped++;
                    }

                    var childMarkerPara = new Paragraph(childMarker, childStyle)
                    {
                        Margins = new EdgeInsets(0, 0, 0, indent),
                    };
                    var childContentPara = new Paragraph(childRuns)
                    {
                        Margins = new EdgeInsets(0, 0, 0, childGutter),
                    };

                    result.Add(new Entry(
                        new ParagraphRenderer(childMarkerPara) { RunOwner = "A list item" },
                        new ParagraphRenderer(childContentPara) { ElementLanguage = child.Language, RunOwner = "A list item" },
                        child,
                        childClamped,
                        unclampedChildGutter));
                }
            }
        }

        return result;
    }
}
