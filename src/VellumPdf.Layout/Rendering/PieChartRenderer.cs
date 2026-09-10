// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>Renders a <see cref="PieChart"/> as a set of filled Bézier-approximated wedges.</summary>
public sealed class PieChartRenderer : IRenderer
{
    private readonly PieChart _chart;
    private LayoutBox _occupied;

    // The diameter actually used to place and draw the chart, computed once in Layout from
    // _chart.Diameter and the area Layout was handed. See the clamp in Layout for why it is
    // measured against that area rather than against the width left after deflating the
    // chart's own margins.
    private double _placementDiameter;

    /// <summary>Creates a renderer for the given pie chart.</summary>
    public PieChartRenderer(PieChart chart) => _chart = chart;

    /// <summary>Validates the slices, reserves the chart diameter plus margins, and reports the occupied region.</summary>
    public LayoutResult Layout(LayoutContext ctx)
    {
        if (_chart.Slices.Count == 0)
            throw new ArgumentException("A pie chart must have at least one slice.", nameof(_chart));

        var total = 0.0;
        foreach (var slice in _chart.Slices)
        {
            if (!double.IsFinite(slice.Value) || slice.Value < 0)
                throw new ArgumentException(
                    FormattableString.Invariant(
                $"Pie slice values must be finite and non-negative (was {slice.Value})."),
                    nameof(_chart));
            total += slice.Value;
        }

        if (total <= 0)
            throw new ArgumentException("The sum of pie slice values must be positive.", nameof(_chart));

        if (!double.IsFinite(_chart.Diameter) || _chart.Diameter <= 0)
            throw new ArgumentException(
                FormattableString.Invariant(
                $"Pie chart diameter must be a positive finite number (was {_chart.Diameter})."),
                nameof(_chart));
        if (!double.IsFinite(_chart.StartAngle))
            throw new ArgumentException(
                FormattableString.Invariant(
                $"Pie chart start angle must be a finite number (was {_chart.StartAngle})."),
                nameof(_chart));
        if (!double.IsFinite(_chart.StrokeWidth) || _chart.StrokeWidth < 0)
            throw new ArgumentException(
                FormattableString.Invariant(
                $"Pie chart stroke width must be a non-negative finite number (was {_chart.StrokeWidth})."),
                nameof(_chart));

        // Clamp against the area Layout was handed, not against the width left after deflating
        // the chart's own margins. PieChart.Margins defaults to EdgeInsets(6), and a Diameter 300
        // chart with those defaults spans exactly [50, 350] in a 300pt content box today -- a
        // correct document. Clamping against the deflated 288pt would move it to [56, 344]
        // instead, which moves bytes a document that already fits has no reason to move. Guarded
        // on a positive width for the same reason as LayoutImageRenderer's own clamp: ordinary
        // positive margins wider than the box reach a non-positive area too, not only the
        // negative insets v3.0 defers.
        _placementDiameter = ctx.Area.Width > 0
            ? Math.Min(_chart.Diameter, ctx.Area.Width)
            : _chart.Diameter;

        // This bounds the placement, not the emitted geometry, which overshoots it in two
        // independent ways this clamp does not attempt to absorb. AppendArc's control points
        // overshoot the true arc: measured at diameter 300 in a 300pt box, the operand extent
        // spans 1.000 times the diameter at the default start angle and up to 1.13216 times it at
        // a start angle of 1.2 with one slice. And the drawn curve itself bulges past the nominal
        // radius, by up to 1.00027253 times it (measured by evaluating the emitted cubics at
        // 2,048 points per segment), 0.0408pt of x beyond the nominal edge at this diameter.
        // Shrinking the chart to absorb either figure is left to the caller's own margin.
        var totalHeight = _placementDiameter + _chart.Margins.Vertical;
        if (ctx.Area.Height < totalHeight) return LayoutResult.Nothing();

        _occupied = ctx.Area.WithHeight(totalHeight);
        return LayoutResult.Full(_occupied);
    }

    /// <summary>Fills each wedge, optionally stroking separators, wrapped as an artifact when tagging is enabled.</summary>
    public void Draw(DrawContext ctx)
    {
        var area = _occupied.Deflate(_chart.Margins);
        var xOff = _chart.Alignment switch
        {
            HorizontalAlignment.Center => (area.Width - _placementDiameter) / 2,
            HorizontalAlignment.Right => area.Width - _placementDiameter,
            _ => 0,
        };

        // Layout reserves exactly _placementDiameter + margins, so the deflated area height
        // equals _placementDiameter -- not necessarily _chart.Diameter, which the clamp in
        // Layout may have shrunk to fit the area -- and the circle is centred horizontally
        // within the content width. Reserving the unclamped Diameter here would hold vertical
        // space nothing draws in.
        var (areaX, y, _, _) = ctx.ToPdfRect(area);
        var (boxX, _, _, _) = ctx.ToPdfRect(_occupied);

        // Clamping the diameter is not sufficient on its own. The offset above is measured inside
        // the area the chart's own margins deflate, so a circle as wide as the content box is then
        // pushed out of it by a margin on whichever side the alignment favours: measured on a
        // 400x900pt page with 50pt margins and the chart's default 6pt, a Diameter 300 circle in
        // the [50, 350] box ran to [56, 356] under Left and [44, 344] under Right, and on a
        // 454.4pt page with a 1.2pt document margin each of those was 4.8pt off the page itself.
        //
        // So the position is clamped into the content box rather than the alignment being measured
        // against a different width. That distinction is what keeps a chart that already fits from
        // moving: a Diameter of 290 with the default margins sits at [56, 346], inside the box,
        // and switching the basis instead would have moved it to [50, 340]. The clamp cannot fire
        // on a circle whose edges are already inside the box, by construction. It is skipped
        // entirely when the diameter is wider than the box, which only the non-positive-width
        // branch in Layout can leave behind, since Math.Clamp requires its bounds in order.
        // The difference is parenthesised for the reason written out in LayoutImageRenderer's own
        // clamp: left-associated, (boxX + _occupied.Width) - _placementDiameter cancels
        // catastrophically when the circle nearly fills the box and can land below boxX, which
        // hands Math.Clamp a maximum below its minimum. Taking the difference first cannot.
        var left = areaX + xOff;
        if (_placementDiameter <= _occupied.Width)
            left = Math.Clamp(left, boxX, boxX + (_occupied.Width - _placementDiameter));

        var radius = _placementDiameter / 2;
        var cx = left + radius;
        var cy = y + radius;

        // Single pass: total magnitude, count of drawable (non-zero) slices, and the lone
        // colour to use when only one slice is drawable.
        var total = 0.0;
        var drawable = 0;
        var soleColor = ColorRgb.Black;
        foreach (var slice in _chart.Slices)
        {
            total += slice.Value;
            if (slice.Value > 0)
            {
                drawable++;
                soleColor = slice.Color;
            }
        }

        var canvas = ctx.Canvas;

        // Tagged PDF: a data-bearing chart is a Figure with alternate text (mirrors
        // LayoutImageRenderer); a chart flagged Decorative is an artifact the structure
        // tree omits, so assistive technology skips it.
        var mcid = -1;
        if (ctx.Tagged)
        {
            if (_chart.Decorative)
                canvas.BeginArtifactMarkedContent();
            else
                mcid = canvas.BeginMarkedContent("Figure");
        }

        // Save/restore so the fill colour, stroke colour and line width set below do not
        // leak into content drawn by later elements on the same page.
        canvas.SaveState();

        var strokeColor = _chart.StrokeColor;
        if (strokeColor is { } stroke)
            canvas.SetStrokeColorRgb(stroke.R, stroke.G, stroke.B).SetLineWidth(_chart.StrokeWidth);

        void Paint()
        {
            if (strokeColor.HasValue) canvas.FillAndStroke(); else canvas.Fill();
        }

        var direction = _chart.Clockwise ? -1.0 : 1.0;
        var angle = _chart.StartAngle;

        // A lone drawable slice spans the whole circle; a 360° wedge has coincident radial
        // edges that show as a seam when stroked, so draw a seamless circle instead.
        if (drawable == 1)
        {
            canvas.SetFillColorRgb(soleColor.R, soleColor.G, soleColor.B)
                  .MoveTo(cx + (radius * Math.Cos(angle)), cy + (radius * Math.Sin(angle)))
                  .AppendArc(cx, cy, radius, angle, angle + (direction * 2 * Math.PI))
                  .ClosePath();
            Paint();
        }
        else
        {
            foreach (var slice in _chart.Slices)
            {
                if (slice.Value <= 0) continue; // zero-value slices contribute no wedge

                var endAngle = angle + (direction * 2 * Math.PI * (slice.Value / total));
                canvas.SetFillColorRgb(slice.Color.R, slice.Color.G, slice.Color.B)
                      .MoveTo(cx, cy)
                      .LineTo(cx + (radius * Math.Cos(angle)), cy + (radius * Math.Sin(angle)))
                      .AppendArc(cx, cy, radius, angle, endAngle)
                      .ClosePath();
                Paint();

                angle = endAngle;
            }
        }

        canvas.RestoreState();

        if (ctx.Tagged)
        {
            canvas.EndMarkedContent();
            if (mcid >= 0)
                ctx.RegisterStructElem(new PdfStructElem("Figure")
                {
                    Mcid = mcid,
                    AltText = _chart.AltText ?? BuildAltText(total),
                });
        }
    }

    /// <summary>
    /// Composes alternate text from the slice labels and their share of the total.
    /// Falls back to "Pie chart" when no slice carries a label.
    /// </summary>
    private string BuildAltText(double total)
    {
        var parts = new List<string>();
        foreach (var slice in _chart.Slices)
        {
            if (string.IsNullOrEmpty(slice.Label)) continue;
            var percent = Math.Round(slice.Value / total * 100);
            // No invariant wrapper here, unlike the exception messages above: percent is rounded
            // to a whole number, so it carries no decimal separator to vary.
            parts.Add($"{slice.Label} {percent}%");
        }

        return parts.Count == 0 ? "Pie chart" : "Pie chart: " + string.Join(", ", parts);
    }
}
