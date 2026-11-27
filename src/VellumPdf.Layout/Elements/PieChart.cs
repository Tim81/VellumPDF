// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A single wedge of a <see cref="PieChart"/>.
/// </summary>
/// <remarks>
/// <see cref="Value"/> must be finite and non-negative when the chart is laid out; zero
/// contributes no angle. See <see cref="PieChart.Slices"/>.
/// </remarks>
public readonly record struct PieSlice
{
    /// <summary>
    /// The slice's magnitude. The wedge angle is this value as a fraction of the sum
    /// of all slice values.
    /// </summary>
    /// <remarks>
    /// Must be finite and non-negative at layout. Zero is accepted and contributes no angle.
    /// See <see cref="PieChart.Slices"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside
    /// <see cref="Document.Save(System.IO.Stream)"/>, when the value is negative or not finite.
    /// <c>ParamName</c> is <c>Slices</c>, the chart's property, not this one.
    /// </exception>
    public double Value { get; init; }

    /// <summary>The fill colour of the wedge.</summary>
    /// <remarks>
    /// Stored as given. Channels are not clamped. A channel that is not finite is refused where it is
    /// written; see <see cref="Document"/>. A slice whose <see cref="Value"/> is zero draws no wedge,
    /// so a non-finite channel on it is not refused. <b>Do not pass</b> a non-finite channel on any slice: 3.0 refuses it.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save, when a channel is <c>NaN</c> or infinite and the slice is drawn. The message
    /// says PDF does not support NaN or Infinity as a real number. A finite channel outside 0 to 1 is
    /// not refused.
    /// </exception>
    public ColorRgb Color { get; init; }

    /// <summary>
    /// Optional label carried with the slice (e.g. for an external legend). It is not drawn on
    /// the chart.
    /// </summary>
    /// <remarks>
    /// In a tagged document, when <see cref="PieChart.AltText"/> is null, the chart's alternate
    /// text is composed from the labels and each slice's share. A slice whose label is null or
    /// empty is left out of that text.
    /// </remarks>
    public string? Label { get; init; }

    /// <summary>Copies the magnitude, colour, and label into the given variables.</summary>
    /// <remarks>Order is Value, Color, Label, the order of the constructor.</remarks>
    public void Deconstruct(out double Value, out ColorRgb Color, out string? Label)
    {
        Value = this.Value;
        Color = this.Color;
        Label = this.Label;
    }

    /// <summary>Creates a slice from a magnitude, fill colour, and optional label.</summary>
    /// <remarks>
    /// Nothing is checked here. The save refuses a negative or non-finite
    /// <paramref name="Value"/>; see <see cref="PieChart.Slices"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside
    /// <see cref="Document.Save(System.IO.Stream)"/>, when <paramref name="Value"/> is negative or
    /// not finite. <c>ParamName</c> is <c>Slices</c>, the chart's property, not this one.
    /// </exception>
    public PieSlice(double Value, ColorRgb Color, string? Label = null)
    {
        this.Value = Value;
        this.Color = Color;
        this.Label = Label;
    }
}

/// <summary>
/// A pie chart drawn as a sequence of filled Bézier-approximated wedges.
/// Atomic: the whole chart is placed on one page or moved to the next; it never splits.
/// </summary>
/// <remarks>
/// An empty slice list, a sum of zero or less, and a negative or non-finite slice value are
/// refused when the chart is laid out, during the save; see <see cref="Slices"/>.
/// <see cref="HorizontalAlignment.Justify"/> is drawn as <see cref="HorizontalAlignment.Left"/>.
/// The chart does not split.
/// </remarks>
public sealed class PieChart
{
    /// <summary>Creates a chart with no slices, 200pt diameter, and default styling.</summary>
    /// <remarks>
    /// A save with no slices throws <see cref="ArgumentException"/>; see <see cref="Slices"/>.
    /// </remarks>
    public PieChart() { }

    /// <summary>The slices, drawn in order. The sum of their values must be positive.</summary>
    /// <remarks>
    /// An empty list is refused. So is a list whose values sum to zero or less. Laying the
    /// chart out throws <see cref="ArgumentException"/> with <c>ParamName</c> of <c>Slices</c>.
    /// In both cases there is no angle to give any slice, so the result would be nothing rather
    /// than something small.
    /// <para>A negative or non-finite slice value is refused by the same exception. A negative
    /// sweep would paint over its neighbours, and a non-finite one has no angle at all.</para>
    /// <para><b>Attention</b>: a value of zero is accepted and contributes no angle. The slice
    /// stays in this list and is absent from the chart, and nothing reports that it was dropped.
    /// If a zero slice should be visible in your chart, give it a small positive value.</para>
    /// <para><b>Finite values whose sum overflows to infinity are accepted, and the chart has no
    /// area.</b> Every slice then has no angle, so the chart is blank, or, with
    /// <see cref="StrokeColor"/> set, every non-zero slice strokes the same radius. A large
    /// sum that stays finite is unaffected, and each wedge still takes its share.</para>
    /// <para><b>Do not pass</b> values whose sum overflows to infinity. The file is valid, so it is not refused before 3.0, which will throw
    /// <see cref="ArgumentException"/> with <c>ParamName</c> <c>Slices</c>.</para>
    /// <para>A null list is refused where it is set.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside
    /// <see cref="Document.Save(System.IO.Stream)"/>, when the list is empty, when a value is
    /// negative or not finite, or when the values sum to zero or less. <c>ParamName</c> is this
    /// property's name.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// The list is <see langword="null"/>. <c>ParamName</c> is <c>value</c>, the name of the
    /// <c>init</c> accessor's parameter.
    /// </exception>
    public IReadOnlyList<PieSlice> Slices
    {
        get => _slices;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _slices = value;
        }
    }

    private IReadOnlyList<PieSlice> _slices = [];

    /// <summary>
    /// The chart diameter in points. Defaults to 200. When it exceeds the width available at
    /// layout time, the chart is placed at that width instead, so this is an upper bound on the
    /// drawn size rather than a guaranteed one.
    /// </summary>
    /// <remarks>
    /// Zero, a negative value and a non-finite value are all refused. Laying the chart out
    /// throws <see cref="ArgumentException"/> and names <c>Diameter</c>.
    /// <para><b>Attention</b>: a diameter wider than the content box is clamped to it, and the
    /// chart drawn is then smaller than the number you set, with nothing reporting the
    /// difference. The clamp compares only against the available width, not the available height,
    /// so a diameter that fits once clamped can still be taller than the page has room for; that
    /// case throws <see cref="InvalidOperationException"/> instead of drawing a smaller chart. Do
    /// not size a chart against the page; size it against the space you have given it.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside
    /// <see cref="Document.Save(System.IO.Stream)"/>, when the diameter is zero, negative or
    /// not finite. <c>ParamName</c> is this property's name.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> when the diameter, after being
    /// clamped to the available width, is still taller than the available height leaves room for.
    /// </exception>
    public double Diameter { get; init; } = 200;

    /// <summary>Margins around the chart. Defaults to 6 points on all sides.</summary>
    /// <remarks>
    /// A non-finite edge is not checked before the layout; see <see cref="Document"/>. The save throws <see cref="ArgumentException"/> from
    /// <see cref="VellumPdf.Canvas.PdfCanvas"/> only where it would write the value.
    /// <b>Do not pass</b> a
    /// non-finite edge: 3.0 refuses it.
    /// <para><b>Attention</b>: no other edge is checked. The top and bottom edges add to the space the
    /// chart takes on the page. The left and right edges move the chart but do not shrink it: its
    /// diameter is limited by the whole content width, and its position is clamped back inside the
    /// content box. A negative edge can make the save throw an exception about something else, or
    /// move the chart. A negative top or bottom edge can also move the elements placed after
    /// this one.</para>
    /// <para>Do not pass a negative edge. 3.0 will refuse it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the box the edges leave is too small for the element. The message
    /// says the element is too tall to fit on a page and does not name the margins.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when a non-finite edge would put <c>NaN</c> or infinity into the content
    /// stream, or when finite edges whose arithmetic overflows leave a later element at a non-finite
    /// position and the save writes that position outside the content stream. The message says PDF
    /// does not support NaN or Infinity as a real number.
    /// </exception>
    public EdgeInsets Margins { get; init; } = new EdgeInsets(6);

    /// <summary>
    /// Optional colour of the separator stroke drawn around each wedge.
    /// When <c>null</c> (the default) no stroke is drawn.
    /// </summary>
    /// <remarks>Null means no stroke. A colour is stored as given. Channels are not clamped. A
    /// channel that is not finite is refused where it is written; see <see cref="Document"/> and
    /// <see cref="ColorRgb"/>. <b>Do not pass</b> one: 3.0 refuses it.</remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save, when a channel is <c>NaN</c> or infinite and the stroke is drawn. The
    /// message says PDF does not support NaN or Infinity as a real number. A finite channel outside 0
    /// to 1 is not refused.
    /// </exception>
    public ColorRgb? StrokeColor { get; init; }

    /// <summary>Width of the separator stroke in points. Defaults to 0.5.</summary>
    /// <remarks>
    /// A negative width and a non-finite width are both refused. Laying the chart out throws
    /// <see cref="ArgumentException"/> and names <c>StrokeWidth</c>.
    /// <para><b>Attention</b>: zero does <b>not</b> remove the separators. Whether a stroke
    /// happens at all is decided by <see cref="StrokeColor"/>, not by this width. With a stroke
    /// colour set and a width of zero, the renderer emits <c>0 w</c> and still strokes, which
    /// asks the device for its thinnest line. If you want no separators, leave
    /// <see cref="StrokeColor"/> unset.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside
    /// <see cref="Document.Save(System.IO.Stream)"/>, when the width is negative or not finite.
    /// <c>ParamName</c> is this property's name.
    /// </exception>
    public double StrokeWidth { get; init; } = 0.5;

    /// <summary>Horizontal placement of the chart within the content area. Defaults to
    /// centre.</summary>
    /// <remarks>
    /// <b>Attention</b>: <see cref="HorizontalAlignment.Justify"/> is neither refused nor honoured.
    /// It falls through to left alignment, and so does a value the enumeration does not name.
    /// Because the default is <see cref="HorizontalAlignment.Center"/>, asking for justify loses
    /// the centring you already had, and nothing reports it.
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
    /// <para>The unit is radians, not degrees. An angle of 1e12 or more in magnitude is reduced
    /// modulo a turn before any wedge is drawn, so a negative angle stays negative; a smaller angle
    /// is drawn as given.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised while the chart is laid out, which happens inside
    /// <see cref="Document.Save(System.IO.Stream)"/>, when the angle is not finite.
    /// <c>ParamName</c> is this property's name.
    /// </exception>
    public double StartAngle { get; init; } = Math.PI / 2;

    /// <summary>
    /// When <c>true</c> (the default) slices sweep clockwise from <see cref="StartAngle"/>,
    /// matching the conventional pie-chart direction. When <c>false</c> they sweep
    /// counter-clockwise.
    /// </summary>
    /// <remarks>Both values are honoured. There is no refusal.</remarks>
    public bool Clockwise { get; init; } = true;

    /// <summary>
    /// Optional alternate text for the PDF <c>/Figure</c> structure element (tagged PDF).
    /// Used as the <c>/Alt</c> entry when tagging is enabled. When null, a description is
    /// composed from the slice <see cref="PieSlice.Label"/>s and their share of the total;
    /// if no slice has a label, the generic fallback "Pie chart" is used.
    /// Ignored when <see cref="Decorative"/> is <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Null composes the text from the slice labels, or uses <c>Pie chart</c> when no slice has
    /// one. Empty writes an empty <c>/Alt</c>. Ignored when <see cref="Decorative"/> is true.
    /// </remarks>
    public string? AltText { get; init; }

    /// <summary>
    /// When <c>true</c>, the chart is marked as a decorative artifact in tagged output and
    /// omitted from the structure tree, so assistive technology skips it. Use this when the
    /// chart only restates data already available in accessible text nearby (e.g. an adjacent
    /// table), to avoid announcing the same values twice. When <c>false</c> (the default) the
    /// chart is a <c>/Figure</c> carrying <see cref="AltText"/>. No effect on untagged output.
    /// </summary>
    /// <remarks>
    /// No effect on untagged output. True omits the figure from the structure tree.
    /// </remarks>
    public bool Decorative { get; init; }

    private double _spaceAfter;

    /// <summary>
    /// The gap, in points, between this chart and the element the document places after it. The
    /// default, 0, leaves none.
    /// </summary>
    /// <remarks>
    /// <b>A value that is not finite or is below zero is refused.</b> This <c>init</c> accessor
    /// throws <see cref="ArgumentOutOfRangeException"/>, so the exception is raised by the
    /// statement that set the value. The rule, the keep requests, where the gap is read and its
    /// removal in 3.0 are those of <see cref="Paragraph.SpaceAfter"/>. The gap follows the
    /// slot, which already holds the vertical <see cref="Margins"/>, so the two add.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is <c>NaN</c>, infinite or below zero.
    /// </exception>
    public double SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = LayoutValidation.SpaceAfter(value);
    }
}
