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
    /// An empty list is refused. So is a list whose values sum to zero or less. Laying the
    /// chart out throws <see cref="ArgumentException"/> with <c>ParamName</c> of <c>Slices</c>.
    /// In both cases there is no angle to give any slice, so the result would be nothing rather
    /// than something small.
    /// <para>A negative or non-finite slice value is refused by the same exception. A negative
    /// sweep would paint over its neighbours, and a non-finite one has no angle at all.</para>
    /// <para><b>Attention</b>: a value of zero is accepted and contributes no angle. The slice stays in
    /// this list and is absent from the chart, and nothing reports that it was dropped. If a zero
    /// slice should be visible in your chart, give it a small positive value.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside <see cref="Document.Save(System.IO.Stream)"/>,
    /// when the list is empty, when a value is negative or not finite, or when the values sum to zero or less. <c>ParamName</c> is this property's name.
    /// </exception>
    public IReadOnlyList<PieSlice> Slices { get; init; } = [];

    /// <summary>
    /// The chart diameter in points. Defaults to 200. When it exceeds the width available at
    /// layout time, the chart is placed at that width instead, so this is an upper bound on the
    /// drawn size rather than a guaranteed one.
    /// </summary>
    /// <remarks>
    /// Zero, a negative value and a non-finite value are all refused. Laying the chart out
    /// throws <see cref="ArgumentException"/> and names <c>Diameter</c>.
    /// <para><b>Attention</b>: a diameter wider than the content box is clamped to it, and the chart
    /// drawn is then smaller than the number you set, with nothing reporting the difference. The
    /// clamp compares only against the available width, not the available height, so a diameter
    /// that fits once clamped can still be taller than the page has room for; that case throws
    /// <see cref="InvalidOperationException"/> instead of drawing a smaller chart. Do not size a
    /// chart against the page; size it against the space you have given it.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside <see cref="Document.Save(System.IO.Stream)"/>,
    /// when the diameter is zero, negative or not finite. <c>ParamName</c> is this property's name.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> when the diameter, after being
    /// clamped to the available width, is still taller than the available height leaves room for.
    /// </exception>
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
    /// A negative width and a non-finite width are both refused. Laying the chart out throws
    /// <see cref="ArgumentException"/> and names <c>StrokeWidth</c>.
    /// <para><b>Attention</b>: zero does <b>not</b> remove the separators. Whether a stroke happens at
    /// all is decided by <see cref="StrokeColor"/>, not by this width. With a stroke colour set
    /// and a width of zero, the renderer emits <c>0 w</c> and still strokes, which asks the device
    /// for its thinnest line. If you want no separators, leave <see cref="StrokeColor"/>
    /// unset.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside <see cref="Document.Save(System.IO.Stream)"/>,
    /// when the width is negative or not finite. <c>ParamName</c> is this property's name.
    /// </exception>
    public double StrokeWidth { get; init; } = 0.5;

    /// <summary>Horizontal placement of the chart within the content area. Defaults to centre.</summary>
    /// <remarks>
    /// <b>Attention</b>: <see cref="HorizontalAlignment.Justify"/> is neither refused <b>nor</b>
    /// honoured. It falls through to left alignment. That costs more here than elsewhere, because
    /// the default is <see cref="HorizontalAlignment.Center"/>: asking for justify loses the
    /// centring you already had, and nothing reports it.
    /// </remarks>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Center;

    /// <summary>
    /// The angle of the first slice's leading edge, in radians, measured counter-clockwise
    /// from the +X axis in PDF space (Y-up). The default <c>π/2</c> starts at the top
    /// (12 o'clock).
    /// </summary>
    /// <remarks>
    /// A non-finite angle is refused. Laying the chart out throws
    /// <see cref="ArgumentException"/> and names <c>StartAngle</c>.
    /// <para>The unit is radians, not degrees. No range is imposed: a value outside 0 to 2π is
    /// accepted and wraps, so you do not have to normalise one yourself. The default of π/2
    /// starts the first slice at the top.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside <see cref="Document.Save(System.IO.Stream)"/>,
    /// when the angle is not finite. <c>ParamName</c> is this property's name.
    /// </exception>
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
