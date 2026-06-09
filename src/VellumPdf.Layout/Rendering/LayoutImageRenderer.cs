// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>Renders a <see cref="LayoutImage"/> as a placed XObject, honouring sizing, margins and alignment.</summary>
public sealed class LayoutImageRenderer : IRenderer
{
    private readonly LayoutImage _img;

    private double _w, _h;
    private LayoutBox _occupied;

    /// <summary>Creates a renderer for the given layout image.</summary>
    public LayoutImageRenderer(LayoutImage img)
    {
        _img = img;
    }

    /// <summary>Resolves the image size within the available area and reports the occupied region.</summary>
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

    /// <summary>Draws the image XObject, emitting a tagged Figure struct element when tagging is enabled.</summary>
    public void Draw(DrawContext ctx)
    {
        var area = _occupied.Deflate(_img.Margins);
        var xOff = _img.Alignment switch
        {
            HorizontalAlignment.Center => (area.Width - _w) / 2,
            HorizontalAlignment.Right => area.Width - _w,
            _ => 0
        };

        // Register the XObject via the per-page RendererContext carried on DrawContext.
        var resName = ctx.RendererContext.RegisterImageXObject(_img.Image);
        var (x, y, _, _) = ctx.ToPdfRect(area);

        // Tagged PDF: Figure struct elem with an /Alt entry for accessibility.
        int mcid = -1;
        if (ctx.Tagged)
            mcid = ctx.Canvas.BeginMarkedContent("Figure");

        ctx.Canvas
            .SaveState()
            .Concat(_w, 0, 0, _h, x + xOff, y)
            .DoXObject(resName)
            .RestoreState();

        if (ctx.Tagged && mcid >= 0)
        {
            ctx.Canvas.EndMarkedContent();
            var elem = new PdfStructElem("Figure")
            {
                Mcid = mcid,
                AltText = _img.AltText ?? "Figure",
            };
            ctx.RegisterStructElem(elem);
        }
    }
}
