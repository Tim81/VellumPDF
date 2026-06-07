// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

public sealed class LayoutImageRenderer : IRenderer
{
    private readonly LayoutImage  _img;
    private readonly RendererContext _rendererCtx;

    private double    _w, _h;
    private LayoutBox _occupied;

    public LayoutImageRenderer(LayoutImage img, RendererContext ctx)
    {
        _img         = img;
        _rendererCtx = ctx;
    }

    public LayoutResult Layout(LayoutContext ctx)
    {
        var area = ctx.Area.Deflate(_img.Margins);
        var imgW = _img.Image.Width;
        var imgH = _img.Image.Height;

        _w = _img.Width ?? area.Width;
        _h = _img.Height ?? (_w / imgW * imgH);

        if (_h > area.Height) return LayoutResult.Nothing();

        _occupied = ctx.Area.WithHeight(_h + _img.Margins.Vertical);
        return LayoutResult.Full(_occupied);
    }

    public void Draw(DrawContext ctx)
    {
        var area = _occupied.Deflate(_img.Margins);
        var xOff = _img.Alignment switch
        {
            HorizontalAlignment.Center => (area.Width - _w) / 2,
            HorizontalAlignment.Right  => area.Width - _w,
            _ => 0
        };

        var resName  = _rendererCtx.RegisterImageXObject(_img.Image);
        var (x, y, _, _) = ctx.ToPdfRect(area);

        ctx.Canvas
            .SaveState()
            .Concat(_w, 0, 0, _h, x + xOff, y)
            .DoXObject(resName)
            .RestoreState();
    }
}
