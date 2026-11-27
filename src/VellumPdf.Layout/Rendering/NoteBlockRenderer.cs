// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Renderer for <see cref="NoteBlock"/>. It is internal, as <see cref="HeadingRenderer"/> is, so a
/// block is only ever a top-level element added with <see cref="Document.Add(NoteBlock)"/> and the
/// checks the document runs before any layout see every instance.
/// </summary>
/// <remarks>
/// It lays its paragraphs out with their own <see cref="ParagraphRenderer"/>, one after another in
/// the width the border and padding leave, and answers for the whole block. A piece is the part of
/// the block on one page: the split piece is drawn where it stands, and the overflow piece starts
/// at the paragraph, or the line of one, where the split ended. The paragraphs read no pagination
/// plan of their own beyond what a top-level paragraph reads, so both passes agree.
/// </remarks>
internal sealed class NoteBlockRenderer : IRenderer, IPageBreakSource, IKeepSource, ISpaceAfterSource, IPlacementSource
{
    // What every piece of one block shares: the renderer of each paragraph, built once, and the
    // structure element the tagged pieces add to, so a block that spans pages is one element.
    private sealed class Shared
    {
        internal ParagraphRenderer[]? Children;
        internal PdfStructElem? Structure;
    }

    // A paragraph, or the rest of one, placed in a piece, with where its renderer put it. A
    // renderer
    // that answered Full is the shared one, and a later layout of the block, a probe for a keep
    // request for one, writes over what it holds, so the piece restores its own placement before it
    // reports or draws.
    private readonly record struct Placed(
        ParagraphRenderer Renderer, LayoutBox Box, (int EndLine, LayoutBox Occupied) Placement);

    // What one attempt at filling a piece produced.
    private sealed class Attempt
    {
        internal readonly List<Placed> Placed = [];
        internal int NextIndex;
        internal ParagraphRenderer? NextCarried;
        internal bool Complete;
        internal double Bottom;
    }

    private readonly NoteBlock _block;
    private readonly Shared _shared;

    // Where this piece starts: the paragraph, and the renderer that continues it when the
    // paragraph was split on the page before. The first piece starts at paragraph 0.
    private readonly int _startIndex;
    private readonly ParagraphRenderer? _carried;
    private readonly bool _isFirst;

    // Set by Layout, and read by Draw and ReportPlacements of the instance Layout answered for.
    private List<Placed> _placed = [];
    private LayoutBox _occupied;

    internal NoteBlockRenderer(NoteBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _block = block;
        _shared = new Shared();
        _isFirst = true;
    }

    private NoteBlockRenderer(NoteBlock block, Shared shared, int startIndex, ParagraphRenderer? carried, bool isFirst)
    {
        _block = block;
        _shared = shared;
        _startIndex = startIndex;
        _carried = carried;
        _isFirst = isFirst;
    }

    internal NoteBlock Element => _block;

    PageBreakMode IPageBreakSource.BreakBefore => _block.BreakBefore;

    double ISpaceAfterSource.SpaceAfter => _block.SpaceAfter;

    bool IKeepSource.KeepWithNext => _block.KeepWithNext;

    bool IKeepSource.KeepTogether => _block.KeepTogether;

    // The block's anchor lands at the top of its box, so only the piece that starts the block
    // reports it. A paragraph inside reports its own, from the piece that holds its first line.
    void IPlacementSource.ReportPlacements(PlacementSink sink)
    {
        if (_isFirst && _block.Anchor is { } anchor)
            sink.ReportAnchor(_block, anchor, _occupied);
        foreach (var placed in _placed)
        {
            placed.Renderer.RestorePlacement(placed.Placement);
            ((IPlacementSource)placed.Renderer).ReportPlacements(sink);
        }
    }

    /// <summary>
    /// The width the paragraphs are laid out in when the block is offered
    /// <paramref name="boxWidth"/>, before any paragraph's own margins.
    /// </summary>
    internal double ContentWidthIn(double boxWidth) =>
        boxWidth - 2 * _block.BorderWidth - _block.Padding.Horizontal;

    /// <summary>
    /// Refuses, before any layout, what the block and the content box alone decide, so the document
    /// stays saveable once the caller has changed it.
    /// </summary>
    internal void Validate(LayoutBox contentArea)
    {
        if (_block.Paragraphs.Count == 0)
            throw new InvalidOperationException(
                "A note block holds no paragraph. Add a paragraph to it, or remove it from the document. " +
                "Nothing has been laid out, so the document can be saved once that is done.");

        var width = ContentWidthIn(contentArea.Width);
        foreach (var paragraph in _block.Paragraphs)
        {
            if (paragraph.TabStops is not { Count: > 0 })
                continue;
            LayoutValidation.ValidateTabStops(paragraph, width);
            LayoutValidation.ValidateLeaderSizes(paragraph);
        }
    }

    private ParagraphRenderer[] ChildrenOf()
    {
        if (_shared.Children is { } built && built.Length == _block.Paragraphs.Count)
            return built;

        var paragraphs = _block.Paragraphs;
        var children = new ParagraphRenderer[paragraphs.Count];
        for (var i = 0; i < children.Length; i++)
            children[i] = new ParagraphRenderer(paragraphs[i]) { ElementLanguage = paragraphs[i].Language };
        _shared.Children = children;
        return children;
    }

    public LayoutResult Layout(LayoutContext context)
    {
        var children = ChildrenOf();
        var width = ContentWidthIn(context.Area.Width);
        if (!(width > 0))
            return LayoutResult.Nothing(
                "The note block's border and padding leave its paragraphs no width.");

        // Every piece is a whole box: the page's room less the top and the bottom border and
        // padding is what its paragraphs get, and they split wherever that room ends.
        var piece = Fill(context, children, width);
        if (piece.Placed.Count == 0)
            return LayoutResult.Nothing(
                "The note block's top and bottom border and padding and its tallest line do not fit on an empty page.");

        var area = context.Area;
        var bottomInset = _block.BorderWidth + _block.Padding.Bottom;
        var occupied = new LayoutBox(area.X, area.Y, area.Width, piece.Bottom + bottomInset - area.Y);

        if (piece.Complete)
        {
            _placed = piece.Placed;
            _occupied = occupied;
            return LayoutResult.Full(occupied);
        }

        var split = new NoteBlockRenderer(_block, _shared, _startIndex, _carried, _isFirst)
        {
            _placed = piece.Placed,
            _occupied = occupied,
        };
        var overflow = new NoteBlockRenderer(_block, _shared, piece.NextIndex, piece.NextCarried, isFirst: false);
        return LayoutResult.Partial(occupied, split, overflow);
    }

    // The height left for the next paragraph: the area's height less what is used and less the
    // bottom inset, in that order, which is how a paragraph with the same top and bottom margins
    // forms its own room, so a block fits wherever that paragraph fits.
    private static double Room(double height, double used, double reserve) =>
        height - used - reserve;

    // Lays the paragraphs of this piece out from its first one until one does not fit whole.
    // Paragraphs are placed in order and a piece ends between any two lines: a block has no keep
    // rules of its own among them, and its paragraphs have no margins.
    private Attempt Fill(LayoutContext context, ParagraphRenderer[] children, double width)
    {
        var block = _block;
        var area = context.Area;
        var left = area.X + block.BorderWidth + block.Padding.Left;
        var topInset = block.BorderWidth + block.Padding.Top;
        var reserve = block.BorderWidth + block.Padding.Bottom;

        var result = new Attempt();
        var used = topInset;
        var i = _startIndex;
        for (; i < children.Length; i++)
        {
            var carried = i == _startIndex ? _carried : null;
            var renderer = carried ?? children[i];

            // The gap after the paragraph before, spent when it fits below it. One that does not
            // fit ends the piece, and is dropped, as the document drops one at a page end.
            if (result.Placed.Count > 0)
            {
                var gap = children[i - 1].Paragraph.SpaceAfter;
                if (gap > 0)
                {
                    if (gap <= Room(area.Height, used, reserve)) used += gap;
                    else break;
                }
            }

            var childArea = new LayoutBox(left, area.Y + used, width, Room(area.Height, used, reserve));
            var answer = renderer.Layout(new LayoutContext(childArea));

            if (answer.Status == LayoutResult.Outcome.Full)
            {
                var box = answer.OccupiedArea!.Value;
                result.Placed.Add(new Placed(renderer, box, renderer.Placement));
                used = box.Bottom - area.Y;
                continue;
            }

            if (answer.Status == LayoutResult.Outcome.Partial)
            {
                var box = answer.OccupiedArea!.Value;
                var split = (ParagraphRenderer)answer.SplitRenderer!;
                result.Placed.Add(new Placed(split, box, split.Placement));
                result.NextIndex = i;
                result.NextCarried = (ParagraphRenderer)answer.OverflowRenderer!;
                result.Bottom = box.Bottom;
                return result;
            }

            break;
        }

        if (i >= children.Length)
        {
            result.Complete = true;
            result.Bottom = area.Y + used;
            return result;
        }

        if (result.Placed.Count == 0)
            return result;

        result.NextIndex = i;
        result.NextCarried = i == _startIndex ? _carried : null;
        result.Bottom = result.Placed[^1].Box.Bottom;
        return result;
    }

    public void Draw(DrawContext ctx)
    {
        if (_placed.Count == 0)
            return;

        DrawBox(ctx);

        PdfStructElem? structure = null;
        var created = false;
        if (ctx.Tagged)
        {
            if (_isFirst || _shared.Structure is null)
            {
                // Div in every file: it is standard in the default namespace of PDF 1.7 and 2.0.
                // Aside
                // is a PDF 2.0 type that needs its own structure namespace, which the Kernel does
                // not write yet.
                _shared.Structure = new PdfStructElem("Div");
                created = true;
            }

            structure = _shared.Structure;
        }

        foreach (var placed in _placed)
        {
            placed.Renderer.RestorePlacement(placed.Placement);
            placed.Renderer.ParentStructElem = structure;
            placed.Renderer.Draw(ctx);
        }

        // Registered once the first piece has added its paragraphs, as a table registers its tree;
        // the later pieces add theirs to the same element.
        if (created)
            ctx.RegisterStructElemTree(structure!);
    }

    // The fill, then the border, as an artifact in a tagged document: neither is content, so
    // neither
    // enters the structure tree or the extracted text. The border is four bars filled in one path,
    // so an edge a page break leaves open is left out and the corners need no join. The background
    // fills the whole box, border included, so no hairline shows between the two.
    private void DrawBox(DrawContext ctx)
    {
        var background = _block.Background;
        var b = _block.BorderWidth;
        if (background is null && !(b > 0))
            return;

        var canvas = ctx.Canvas;
        var box = _occupied;
        if (ctx.Tagged) canvas.BeginArtifactMarkedContent();
        canvas.SaveState();

        if (background is { } fill)
        {
            var (x, y, w, h) = ctx.ToPdfRect(box);
            canvas.SetFillColorRgb(fill.R, fill.G, fill.B).Rectangle(x, y, w, h).Fill();
        }

        if (b > 0)
        {
            var color = _block.BorderColor;
            canvas.SetFillColorRgb(color.R, color.G, color.B);
            Bar(ctx, new LayoutBox(box.X, box.Y, b, box.Height));
            Bar(ctx, new LayoutBox(box.Right - b, box.Y, b, box.Height));
            Bar(ctx, new LayoutBox(box.X + b, box.Y, box.Width - 2 * b, b));
            Bar(ctx, new LayoutBox(box.X + b, box.Bottom - b, box.Width - 2 * b, b));
            canvas.Fill();
        }

        canvas.RestoreState();
        if (ctx.Tagged) canvas.EndMarkedContent();
    }

    private static void Bar(DrawContext ctx, LayoutBox bar)
    {
        var (x, y, w, h) = ctx.ToPdfRect(bar);
        ctx.Canvas.Rectangle(x, y, w, h);
    }
}
