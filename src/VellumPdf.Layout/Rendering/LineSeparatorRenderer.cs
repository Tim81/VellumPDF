// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>Renders a <see cref="LineSeparator"/> as a horizontal rule spanning the content width.</summary>
/// <remarks>Does not split. Non-finite width or inset is refused from Layout.</remarks>
public sealed class LineSeparatorRenderer : IRenderer
{
    private readonly LineSeparator _sep;
    private LayoutBox _occupied;

    /// <summary>Creates a renderer for the given line separator.</summary>
    /// <remarks>
    /// A null <paramref name="sep"/> is stored, and <see cref="Layout"/> throws.
    /// <para>Do not pass null. A later major version will throw <see cref="ArgumentNullException"/>
    /// from this constructor.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised later from <see cref="Layout"/>, not from this constructor, when
    /// <paramref name="sep"/> is <see langword="null"/>.
    /// </exception>
    public LineSeparatorRenderer(LineSeparator sep) => _sep = sep;

    /// <summary>Reserves the separator's line width plus margins and reports the occupied region.</summary>
    /// <remarks>
    /// This renderer does not split. It refuses a non-finite line width or inset itself. When the
    /// document calls this method, the refusal reaches you from the save.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="LineSeparator.LineWidth"/> or an edge of <see cref="LineSeparator.Margins"/> is
    /// not finite.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// The separator is <see langword="null"/>.
    /// </exception>
    public LayoutResult Layout(LayoutContext ctx)
    {
        // A non-finite width reaches the stream as "NaN w", and carries into the line's own
        // coordinates as "10 NaN m" and "290 NaN l". Zero is fine: "0 w" asks for the thinnest
        // line the device renders.
        LayoutValidation.ValidateLineWidth(_sep.LineWidth, "A line separator");
        LayoutValidation.ValidateInsets(_sep.Margins, "A line separator");

        var totalHeight = _sep.Margins.Top + _sep.LineWidth + _sep.Margins.Bottom;
        if (ctx.Area.Height < totalHeight) return LayoutResult.Nothing();
        _occupied = ctx.Area.WithHeight(totalHeight);
        return LayoutResult.Full(_occupied);
    }

    /// <summary>Strokes the separator line at the configured colour and width.</summary>
    /// <remarks>See <see cref="IRenderer.Draw"/>.</remarks>
    public void Draw(DrawContext ctx)
    {
        var (x, y, w, h) = ctx.ToPdfRect(_occupied);
        var lineY = y + _sep.Margins.Bottom + _sep.LineWidth / 2;
        if (ctx.Tagged) ctx.Canvas.BeginArtifactMarkedContent();
        ctx.Canvas
            .SetStrokeColorRgb(_sep.Color.R, _sep.Color.G, _sep.Color.B)
            .SetLineWidth(_sep.LineWidth)
            .MoveTo(x, lineY)
            .LineTo(x + w, lineY)
            .Stroke();
        if (ctx.Tagged) ctx.Canvas.EndMarkedContent();
    }
}
