// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Fonts;

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// Draws any <see cref="Barcode"/> onto a <see cref="PdfCanvas"/> as vector rectangles. The sole
/// rendering implementation shared by <see cref="BarcodeCanvasExtensions.DrawBarcode"/> and
/// <see cref="BarcodeRenderer"/>, so the low-level and flow-layout APIs always draw identically.
///
/// <para>
/// PDF space is Y-up; (<c>x</c>, <c>y</c>) is the symbol's lower-left corner, including the quiet
/// zone when <see cref="Barcode.IncludeQuietZone"/> is <c>true</c>. For matrix symbologies the
/// module grid's row 0 (top, per <see cref="BarcodeMatrix"/>) renders at the top of the footprint.
/// </para>
/// </summary>
internal static class BarcodePainter
{
    /// <summary>
    /// How far, in modules, a flagged 1D guard/start/stop pattern (<see cref="GuardExtension"/>)
    /// extends below the ordinary data bars — the classic taller guard-bar look, without
    /// reaching so far that it collides with the human-readable text baseline.
    /// </summary>
    private const double GuardExtensionModules = 5;

    /// <summary>The horizontal clearance, in modules, between a symbol's guard bars and its <see cref="HriAnchor.OutsideLeft"/>/<see cref="HriAnchor.OutsideRight"/> text.</summary>
    private const double OutsideHriGapModules = 1;

    /// <summary>The vertical offset, in text-size units, of an HRI baseline from the bottom edge of its band (leaving room for descenders).</summary>
    private const double HriBaselineInset = 0.2;

    /// <summary>
    /// Draws <paramref name="barcode"/> with its footprint's lower-left corner at
    /// (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The barcode's sizing or 1D-specific options are invalid.</exception>
    /// <exception cref="InvalidOperationException">The barcode is 1D, shows human-readable text, and <paramref name="textFont"/> is null.</exception>
    internal static void Draw(PdfCanvas canvas, Barcode barcode, double x, double y, PdfFontResource? textFont)
    {
        barcode.Measure(); // validates sizing/1D options and forces encoding, shared with both callers

        if (barcode.GetEncoded1D() is { } encoded1D)
        {
            var barcode1D = (Barcode1D)barcode;
            if (barcode1D.ShowText && textFont is null)
                throw new InvalidOperationException(
                    $"{barcode.GetType().Name} has ShowText enabled but no text font was supplied; " +
                    "pass a PdfFontResource for the human-readable text, or set ShowText to false.");

            Draw1D(canvas, barcode1D, encoded1D, x, y, textFont);
        }
        else
        {
            Draw2D(canvas, barcode, barcode.GetEncoded2D()!, x, y);
        }
    }

    // ── 1D (linear) symbologies ─────────────────────────────────────────────

    private static void Draw1D(PdfCanvas canvas, Barcode1D barcode, Encoded1D encoded, double x, double y, PdfFontResource? textFont)
    {
        var moduleSize = BarcodeGeometry.ResolveModuleSize1D(barcode, encoded);

        var bearer = encoded.Bearer is { Style: not ItfBearerBarStyle.None } b ? b : (BearerSpec?)null;
        var bearerThicknessModules = bearer is { } bs ? bs.ThicknessModules : 0;
        var bearerWidthModules = bearer is { Style: ItfBearerBarStyle.Frame } fb ? fb.ThicknessModules * 2 : 0;
        var bearerThickness = bearerThicknessModules * moduleSize;
        var bearerInsetX = bearerWidthModules > 0 ? bearerThickness : 0;

        var textSize = barcode.TextSize > 0 ? barcode.TextSize : BarcodeGeometry.DefaultTextSize(barcode.BarHeight);
        var belowBandHeight = barcode.ShowText ? textSize * 1.1 : 0;
        var aboveBandHeight = barcode.ShowText && BarcodeGeometry.HasAboveGroup(encoded.HriGroups) ? textSize * 1.1 : 0;

        var quietLeft = barcode.IncludeQuietZone ? encoded.QuietZoneLeft : 0;
        var quietRight = barcode.IncludeQuietZone ? encoded.QuietZoneRight : 0;
        var totalModules = quietLeft + BarcodeGeometry.SumRuns(encoded.Runs) + quietRight;

        var footprintWidth = (totalModules + bearerWidthModules) * moduleSize;
        var footprintHeight = barcode.BarHeight + belowBandHeight + aboveBandHeight + (2 * bearerThickness);

        var dataOriginX = x + bearerInsetX + (quietLeft * moduleSize);
        var belowBandBottomY = y + bearerThickness;
        var barsBottomY = belowBandBottomY + belowBandHeight;
        var barsTopY = barsBottomY + barcode.BarHeight;

        canvas.SaveState();

        if (barcode.Background is { } background)
        {
            canvas.SetFillColorRgb(background.R, background.G, background.B);
            canvas.Rectangle(x, y, footprintWidth, footprintHeight);
            canvas.Fill();
        }

        canvas.SetFillColorRgb(barcode.Foreground.R, barcode.Foreground.G, barcode.Foreground.B);

        var moduleOffset = 0.0;
        for (var i = 0; i < encoded.Runs.Count; i++)
        {
            var width = encoded.Runs[i];
            if (i % 2 == 0) // even index = bar, per Encoded1D.Runs' "starts with a bar" contract
            {
                var extended = OverlapsGuard(moduleOffset, width, encoded.GuardExtensions);
                var barBottom = extended
                    ? Math.Max(belowBandBottomY, barsBottomY - (GuardExtensionModules * moduleSize))
                    : barsBottomY;
                canvas.Rectangle(dataOriginX + (moduleOffset * moduleSize), barBottom, width * moduleSize, barsTopY - barBottom);
            }

            moduleOffset += width;
        }

        if (bearer is { } bearerSpec)
            DrawBearer(canvas, bearerSpec, x, y, footprintWidth, footprintHeight, bearerThickness);

        canvas.Fill();

        if (barcode.ShowText && textFont is not null)
        {
            canvas.BeginText();
            canvas.SetFont(textFont, textSize);
            foreach (var group in encoded.HriGroups)
            {
                var (tx, ty, align) = HriPosition(group, dataOriginX, moduleSize, belowBandBottomY, barsTopY, textSize);
                canvas.ShowTextAligned(group.Text, tx, ty, align);
            }

            canvas.EndText();
        }

        canvas.RestoreState();
    }

    private static void DrawBearer(PdfCanvas canvas, BearerSpec bearer, double x, double y, double footprintWidth, double footprintHeight, double thickness)
    {
        canvas.Rectangle(x, y, footprintWidth, thickness); // bottom
        canvas.Rectangle(x, y + footprintHeight - thickness, footprintWidth, thickness); // top

        if (bearer.Style == ItfBearerBarStyle.Frame)
        {
            canvas.Rectangle(x, y, thickness, footprintHeight); // left
            canvas.Rectangle(x + footprintWidth - thickness, y, thickness, footprintHeight); // right
        }
    }

    private static bool OverlapsGuard(double moduleOffset, double width, IReadOnlyList<GuardExtension> guards)
    {
        foreach (var guard in guards)
            if (moduleOffset < guard.StartModule + guard.ModuleCount && guard.StartModule < moduleOffset + width)
                return true;
        return false;
    }

    private static (double X, double Y, TextAlignment Align) HriPosition(
        HriGroup group, double dataOriginX, double moduleSize, double belowBandBottomY, double barsTopY, double textSize)
    {
        var baselineInset = textSize * HriBaselineInset;
        var centerX = dataOriginX + ((group.StartModule + (group.ModuleSpan / 2)) * moduleSize);

        return group.Anchor switch
        {
            HriAnchor.Above => (centerX, barsTopY + baselineInset, TextAlignment.Center),
            HriAnchor.OutsideLeft => (dataOriginX + ((group.StartModule - OutsideHriGapModules) * moduleSize), belowBandBottomY + baselineInset, TextAlignment.Right),
            HriAnchor.OutsideRight => (dataOriginX + ((group.StartModule + OutsideHriGapModules) * moduleSize), belowBandBottomY + baselineInset, TextAlignment.Left),
            _ => (centerX, belowBandBottomY + baselineInset, TextAlignment.Center), // HriAnchor.Below
        };
    }

    // ── 2D (matrix) symbologies ──────────────────────────────────────────────

    private static void Draw2D(PdfCanvas canvas, Barcode barcode, Encoded2D encoded, double x, double y)
    {
        var moduleSize = BarcodeGeometry.ResolveModuleSize2D(barcode, encoded);
        var quietZone = barcode.IncludeQuietZone ? encoded.QuietZoneModules : 0;
        var matrix = encoded.Matrix;

        var dataWidth = matrix.Width * moduleSize;
        var rowHeightPts = encoded.RowHeightModules * moduleSize;
        var dataHeight = matrix.Height * rowHeightPts;
        var footprintWidth = dataWidth + (2 * quietZone * moduleSize);
        var footprintHeight = dataHeight + (2 * quietZone * moduleSize);

        var dataOriginX = x + (quietZone * moduleSize);
        var dataOriginY = y + (quietZone * moduleSize);

        canvas.SaveState();

        if (barcode.Background is { } background)
        {
            canvas.SetFillColorRgb(background.R, background.G, background.B);
            canvas.Rectangle(x, y, footprintWidth, footprintHeight);
            canvas.Fill();
        }

        canvas.SetFillColorRgb(barcode.Foreground.R, barcode.Foreground.G, barcode.Foreground.B);

        for (var row = 0; row < matrix.Height; row++)
        {
            var rowTop = dataOriginY + dataHeight - (row * rowHeightPts);
            var rowBottom = rowTop - rowHeightPts;

            var col = 0;
            while (col < matrix.Width)
            {
                if (!matrix.IsDark(col, row)) { col++; continue; }

                var runStart = col;
                do { col++; } while (col < matrix.Width && matrix.IsDark(col, row));

                canvas.Rectangle(dataOriginX + (runStart * moduleSize), rowBottom, (col - runStart) * moduleSize, rowHeightPts);
            }
        }

        canvas.Fill();
        canvas.RestoreState();
    }
}
