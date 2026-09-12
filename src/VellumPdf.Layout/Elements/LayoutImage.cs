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
    /// Display width in points; when null the image fits the available width. Must be finite and
    /// non-zero.
    /// </summary>
    /// <remarks>
    /// A value wider than the available width is clamped to it, so this is an upper bound rather
    /// than a guaranteed display size. Positive infinity is therefore accepted and simply fills the
    /// content box.
    /// <para><b>Zero and non-finite values other than positive infinity are refused.</b>
    /// <see cref="Document.Save(System.IO.Stream)"/> throws <see cref="InvalidOperationException"/>
    /// naming the width. A zero width writes a transformation matrix that cannot be inverted, which
    /// ISO 32000-2 leaves undefined for a painted image, and it also collapses the height, which is
    /// derived from the width. A width of NaN writes a token that is not a PDF number.</para>
    /// <para><b>Do not pass a negative width.</b> It is not refused today and mirrors the image
    /// horizontally, which is a side effect of the transformation matrix rather than a supported
    /// way to flip an image. A later major version will reject it.</para>
    /// <para><b>The zero check is on the written token, not the value.</b>
    /// <c>PdfCanvas</c> formats coordinates to five decimals, so anything under 5e-6 in magnitude
    /// is written as <c>0</c> and the matrix is singular whatever the value held. Measured at the
    /// boundary: 5e-6 writes <c>0.00001</c>, 4.9e-6 writes <c>0</c>. At that boundary the message
    /// names the <i>height</i>, not the width, because the height is derived from the width and
    /// collapses first.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property, for zero, for a magnitude under 5e-6, and for NaN or negative infinity. Positive infinity is clamped to the content box instead and does not throw.
    /// </exception>
    public double? Width { get; init; }  // null = fit to available width

    /// <summary>
    /// Display height in points; when null the aspect ratio is maintained. Must be finite and
    /// non-zero.
    /// </summary>
    /// <remarks>
    /// Setting this is a request for a non-proportional box: unlike a null height, it is not
    /// rescaled when the width is clamped to the content box. An image taller than the remaining
    /// space moves to the next page.
    /// <para><b>Zero and non-finite values are refused</b>, for the same reason as
    /// <see cref="Width"/>: the emitted transformation matrix is either singular or not made of PDF
    /// numbers. <b>Do not pass a negative height</b>; it is not refused today and flips the image
    /// vertically as a side effect.</para>
    /// <para>Anything under 5e-6 in magnitude is refused too, for the reason given on
    /// <see cref="Width"/>: the canvas writes it as the token <c>0</c>.</para>
    /// <para>Setting both this and <see cref="Width"/> overrides the aspect ratio rather than
    /// fitting inside the pair, so the image is distorted if the two disagree with it.</para>
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
