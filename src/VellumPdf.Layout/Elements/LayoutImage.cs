// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>An image element that can be placed in document flow.</summary>
public sealed class LayoutImage
{
    /// <summary>The image to draw.</summary>
    public PdfImageXObject Image { get; }

    /// <summary>
    /// Display width in points; when null the image fits the available width. Must be at least
    /// 5e-6 points in magnitude.
    /// </summary>
    /// <remarks>
    /// A value wider than the available width is clamped to it, so this is an upper bound rather
    /// than a guaranteed display size. Positive infinity is therefore accepted and simply fills the
    /// content box.
    /// <para>Zero and non-finite values other than positive infinity are <b>refused</b>.
    /// <see cref="Document.Save(System.IO.Stream)"/> throws <see cref="InvalidOperationException"/>
    /// naming the width. A zero width writes a transformation matrix that cannot be inverted, which
    /// ISO 32000-2 leaves undefined for a painted image, and it also collapses the height, which is
    /// derived from the width. A width of NaN writes a token that is not a PDF number.</para>
    /// <para>Do <b>not</b> pass a negative width. It is not refused today and mirrors the image
    /// horizontally, which is a side effect of the transformation matrix rather than a supported
    /// way to flip an image. A later major version will reject it.</para>
    /// <para>Attention: the check is on the token written, not on the value held. <c>PdfCanvas</c>
    /// formats coordinates to five decimals, so any magnitude below 5e-6 is written as <c>0</c>
    /// and the matrix is singular whatever you passed. At the boundary, 5e-6 writes
    /// <c>0.00001</c> and 4.9e-6 writes <c>0</c>.</para>
    /// <para>The width is validated before the height, so a width under the boundary is always
    /// what the message names, whatever the source image's aspect ratio. Above the boundary it is
    /// the other way round: a width that is legal but small collapses the derived height of a
    /// non-square image, and then the height is named. A width of 5e-6 on a 100 by 1 source
    /// reports a height of 0.0000001.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property, for zero, for a magnitude under 5e-6, and for NaN or negative infinity. Positive infinity is clamped to the content box instead and does not throw.
    /// </exception>
    public double? Width { get; init; }  // null = fit to available width

    /// <summary>
    /// Display height in points; when null the aspect ratio is maintained. Must be finite and at
    /// least 5e-6 points in magnitude.
    /// </summary>
    /// <remarks>
    /// Setting this is a request for a non-proportional box: unlike a null height, it is not
    /// rescaled when the width is clamped to the content box. An image taller than the remaining
    /// space moves to the next page.
    /// <para>Zero and non-finite values are <b>refused</b>, for the same reason as
    /// <see cref="Width"/>: the emitted transformation matrix is either singular or not made of PDF
    /// numbers. Do <b>not</b> pass a negative height; it is not refused today and flips the image
    /// vertically as a side effect.</para>
    /// <para>Any magnitude below 5e-6 is refused too. The canvas writes it as the token
    /// <c>0</c>, as described on <see cref="Width"/>.</para>
    /// <para>Setting both this and <see cref="Width"/> overrides the aspect ratio. The image is
    /// not fitted inside the pair, so it is distorted whenever the two disagree with its own
    /// proportions. If you want it fitted, set one and leave the other null.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property, for zero, for a magnitude under 5e-6, and for any non-finite value.
    /// </exception>
    public double? Height { get; init; }  // null = maintain aspect ratio

    /// <summary>Horizontal alignment of the image within the available width.</summary>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>Margins around the image.</summary>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>
    /// Optional alternate text for the PDF /Figure structure element (tagged PDF).
    /// Used as the <c>/Alt</c> entry on the Figure struct elem when tagging is enabled.
    /// If null, a generic fallback "Figure" is used.
    /// </summary>
    public string? AltText { get; init; }

    /// <summary>Creates a flow image element for the given image.</summary>
    public LayoutImage(PdfImageXObject image) => Image = image;
}
