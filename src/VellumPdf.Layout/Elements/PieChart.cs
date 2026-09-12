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
    /// <remarks>
    /// <b>An empty list is refused, and so is a list whose values sum to zero or less.</b>
    /// Laying the chart out throws <see cref="ArgumentException"/> with
    /// <c>ParamName</c> of <c>Slices</c>: there is no angle to give any slice, so there is
    /// nothing to draw rather than something small.
    /// <para><b>A slice value that is negative or not finite is refused</b> by the same
    /// exception, because a negative sweep would paint over its neighbours and a non-finite one
    /// has no angle at all.</para>
    /// <para><b>Do not rely on a zero value being visible.</b> A zero is accepted and
    /// contributes no angle, so the slice is absent from the chart while remaining in this list;
    /// nothing reports that it was dropped.</para>
    /// </remarks>
    public IReadOnlyList<PieSlice> Slices { get; init; } = [];

    /// <summary>
    /// The chart diameter in points. Defaults to 200. When it exceeds the width available at
    /// layout time, the chart is placed at that width instead, so this is an upper bound on the
    /// drawn size rather than a guaranteed one.
    /// </summary>
    /// <remarks>
    /// <b>Zero, a negative value and a non-finite value are all refused.</b> Laying the chart
    /// out throws <see cref="ArgumentException"/> naming <c>Diameter</c>.
    /// <para><b>Do not size this against the page.</b> A diameter wider than the content box is
    /// accepted and clamped to the area the layout pass was handed, so the chart drawn is
    /// smaller than the number set here and nothing reports the difference.</para>
    /// </remarks>
    public double Diameter { get; init; } = 200;

    /// <summary>Margins around the chart. Defaults to 6 points on all sides.</summary>
    public EdgeInsets Margins { get; init; } = new EdgeInsets(6);

    /// <summary>
    /// Optional colour of the separator stroke drawn around each wedge.
    /// When <c>null</c> (the default) no stroke is drawn.
    /// </summary>
    public ColorRgb? StrokeColor { get; init; }

    /// <summary>Width of the separator stroke in points. Defaults to 0.5.</summary>
    /// <remarks>
    /// <b>A negative width and a non-finite width are both refused.</b> Laying the chart out
    /// throws <see cref="ArgumentException"/> naming <c>StrokeWidth</c>.
    /// <para><b>Do not use zero to remove the separators.</b> Zero is accepted, but the stroke
    /// is skipped on <see cref="StrokeColor"/> being unset, not on this being zero, so a zero
    /// width with a stroke colour still strokes: it emits <c>0 w</c>, which asks the device for
    /// the thinnest line it can draw, and that grows heavier as the page is scaled down. Leave
    /// <see cref="StrokeColor"/> unset instead.</para>
    /// </remarks>
    public double StrokeWidth { get; init; } = 0.5;

    /// <summary>Horizontal placement of the chart within the content area. Defaults to centre.</summary>
    /// <remarks>
    /// <b>Do not pass <see cref="HorizontalAlignment.Justify"/>.</b> It is not refused and it is
    /// not honoured: it falls through to left alignment. This is worse here than elsewhere
    /// because the default is <see cref="HorizontalAlignment.Center"/>, so asking for justify
    /// silently loses the centring the chart had.
    /// </remarks>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Center;

    /// <summary>
    /// The angle of the first slice's leading edge, in radians, measured counter-clockwise
    /// from the +X axis in PDF space (Y-up). The default <c>π/2</c> starts at the top
    /// (12 o'clock).
    /// </summary>
    /// <remarks>
    /// <b>A non-finite angle is refused.</b> Laying the chart out throws
    /// <see cref="ArgumentException"/> naming <c>StartAngle</c>.
    /// <para>Radians, not degrees, and no range is imposed: a value outside 0 to 2π is accepted
    /// and wraps, so there is no need to normalise one. The default of π/2 starts at the top.</para>
    /// </remarks>
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
