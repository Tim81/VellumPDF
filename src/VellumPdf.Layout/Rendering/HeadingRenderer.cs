// Copyright 2026 Timothy van der Ham (@Tim81)
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
        _heading = heading;
        var para = new Paragraph(heading.Text, heading.Style)
        {
            Margins = heading.Margins,
            Alignment = heading.Alignment,
        };
        _inner = new ParagraphRenderer(para);
    }

    // Private constructor used by Split/Overflow path.
    private HeadingRenderer(Heading heading, ParagraphRenderer inner)
    {
        _heading = heading;
        _inner = inner;
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
                new HeadingRenderer(_heading, (ParagraphRenderer)result.SplitRenderer!),
                result.OverflowRenderer!);
        }

        return result;
    }

    public void Draw(DrawContext ctx)
    {
        // Register outline entry at the top of the occupied area.
        ctx.AddOutlineEntry(_heading.ResolvedBookmarkTitle, _heading.Level, _occupied.Y);
        _inner.Draw(ctx);
    }
}
