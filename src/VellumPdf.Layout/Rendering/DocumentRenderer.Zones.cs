// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

// The drawing of a band built from three zones, and of a rule. The string band's own path is in
// DocumentRenderer.cs and is not entered for a zoned band.
public sealed partial class DocumentRenderer
{
    private ZoneTexts? _headerZones;
    private ZoneTexts? _footerZones;

    /// <summary>The runs of one zoned band, resolved on demand like a template's text.</summary>
    private sealed class ZoneTexts
    {
        internal ZoneTexts(RunningBand band)
        {
            Band = band;
            Texts = [.. band.ZoneTemplates.Select(zone => zone.Select(t => new BandText(t)).ToArray())];
        }

        internal RunningBand Band { get; }

        /// <summary>Indexed by zone, then by run.</summary>
        internal BandText[][] Texts { get; }
    }

    private enum BandSlot
    {
        Left,
        Center,
        Right,
    }

    // The x of the content box's two edges on this page. Under mirrored margins a verso page has its
    // outside margin on the left, as the string band reads it.
    private (double Left, double Right) BandEdges(int pageNumber)
    {
        var mirrored = MirrorMargins && !IsRectoNumber(pageNumber);
        return (
            mirrored ? _margins.Right : _margins.Left,
            _pageSize.Width - (mirrored ? _margins.Left : _margins.Right));
    }

    // A rule lies inside the band: its centre is half its width from the band's edge, so the band's
    // reserved height still holds everything the band draws. The state is saved because a stroke
    // colour and a line width otherwise stay set for what the page draws next.
    private void DrawBandRule(
        DrawContext ctx, PdfCanvas canvas, BandRule rule, int pageNumber, double bandY, double bandHeight,
        bool ownMarkedContent)
    {
        var (leftX, rightX) = BandEdges(pageNumber);
        var y = ctx.ToPdfY(rule.Position == BandRulePosition.Above
            ? bandY + rule.Width / 2
            : bandY + bandHeight - rule.Width / 2);

        if (ownMarkedContent && ctx.Tagged) canvas.BeginArtifactMarkedContent();
        canvas
            .SaveState()
            .SetStrokeColorRgb(rule.Color.R, rule.Color.G, rule.Color.B)
            .SetLineWidth(rule.Width)
            .MoveTo(leftX, y)
            .LineTo(rightX, y)
            .Stroke()
            .RestoreState();
        if (ownMarkedContent && ctx.Tagged) canvas.EndMarkedContent();
    }

    private void DrawZonedBand(
        DrawContext ctx,
        PdfCanvas canvas,
        RunningBand band,
        RunningBandKind kind,
        ref ZoneTexts? cache,
        int pageNumber,
        int totalPages,
        double bandY,
        double bandHeight)
    {
        if (cache is null || !ReferenceEquals(cache.Band, band)) cache = new ZoneTexts(band);

        var label = LabelFor(band, pageNumber);
        var chapter = ChapterOnPage;
        foreach (var zoneTexts in cache.Texts)
            foreach (var text in zoneTexts)
                text.Reset(pageNumber, totalPages, label, chapter);

        var zones = new[] { band.Left!, band.Center!, band.Right! };
        var (leftX, rightX) = BandEdges(pageNumber);
        var contentWidth = _pageSize.Width - _margins.Horizontal;
        var centerX = leftX + contentWidth / 2;
        var gap = band.ZoneGap;
        var swap = band.MirrorZones && MirrorMargins && !IsRectoNumber(pageNumber);

        // Priority follows the zone, so the slot a zone stands in does not change who is cut.
        BandSlot SlotOf(int zone) =>
            zone == 1 ? BandSlot.Center : (zone == 0) == !swap ? BandSlot.Left : BandSlot.Right;

        // The extents the zones placed so far take, per slot.
        double? leftEnd = null, rightStart = null, centerStart = null, centerEnd = null;
        var x = new double[3];
        var drawn = new string[3][];
        var drawnWidths = new double[3][];

        static double? Nearest(double? a, double? b, Func<double, double, double> pick) =>
            a is { } x1 ? (b is { } x2 ? pick(x1, x2) : x1) : b;

        void Place(int zone, double width)
        {
            if (width <= 0) return;
            switch (SlotOf(zone))
            {
                case BandSlot.Left:
                    x[zone] = leftX;
                    leftEnd = leftX + width;
                    break;
                case BandSlot.Right:
                    x[zone] = rightX - width;
                    rightStart = x[zone];
                    break;
                default:
                    x[zone] = centerX - width / 2;
                    centerStart = x[zone];
                    centerEnd = x[zone] + width;
                    break;
            }
        }

        // Logos first: one is never cut, and its width is fixed by the band's height.
        for (var z = 0; z < 3; z++)
            if (zones[z].Image is not null) Place(z, band.LogoWidth(z));

        long resolvedCharacters = 0, drawnCharacters = 0;
        foreach (var z in new[] { 2, 0, 1 })
        {
            var runs = zones[z].Runs;
            if (runs.Count == 0) continue;

            // The room the zone has: out to the nearest zone already placed on its inner side, less
            // the gap. A zone that is the only one on its side of the page has the content box.
            var allowed = SlotOf(z) switch
            {
                BandSlot.Left => (Nearest(centerStart, rightStart, Math.Min) is { } next ? next - gap : rightX) - leftX,
                BandSlot.Right => rightX - (Nearest(leftEnd, centerEnd, Math.Max) is { } previous ? previous + gap : leftX),
                _ => 2 * Math.Min(
                    centerX - (leftEnd is { } l ? l + gap : leftX),
                    (rightStart is { } r ? r - gap : rightX) - centerX),
            };

            // A zone's runs are cut in turn: the run that does not fit ends the zone, and the runs
            // after it are not drawn.
            var texts = cache.Texts[z];
            drawn[z] = new string[runs.Count];
            drawnWidths[z] = new double[runs.Count];
            var remaining = allowed;
            var cut = false;
            var total = 0.0;
            for (var r = 0; r < runs.Count; r++)
            {
                var text = texts[r];
                resolvedCharacters += text.Length;
                if (cut || remaining <= 0 || text.Length == 0)
                {
                    drawn[z][r] = "";
                    cut |= text.Length > 0;
                    continue;
                }

                var (piece, width) = FitToWidth(runs[r].Style, text, remaining);
                BandCharactersProduced += text.Produced;
                drawn[z][r] = piece;
                drawnWidths[z][r] = width;
                drawnCharacters += piece.Length;
                remaining -= width;
                total += width;
                cut = piece.Length < text.Length;
            }

            Place(z, total);
        }

        if (drawnCharacters < resolvedCharacters)
            RecordTruncation(kind, pageNumber, checked((int)drawnCharacters), checked((int)resolvedCharacters));

        var anyText = drawn.Any(zone => zone is not null && zone.Any(s => s.Length > 0));
        var anyLogo = zones.Any(zone => zone.Image is not null);
        if (!anyText && !anyLogo && band.Rule is null) return;

        // One baseline for the band, placed as a string band places its text, from the largest size.
        var baseline = ctx.ToPdfY(bandY + bandHeight - (bandHeight - band.LargestFontSize) / 2);

        // A band is decoration, so a tagged document marks it as an artifact, rule and logo included.
        if (ctx.Tagged) canvas.BeginArtifactMarkedContent();
        if (band.Rule is { } rule)
            DrawBandRule(ctx, canvas, rule, pageNumber, bandY, bandHeight, ownMarkedContent: false);

        TextEmitter.SelectedFont? bandFont = null;
        for (var z = 0; z < 3; z++)
        {
            if (zones[z].Image is { } logo)
            {
                var resName = ctx.RendererContext.RegisterImageXObject(logo.Image);
                canvas
                    .SaveState()
                    .Concat(band.LogoWidth(z), 0, 0, bandHeight, x[z], ctx.ToPdfY(bandY + bandHeight))
                    .DoXObject(resName)
                    .RestoreState();
                continue;
            }

            if (drawn[z] is null) continue;
            var runX = x[z];
            for (var r = 0; r < drawn[z].Length; r++)
            {
                if (drawn[z][r].Length == 0) continue;

                var style = zones[z].Runs[r].Style;
                canvas.BeginText();
                TextEmitter.SelectFont(ctx, canvas, style, ref bandFont);
                canvas.SetFillColorRgb(style.Color.R, style.Color.G, style.Color.B);
                canvas.SetTextMatrix(1, 0, 0, 1, runX, baseline);
                TextEmitter.Show(canvas, style, drawn[z][r]);
                canvas.EndText();
                runX += drawnWidths[z][r];
            }
        }

        if (ctx.Tagged) canvas.EndMarkedContent();
    }
}
