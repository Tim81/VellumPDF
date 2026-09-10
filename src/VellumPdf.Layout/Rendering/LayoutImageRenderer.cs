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

        if (!double.IsFinite(imgW) || imgW <= 0)
            throw new ArgumentException(
                // No invariant wrapper: PdfImageXObject.Width is int, so this carries no decimal
                // separator to vary. The floating-point messages in this assembly are wrapped.
                $"Image width must be a positive finite number (was {imgW}).",
                nameof(_img));
        if (!double.IsFinite(imgH) || imgH <= 0)
            throw new ArgumentException(
                $"Image height must be a positive finite number (was {imgH}).",
                nameof(_img));

        _w = _img.Width ?? area.Width;

        // An explicit Width is honoured at any size, and only Height was checked against the
        // box. Clamp it, mirroring the height check below.
        //
        // Measured against ctx.Area.Width, the area Layout was handed, not against the width left
        // after the image's own margins deflate it. LayoutImage.Margins defaults to zero, so the
        // two agree for most callers, but a caller who sets them has an explicit Width that fits
        // the content box and not the deflated area: a Width of 290 with EdgeInsets(6) in a 300pt
        // box draws to [56, 346], inside the box, and clamping against the deflated 288 would
        // shrink it to 288 and, with a null Height, shrink the reservation and move everything
        // below -- measured, a 400x413pt page went from two pages to one.
        //
        // Guarded on a positive width for more than the negative insets v3.0 defers: ordinary
        // positive margins wider than the content box reach a non-positive deflated area too,
        // where EdgeInsets(200) on a 300pt box gives -100. That case is left as it renders, which
        // for an explicit Width is unchanged; clamping to a negative width would set the x scale
        // negative and mirror the image.
        if (_img.Width is { } explicitWidth && ctx.Area.Width > 0 && explicitWidth > ctx.Area.Width)
            _w = ctx.Area.Width;

        // An explicit Height is the caller asking for a non-proportional box, and clamping the
        // width is already as close to their intent as the box allows, so only a null Height --
        // which already follows the aspect ratio -- is rescaled to match the clamp above.
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
        var (areaX, y, _, _) = ctx.ToPdfRect(area);
        var (boxX, _, _, _) = ctx.ToPdfRect(_occupied);

        // The offset above is measured inside the margin-deflated area, so a clamped image as wide
        // as the content box is then pushed out of it by a margin, exactly as PieChartRenderer's
        // circle was. Clamp the position into the content box rather than measuring the alignment
        // against a different width, which is what leaves an image that already fits untouched.
        // Skipped when the width is wider than the box, which only the non-positive-width branch
        // in Layout can leave behind, since Math.Clamp requires its bounds in order.
        //
        // The difference is parenthesised, and that is not cosmetic. Written as
        // boxX + _occupied.Width - _w, C# left-associates it into (boxX + _occupied.Width) - _w,
        // and where the width nearly fills the box that subtraction cancels catastrophically and
        // lands a fraction below boxX, so Math.Clamp is handed a maximum below its minimum and
        // throws. The property suite found it: generated margins produced a maximum of
        // 49.25926 against a minimum of 49.25924. Taking the difference first cannot do that,
        // because the guard above makes it non-negative and adding a non-negative to a finite
        // value never decreases it.
        var left = areaX + xOff;
        if (_w <= _occupied.Width)
            left = Math.Clamp(left, boxX, boxX + (_occupied.Width - _w));

        // Tagged PDF: Figure struct elem with an /Alt entry for accessibility.
        int mcid = -1;
        if (ctx.Tagged)
            mcid = ctx.Canvas.BeginMarkedContent("Figure");

        ctx.Canvas
            .SaveState()
            .Concat(_w, 0, 0, _h, left, y)
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
