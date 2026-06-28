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
                    $"Pie slice values must be finite and non-negative (was {slice.Value}).",
                    nameof(_chart));
            total += slice.Value;
        }

        if (total <= 0)
            throw new ArgumentException("The sum of pie slice values must be positive.", nameof(_chart));

        if (!double.IsFinite(_chart.Diameter) || _chart.Diameter <= 0)
            throw new ArgumentException(
                $"Pie chart diameter must be a positive finite number (was {_chart.Diameter}).",
                nameof(_chart));
        if (!double.IsFinite(_chart.StartAngle))
            throw new ArgumentException(
                $"Pie chart start angle must be a finite number (was {_chart.StartAngle}).",
                nameof(_chart));
        if (!double.IsFinite(_chart.StrokeWidth) || _chart.StrokeWidth < 0)
            throw new ArgumentException(
                $"Pie chart stroke width must be a non-negative finite number (was {_chart.StrokeWidth}).",
                nameof(_chart));

        var totalHeight = _chart.Diameter + _chart.Margins.Vertical;
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
            HorizontalAlignment.Center => (area.Width - _chart.Diameter) / 2,
            HorizontalAlignment.Right => area.Width - _chart.Diameter,
            _ => 0,
        };

        // Layout reserves exactly Diameter + margins, so the deflated area height equals the
        // diameter; the circle is centred horizontally within the content width.
        var (x, y, _, _) = ctx.ToPdfRect(area);
        var radius = _chart.Diameter / 2;
        var cx = x + xOff + radius;
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
            parts.Add($"{slice.Label} {percent}%");
        }

        return parts.Count == 0 ? "Pie chart" : "Pie chart: " + string.Join(", ", parts);
    }
}
