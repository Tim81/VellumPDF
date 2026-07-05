// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// Shared sizing logic for every barcode: resolves the effective module size from
/// <see cref="Barcode.ModuleSize"/>/<see cref="Barcode.TargetWidth"/>, and computes a 1D
/// symbol's footprint (quiet zones, HRI text band, bearer bars) for <see cref="Barcode.Measure"/>.
/// </summary>
internal static class BarcodeGeometry
{
    /// <summary>The default module size, in points, for 1D symbologies when neither <see cref="Barcode.ModuleSize"/> nor <see cref="Barcode.TargetWidth"/> is set.</summary>
    internal const double Default1DModuleSize = 1.0;

    /// <summary>
    /// Resolves the effective module size from the mutually exclusive
    /// <paramref name="moduleSize"/>/<paramref name="targetWidth"/> options.
    /// </summary>
    /// <param name="moduleSize">An explicit module size, in points.</param>
    /// <param name="targetWidth">A desired overall rendered width, in points, to derive the module size from.</param>
    /// <param name="totalModuleWidth">The symbol's total width, in module units, that <paramref name="targetWidth"/> is spread over.</param>
    /// <param name="defaultModuleSize">The module size to use when neither option is set.</param>
    /// <exception cref="ArgumentException">Both options are set, or the set option is not a positive finite number.</exception>
    internal static double ResolveModuleSize(double? moduleSize, double? targetWidth, double totalModuleWidth, double defaultModuleSize)
    {
        if (moduleSize is not null && targetWidth is not null)
            throw new ArgumentException("Set either ModuleSize or TargetWidth, not both.", nameof(moduleSize));

        if (moduleSize is { } explicitSize)
        {
            if (!double.IsFinite(explicitSize) || explicitSize <= 0)
                throw new ArgumentException($"ModuleSize must be a positive finite number (was {explicitSize}).", nameof(moduleSize));
            return explicitSize;
        }

        if (targetWidth is { } width)
        {
            if (!double.IsFinite(width) || width <= 0)
                throw new ArgumentException($"TargetWidth must be a positive finite number (was {width}).", nameof(targetWidth));
            return width / totalModuleWidth;
        }

        return defaultModuleSize;
    }

    /// <summary>
    /// Measures a 1D barcode's footprint: the module run widths scaled to the resolved module
    /// size, plus quiet zones (when included), the HRI text band, and any bearer bars.
    /// </summary>
    internal static BarcodeSize Measure1D(Barcode1D barcode, Encoded1D encoded)
    {
        if (!double.IsFinite(barcode.BarHeight) || barcode.BarHeight <= 0)
            throw new ArgumentException($"BarHeight must be a positive finite number (was {barcode.BarHeight}).", nameof(barcode));
        if (!double.IsFinite(barcode.TextSize) || barcode.TextSize < 0)
            throw new ArgumentException($"TextSize must be a non-negative finite number (was {barcode.TextSize}).", nameof(barcode));

        var totalModules = barcode.IncludeQuietZone ? encoded.TotalModuleWidth : SumRuns(encoded.Runs);
        var moduleSize = ResolveModuleSize(barcode.ModuleSize, barcode.TargetWidth, totalModules, Default1DModuleSize);

        var bearerWidthModules = 0.0;
        var bearerHeightModules = 0.0;
        if (encoded.Bearer is { Style: not ItfBearerBarStyle.None } bearer)
        {
            bearerHeightModules = bearer.ThicknessModules * 2; // top + bottom
            if (bearer.Style == ItfBearerBarStyle.Frame)
                bearerWidthModules = bearer.ThicknessModules * 2; // left + right
        }

        var width = ((totalModules + bearerWidthModules) * moduleSize) + barcode.Margins.Horizontal;

        var textSize = barcode.TextSize > 0 ? barcode.TextSize : DefaultTextSize(barcode.BarHeight);
        var belowBandHeight = barcode.ShowText ? textSize * 1.1 : 0;
        var aboveBandHeight = barcode.ShowText && HasAboveGroup(encoded.HriGroups) ? textSize * 1.1 : 0;

        var height = barcode.BarHeight + belowBandHeight + aboveBandHeight
                     + (bearerHeightModules * moduleSize) + barcode.Margins.Vertical;

        return new BarcodeSize(width, height);
    }

    private static double SumRuns(IReadOnlyList<double> runs)
    {
        var total = 0.0;
        foreach (var run in runs) total += run;
        return total;
    }

    private static bool HasAboveGroup(IReadOnlyList<HriGroup> groups)
    {
        foreach (var group in groups)
            if (group.Anchor == HriAnchor.Above)
                return true;
        return false;
    }

    private static double DefaultTextSize(double barHeight) => Math.Clamp(barHeight * 0.2, 6, 12);
}
