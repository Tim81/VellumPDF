// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A single wedge of a <see cref="PieChart"/>.
/// </summary>
/// <param name="Value">
/// The slice's magnitude. The wedge angle is this value as a fraction of the sum
/// of all slice values. Must be finite and non-negative.
/// </param>
/// <param name="Color">The fill colour of the wedge.</param>
/// <param name="Label">
/// Optional label carried with the slice (e.g. for an external legend). Currently
/// stored as data only — it is not rendered as on-chart text.
/// </param>
public readonly record struct PieSlice(double Value, ColorRgb Color, string? Label = null);

/// <summary>
/// A pie chart drawn as a sequence of filled Bézier-approximated wedges.
/// Atomic: the whole chart is placed on one page or moved to the next; it never splits.
/// </summary>
public sealed class PieChart
{
    /// <summary>The slices, drawn in order. The sum of their values must be positive.</summary>
    public IReadOnlyList<PieSlice> Slices { get; init; } = [];

    /// <summary>The chart diameter in points. Defaults to 200.</summary>
    public double Diameter { get; init; } = 200;

    /// <summary>Margins around the chart. Defaults to 6 points on all sides.</summary>
    public EdgeInsets Margins { get; init; } = new EdgeInsets(6);

    /// <summary>
    /// Optional colour of the separator stroke drawn around each wedge.
    /// When <c>null</c> (the default) no stroke is drawn.
    /// </summary>
    public ColorRgb? StrokeColor { get; init; }

    /// <summary>Width of the separator stroke in points. Defaults to 0.5.</summary>
    public double StrokeWidth { get; init; } = 0.5;

    /// <summary>Horizontal placement of the chart within the content area. Defaults to centre.</summary>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Center;

    /// <summary>
    /// The angle of the first slice's leading edge, in radians, measured counter-clockwise
    /// from the +X axis in PDF space (Y-up). The default <c>π/2</c> starts at the top
    /// (12 o'clock).
    /// </summary>
    public double StartAngle { get; init; } = Math.PI / 2;

    /// <summary>
    /// When <c>true</c> (the default) slices sweep clockwise from <see cref="StartAngle"/>,
    /// matching the conventional pie-chart direction. When <c>false</c> they sweep
    /// counter-clockwise.
    /// </summary>
    public bool Clockwise { get; init; } = true;

    /// <summary>
    /// Optional alternate text for the PDF <c>/Figure</c> structure element (tagged PDF).
    /// Used as the <c>/Alt</c> entry when tagging is enabled. When null, a description is
    /// composed from the slice <see cref="PieSlice.Label"/>s and their share of the total;
    /// if no slice has a label, the generic fallback "Pie chart" is used.
    /// Ignored when <see cref="Decorative"/> is <c>true</c>.
    /// </summary>
    public string? AltText { get; init; }

    /// <summary>
    /// When <c>true</c>, the chart is marked as a decorative artifact in tagged output and
    /// omitted from the structure tree, so assistive technology skips it. Use this when the
    /// chart only restates data already available in accessible text nearby (e.g. an adjacent
    /// table), to avoid announcing the same values twice. When <c>false</c> (the default) the
    /// chart is a <c>/Figure</c> carrying <see cref="AltText"/>. No effect on untagged output.
    /// </summary>
    public bool Decorative { get; init; }
}
