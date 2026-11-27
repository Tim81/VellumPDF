// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Renderer for <see cref="Heading"/>. Delegates text layout/draw to
/// <see cref="ParagraphRenderer"/> and additionally registers a document
/// outline entry at the exact position where the heading lands.
/// </summary>
internal sealed class HeadingRenderer : IRenderer, IPlacementSource, IPageBreakSource, IKeepSource, ISpaceAfterSource, IContinuationTwin
{
    private readonly Heading _heading;
    private readonly ParagraphRenderer _inner;
    private LayoutBox _occupied;

    public HeadingRenderer(Heading heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        _heading = heading;
        var para = new Paragraph(heading.Runs)
        {
            Margins = heading.Margins,
            Alignment = heading.Alignment,
        };
        _inner = new ParagraphRenderer(para)
        {
            StructType = HeadingStructType(heading.Level),
            ElementLanguage = heading.Language,
            RunOwner = "A heading",
        };
    }

    PageBreakMode IPageBreakSource.BreakBefore => _heading.BreakBefore;

    // Explicit, to match the public renderers.
    double ISpaceAfterSource.SpaceAfter => _heading.SpaceAfter;

    bool IKeepSource.KeepWithNext => _heading.KeepWithNext;

    bool IKeepSource.KeepTogether => _heading.KeepTogether;

    /// <summary>Maps a heading level (0-based) to a PDF structure type.</summary>
    private static string HeadingStructType(int level) => level switch
    {
        0 => "H1",
        1 => "H2",
        2 => "H3",
        3 => "H4",
        4 => "H5",
        _ => "H6",
    };

    // Private constructor used by the split path (inner already has StructType set). The split piece
    // draws the bookmark, so it carries the box it was placed in; without it the entry would target
    // layout y 0, the top of the page (#464).
    private HeadingRenderer(Heading heading, ParagraphRenderer inner, LayoutBox occupied)
    {
        _heading = heading;
        _inner = inner;
        _occupied = occupied;
    }

    // The heading laid out the way 2.3.3 laid it out, from the same start: its paragraph is the
    // paragraph's twin. Removed in 3.0 (#688).
    private HeadingRenderer? _twin;

    IRenderer IContinuationTwin.Twin() =>
        _twin ??= new HeadingRenderer(_heading, (ParagraphRenderer)((IContinuationTwin)_inner).Twin(), default);

    public LayoutResult Layout(LayoutContext context)
    {
        // The paragraph is this heading's child: it lays out in the heading's mode, and the
        // heading reports for it, so a paragraph's own report is never read. Under a context that
        // does not address the heading, the paragraph is not addressed either and wraps as 2.3.3
        // did. Removed in 3.0 (#688).
        var mode = context.ModeFor(this);
        if (mode == ContinuationMode.Undecided && context.Session is { Probing: false, IsView: false } session
            && ReferenceEquals(session.Called, this))
        {
            var width = context.Area.Deflate(_heading.Margins).Width;
            if (width > 0 && _inner.TwinDifferenceBound(width) is { } bound)
                session.Report(this, bound);
        }

        var innerContext = mode == ContinuationMode.Twin
            ? context
            : context.Address(this, _inner, context.Area, context.AtPageTop, context.HonourLineCounts);
        var result = _inner.Layout(innerContext);
        if (result.OccupiedArea.HasValue)
            _occupied = result.OccupiedArea.Value;

        // For Partial, wrap only the split piece (the part that fits) in a
        // HeadingRenderer so the outline entry fires once at first-draw.
        // The overflow is a plain ParagraphRenderer — no second bookmark entry.
        if (result.Status == LayoutResult.Outcome.Partial)
        {
            return LayoutResult.Partial(
                result.OccupiedArea!.Value,
                new HeadingRenderer(
                    _heading,
                    (ParagraphRenderer)result.SplitRenderer!,
                    result.OccupiedArea!.Value),
                result.OverflowRenderer!);
        }

        return result;
    }

    // Explicit, and for the same y the outline entry below uses, so the plan and the /Dest it
    // writes are two routes to one fact. A split heading reports from the piece that holds its
    // first line; the overflow is a plain paragraph and reports nothing.
    void IPlacementSource.ReportPlacements(PlacementSink sink)
    {
        sink.Report(_heading, _occupied);
        if (_heading.Anchor is { } anchor)
            sink.ReportAnchor(_heading, anchor, _occupied);
    }

    internal Heading Element => _heading;

    public void Draw(DrawContext ctx)
    {
        // Register outline entry at the top of the occupied area.
        // The unchecked core, not the validating overload: a heading's title is never null, and a
        // non-finite destination is refused later, by the writer, with ArgumentException.
        ctx.AddOutlineEntryCore(
            _heading.ResolvedBookmarkTitle, _heading.Level, _occupied.Y, _heading.BookmarkExpanded);
        _inner.Draw(ctx);
    }
}
