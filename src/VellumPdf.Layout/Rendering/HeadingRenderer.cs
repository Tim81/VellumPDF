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
internal sealed class HeadingRenderer : IRenderer
{
    private readonly Heading _heading;
    private readonly ParagraphRenderer _inner;
    private LayoutBox _occupied;

    public HeadingRenderer(Heading heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        _heading = heading;
        var para = new Paragraph(heading.Text, heading.Style)
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

    public LayoutResult Layout(LayoutContext context)
    {
        var result = _inner.Layout(context);
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
