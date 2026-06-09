// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>Renders a <see cref="LineSeparator"/> as a horizontal rule spanning the content width.</summary>
public sealed class LineSeparatorRenderer : IRenderer
{
    private readonly LineSeparator _sep;
    private LayoutBox _occupied;

    /// <summary>Creates a renderer for the given line separator.</summary>
    public LineSeparatorRenderer(LineSeparator sep) => _sep = sep;

    /// <summary>Reserves the separator's line width plus margins and reports the occupied region.</summary>
    public LayoutResult Layout(LayoutContext ctx)
    {
        var totalHeight = _sep.Margins.Top + _sep.LineWidth + _sep.Margins.Bottom;
        if (ctx.Area.Height < totalHeight) return LayoutResult.Nothing();
        _occupied = ctx.Area.WithHeight(totalHeight);
        return LayoutResult.Full(_occupied);
    }

    /// <summary>Strokes the separator line at the configured colour and width.</summary>
    public void Draw(DrawContext ctx)
    {
        var (x, y, w, h) = ctx.ToPdfRect(_occupied);
        var lineY = y + _sep.Margins.Bottom + _sep.LineWidth / 2;
        ctx.Canvas
            .SetStrokeColorRgb(_sep.Color.R, _sep.Color.G, _sep.Color.B)
            .SetLineWidth(_sep.LineWidth)
            .MoveTo(x, lineY)
            .LineTo(x + w, lineY)
            .Stroke();
    }
}
