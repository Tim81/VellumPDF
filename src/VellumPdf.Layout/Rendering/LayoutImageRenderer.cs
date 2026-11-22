// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>Renders a <see cref="LayoutImage"/> as a placed XObject, honouring sizing, margins and alignment.</summary>
/// <remarks>Does not split. Too-tall images throw from save. Justify is treated as left.</remarks>
public sealed class LayoutImageRenderer : IRenderer, IPageBreakSource, IKeepSource, IPlacementSource
{
    private readonly LayoutImage _img;

    private double _w, _h;
    private LayoutBox _occupied;

    /// <summary>Creates a renderer for the given layout image.</summary>
    /// <remarks>
    /// A null <paramref name="img"/> is refused here. A layout image cannot hold a
    /// null image, because <see cref="LayoutImage"/> refuses one when it is created.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="img"/> is <see langword="null"/>.
    /// </exception>
    public LayoutImageRenderer(LayoutImage img)
    {
        ArgumentNullException.ThrowIfNull(img);
        _img = img;
    }

    // Explicit, because the renderer is a public class and an implicit member would be public API.
    // The mode belongs to the element, so a renderer added through Document.Add(IRenderer) honours
    // it as well.
    PageBreakMode IPageBreakSource.BreakBefore => _img.BreakBefore;

    // An image does not split, so it has no keep-together request.
    bool IKeepSource.KeepWithNext => _img.KeepWithNext;

    bool IKeepSource.KeepTogether => false;

    // Explicit, as above. The image is drawn inside its margins, so its anchor is the top of the
    // area it is drawn in and not the top of the box it reserves.
    void IPlacementSource.ReportPlacements(PlacementSink sink)
    {
        if (_img.Anchor is { } anchor)
            sink.ReportAnchor(_img, anchor, _occupied.Deflate(_img.Margins));
    }

    internal LayoutImage Element => _img;

    /// <summary>Resolves the image size within the available area and reports the occupied region.</summary>
    /// <remarks>
    /// This renderer does not split. An image taller than the area returns
    /// <see cref="LayoutResult.Outcome.Nothing"/>, and the document's save then throws
    /// <see cref="InvalidOperationException"/> saying the element is too tall to fit. This method
    /// raises the image's size refusals itself. When the document calls this method, they reach you
    /// from the save.
    /// <para>With <see cref="LayoutImage.FitToPage"/> set, the size is scaled to the content height
    /// of a fresh page, which the document supplies. A context you build with the public
    /// constructor carries none, so there the image is fitted to <paramref name="ctx"/>'s own
    /// <see cref="LayoutContext.Area"/>, less the image's margins.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="LayoutImage.Width"/> or <see cref="LayoutImage.Height"/>, or the height derived
    /// from the width, is refused, or a fitted extent is; see those members,
    /// <see cref="LayoutImage.FitToPage"/> and <see cref="LayoutImage.Margins"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The image's own pixel width or height is not a positive number.
    /// </exception>
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

        // The drawn extents, not the source pixels, which were checked above. A zero width also
        // collapses the height through the aspect-ratio division, and the height check below
        // passes trivially at zero, so nothing rejected either and the matrix reached the content
        // stream non-invertible (#478).
        LayoutValidation.ValidateImageExtent(_w, _h);

        if (_img.FitToPage)
        {
            // Fitted to the box every fresh page offers and not to the area left on this one, so
            // that an image too tall for the rest of a page still moves to the next page at the
            // size it would have there. The box is the one the retry is offered, so a fitted
            // image is placeable on it by construction. Only the height and width of the box are
            // read: under mirrored margins its X differs from page to page. A caller-built
            // context carries no such box, and the area offered is the only one known.
            var fit = (ctx.FreshPageArea ?? ctx.Area).Deflate(_img.Margins).Height;

            // A box with no positive height cannot hold a scaled image, so the image is left as
            // sized and refused as too tall. NaN fails the comparison and is left the same way.
            if (fit > 0 && _h > fit)
            {
                _w *= fit / _h;
                _h = fit;

                // The factor can take a wide, short source under the smallest written extent.
                LayoutValidation.ValidateImageExtent(_w, _h);
            }
        }

        if (_h > area.Height)
        {
            // The reason is read only when the retry on a fresh page also answers Nothing, which
            // is a call at a page top, so any other call skips building the string.
            return ctx.AtPageTop
                ? LayoutResult.Nothing(TooTallReason(area.Height))
                : LayoutResult.Nothing();
        }

        _occupied = ctx.Area.WithHeight(_h + _img.Margins.Vertical);
        return LayoutResult.Full(_occupied);
    }

    // Names the axis that overflowed and both numbers, because the document's own sentence cannot:
    // it does not know the image. Whole points, trimmed to two decimals.
    private string TooTallReason(double contentHeight)
    {
        var content = Pt(contentHeight);
        if (_img.Height is not null)
            return $"LayoutImage.Height {Pt(_h)} exceeds the {content} content height.";

        return "The height derived from the width and the image's proportions, " +
            $"{Pt(_h)}, exceeds the {content} content height" +
            (_img.FitToPage ? "." : "; set FitToPage to scale it down.");
    }

    private static string Pt(double points) =>
        points.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "pt";

    /// <summary>Draws the image XObject, emitting a tagged Figure struct element when tagging is enabled.</summary>
    /// <remarks>See <see cref="IRenderer.Draw"/>.</remarks>
    /// <inheritdoc cref="IRenderer.Draw" path="/exception"/>
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
        // Skipped when the width is wider than the box, because Math.Clamp requires its bounds in
        // order. Two things reach that: the non-positive-width branch in Layout, and a negative
        // horizontal inset, which inflates the deflated area instead of shrinking it, so a null
        // Width takes a value wider than the box the clamp measures against. Measured on a 400x900pt
        // page at 50pt margins with insets of -20 left and right: the image is placed [30, 370],
        // 20pt outside the [50, 350] box on each side, exactly as it was before this branch.
        // Negative insets are the v3.0 case (#484) and are left as they render.
        //
        // The difference is parenthesised, and that is not cosmetic. Written as
        // boxX + _occupied.Width - _w, C# left-associates it into (boxX + _occupied.Width) - _w,
        // and where the width nearly fills the box that subtraction cancels catastrophically and
        // lands a fraction below boxX, so Math.Clamp is handed a maximum below its minimum and
        // throws. The property suite found it, on generated margins, which is the only reason it
        // was found at all. Taking the difference first cannot do that, and the reason is a proof
        // rather than a measurement: the guard above makes the difference non-negative, and adding
        // a non-negative to a finite value never decreases it under round-to-nearest.
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
