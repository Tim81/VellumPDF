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

    // A rule lies inside the band: its centre is half its width from the band's edge, so a zoned band's
    // reserved height holds it. A string band's height is not checked against its rule, which can be
    // wider than the band. The state is saved because a stroke colour and a line width otherwise stay
    // set for what the page draws next.
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
        long pageLength = 0;
        foreach (var zoneTexts in cache.Texts)
            foreach (var text in zoneTexts)
            {
                text.Reset(pageNumber, totalPages, label, chapter);
                pageLength += text.Length;
            }

        // Each run is under int.MaxValue, which Reset checks, and the report counts the runs of all
        // three zones together as an int.
        if (pageLength > int.MaxValue)
            throw new InvalidOperationException(
                $"The {kind.ToString().ToLowerInvariant()} band's text is too long: its zones resolve to " +
                $"{pageLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters on page " +
                $"{pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}, and no report could count them.");

        var zones = new[] { band.Left!, band.Center!, band.Right! };
        var (leftX, rightX) = BandEdges(pageNumber);
        var contentWidth = _pageSize.Width - _margins.Horizontal;
        var centerX = leftX + contentWidth / 2;
        var gap = band.ZoneGap;
        var swap = band.MirrorZones && MirrorMargins && !IsRectoNumber(pageNumber);

        // The part of the band the rule leaves: the text box and the logos are drawn in it.
        var ruleAbove = band.Rule is { Position: BandRulePosition.Above };
        var regionTop = bandY + (ruleAbove ? band.RuleClearance : 0);
        var regionHeight = bandHeight - band.RuleClearance;

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

        // A zone has a place whatever its text measures: a zone of zero width, in a face whose glyphs
        // measure zero, stands at its side's edge instead of at x = 0.
        void Place(int zone, double width)
        {
            switch (SlotOf(zone))
            {
                case BandSlot.Left:
                    x[zone] = leftX;
                    if (width > 0) leftEnd = leftX + width;
                    break;
                case BandSlot.Right:
                    x[zone] = rightX - width;
                    if (width > 0) rightStart = x[zone];
                    break;
                default:
                    x[zone] = centerX - width / 2;
                    if (width > 0)
                    {
                        centerStart = x[zone];
                        centerEnd = x[zone] + width;
                    }

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

            var texts = cache.Texts[z];
            (drawn[z], drawnWidths[z]) = FitZone(runs, texts, allowed);
            var total = 0.0;
            for (var r = 0; r < runs.Count; r++)
            {
                BandCharactersProduced += texts[r].Produced;
                resolvedCharacters += texts[r].Length;
                drawnCharacters += drawn[z][r].Length;
                total += drawnWidths[z][r];
            }

            Place(z, total);
        }

        if (drawnCharacters < resolvedCharacters)
            RecordTruncation(kind, pageNumber, checked((int)drawnCharacters), checked((int)resolvedCharacters));

        var anyText = drawn.Any(zone => zone is not null && zone.Any(s => s.Length > 0));
        var anyLogo = zones.Any(zone => zone.Image is not null);
        if (!anyText && !anyLogo && band.Rule is null) return;

        // One baseline for the band: the text box, the largest ascent above it and the largest descent
        // below it, centred in the part of the band the rule leaves.
        var (ascent, descent) = band.TextExtent;
        var baseline = ctx.ToPdfY(regionTop + (regionHeight - (ascent + descent)) / 2 + ascent);

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
                    .Concat(band.LogoWidth(z), 0, 0, regionHeight, x[z], ctx.ToPdfY(regionTop + regionHeight))
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

    /// <summary>
    /// Cuts the runs of one zone to <paramref name="allowed"/> points as one text, and returns what
    /// each run draws and how wide it is.
    /// </summary>
    /// <remarks>
    /// The cut falls before the last space that is literal text, in any run, counting only the first
    /// space of a run of spaces and no space at the very start, as <see cref="FitToWidth"/> cuts a
    /// string band. A boundary between two runs is a cut only where a space stands. A token's value is
    /// never cut: spaces inside it are not
    /// cut points, and a cut that cannot use a space backs off to the start of the token. When no
    /// space qualifies, the cut falls at the last position that fits and is not inside a token, so a
    /// zone of one long word is cut as a string band cuts it. When nothing fits, every run is empty.
    /// The work follows the characters that fit, as in <see cref="FitToWidth"/>.
    /// </remarks>
    private static (string[] Drawn, double[] Widths) FitZone(
        IReadOnlyList<TextRun> runs, BandText[] texts, double allowed)
    {
        var count = runs.Count;
        var width = 0.0;
        var seen = 0;
        var prevSpace = false;
        var overflow = false;

        // Where to cut if the zone overflows: before the last qualifying space, else at the last
        // position that is not inside a token. A position is a run and an offset in it.
        var haveBoundary = false;
        int boundaryRun = 0, boundaryAt = 0, safeRun = 0, safeAt = 0;

        for (var r = 0; r < count && !overflow; r++)
        {
            var text = texts[r];
            var style = runs[r].Style;
            var segment = 0;
            var segmentStart = 0;
            var i = 0;
            while (i < text.Length)
            {
                // The segment that holds character i, advanced as i grows. A segment of no length (a
                // token with an empty value) is passed.
                (int Kind, int Length) seg;
                while (true)
                {
                    seg = text.SegmentAt(segment);
                    if (i < segmentStart + seg.Length) break;
                    segmentStart += seg.Length;
                    segment++;
                }

                var insideToken = seg.Kind >= 0;
                if (!(insideToken && i > segmentStart))
                    (safeRun, safeAt) = (r, i);

                var step = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                var piece = style.MeasureString(text.Slice(i, step));
                if (width + piece > allowed)
                {
                    overflow = true;
                    break;
                }

                if (!insideToken && step == 1 && text[i] == ' ')
                {
                    if (!prevSpace && seen > 0)
                    {
                        haveBoundary = true;
                        (boundaryRun, boundaryAt) = (r, i);
                    }

                    prevSpace = true;
                }
                else
                    prevSpace = false;

                width += piece;
                seen += step;
                i += step;
            }
        }

        var cutRun = count;
        var cutAt = 0;
        if (overflow)
            (cutRun, cutAt) = haveBoundary ? (boundaryRun, boundaryAt) : (safeRun, safeAt);

        var drawn = new string[count];
        var widths = new double[count];
        for (var r = 0; r < count; r++)
        {
            drawn[r] = r < cutRun ? texts[r].Prefix(texts[r].Length) : r == cutRun ? texts[r].Prefix(cutAt) : "";
            if (drawn[r].Length > 0) widths[r] = runs[r].Style.MeasureString(drawn[r]);
        }

        return (drawn, widths);
    }
}
